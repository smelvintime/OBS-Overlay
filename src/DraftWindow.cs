// The desktop half of the draft board: a Blitz-style companion window that
// appears over the League client while champ select runs and leaves when it
// ends. Same data as the /draft OBS source - DraftWatch's JSON is the one
// contract - drawn natively so it needs no web engine (the WebBrowser
// control is old IE and cannot run the app's pages; WebView2 would be a
// dependency; this app ships as one file).
//
// Owner-drawn on purpose: ten rows of PictureBoxes and Labels is a control
// soup that flickers; one double-buffered Paint is simple and calm. The
// window never takes focus (champ select has a timer; stealing the client's
// keyboard during it would be hostile), sits above other windows, drags by
// grabbing anywhere, and hides via the corner x.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace NowPlaying {

  class DraftWindow : Form {

    // ------------------------------------------------------------- lifecycle
    static DraftWindow _inst;

    public static void Init() {
      if (_inst == null) _inst = new DraftWindow();
    }

    // Callable from any thread (the tray click is UI, the preview route is a
    // server thread) - marshals itself.
    public static void ShowDemo() {
      if (_inst == null) return;
      if (_inst.InvokeRequired) { _inst.BeginInvoke((MethodInvoker)ShowDemo); return; }
      _inst._demo = true;
      _inst.PlaceDefault();
      _inst.ShowNoSteal();
    }

    // ---------------------------------------------------------------- fields
    bool _demo;
    DraftView _view;
    readonly System.Web.Script.Serialization.JavaScriptSerializer _js =
      new System.Web.Script.Serialization.JavaScriptSerializer();
    readonly Dictionary<long, Image> _champImg = new Dictionary<long, Image>();
    readonly Dictionary<long, Image> _spellImg = new Dictionary<long, Image>();
    readonly HashSet<string> _fetching = new HashSet<string>();
    Point _dragAt = Point.Empty;
    bool _dragging;

    static readonly Color BgCol     = Color.FromArgb(18, 18, 20);
    static readonly Color PlateCol  = Color.FromArgb(28, 28, 32);
    static readonly Color LineCol   = Color.FromArgb(48, 48, 54);
    static readonly Color AccentCol = Color.FromArgb(155, 212, 245);
    static readonly Color EnemyCol  = Color.FromArgb(242, 92, 92);
    static readonly Color DimCol    = Color.FromArgb(168, 168, 176);

    const int W = 480;

    DraftWindow() {
      FormBorderStyle = FormBorderStyle.None;
      ShowInTaskbar = false;
      TopMost = true;
      StartPosition = FormStartPosition.Manual;
      BackColor = BgCol;
      ClientSize = new Size(W, 700);
      DoubleBuffered = true;
      var h = Handle;   // force creation so BeginInvoke works before first Show

      // Poll once a second - the same cadence the OBS page uses. The 250ms
      // ticker only repaints while visible, for the countdown.
      var poll = new System.Windows.Forms.Timer { Interval = 1000 };
      poll.Tick += delegate { PollTick(); };
      poll.Start();
      var tick = new System.Windows.Forms.Timer { Interval = 250 };
      tick.Tick += delegate { if (Visible) Invalidate(); };
      tick.Start();

      MouseDown += OnDown; MouseMove += OnMove; MouseUp += delegate { _dragging = false; };
    }

    // Appear without taking the keyboard away from the client.
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
      get {
        var cp = base.CreateParams;
        cp.ExStyle |= 0x08000000 | 0x00000080;   // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
        return cp;
      }
    }

    // TopMost alone is not enough here: a window shown with WS_EX_NOACTIVATE
    // does not reliably get its topmost z-order applied, and "the draft board
    // opened behind the browser" is indistinguishable from it never opening.
    // SetWindowPos with NOACTIVATE pins it above without touching focus.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    void ShowNoSteal() {
      if (!Visible) Show();
      try {
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
      } catch { }
      Invalidate();
    }

    bool _placed;
    void PlaceDefault() {
      if (_placed) return;
      _placed = true;
      var wa = Screen.PrimaryScreen.WorkingArea;
      Location = new Point(wa.Right - Width - 24, wa.Top + (wa.Height - Height) / 2);
    }

    // ------------------------------------------------------------------ poll
    void PollTick() {
      bool wanted = _demo || Program.DraftWindowEnabled;
      if (!wanted) { if (Visible) Hide(); return; }

      DraftView v = null;
      try { v = ParseView(DraftWatch.StateJson(_demo)); } catch { }
      if (v == null || !v.Active) {
        _view = null;
        // A demo stays up until its x is clicked - it exists to be looked at.
        if (Visible && !_demo) Hide();
        if (_demo && v != null) _view = v;
        Invalidate();
        return;
      }
      _view = v;
      // The form hugs its rows: ARAM drafts have benches and no bans, customs
      // can seat fewer than ten - a fixed height is always wrong for someone.
      int rows = Math.Max(v.Mine.Count, v.Theirs.Count);
      int wantH = 102 + rows * 52 + (v.Extras.Length > 0 ? 30 : 10);
      if (ClientSize.Height != wantH) ClientSize = new Size(W, wantH);
      if (!Visible) { PlaceDefault(); ShowNoSteal(); }
      Invalidate();
    }

    // ------------------------------------------------------------- view data
    class Row {
      public long Cell, ChampId, IntentId, Spell1, Spell2;
      public bool Self, Locked;
      public string Champ = "", Intent = "", Pos = "", Player = "", Tag = "", Skin = "";
      public string Spell1Name = "", Spell2Name = "";
    }
    class DraftView {
      public bool Active, Infinite;
      public string TimerPhase = "";
      public long LeftMs, TotalMs;
      public DateTime Anchor;
      public List<long> BansMine = new List<long>();
      public List<long> BansTheirs = new List<long>();
      public int BansNum;
      public List<Row> Mine = new List<Row>();
      public List<Row> Theirs = new List<Row>();
      public string Extras = "";
    }

    DraftView ParseView(string json) {
      var d = _js.DeserializeObject(json) as Dictionary<string, object>;
      if (d == null) return null;
      var v = new DraftView();
      v.Active = Truthy(d, "active");
      if (!v.Active) return v;
      var timer = Get(d, "timer") as Dictionary<string, object>;
      if (timer != null) {
        v.TimerPhase = Str(timer, "phase");
        v.LeftMs = Num(timer, "leftMs");
        v.TotalMs = Num(timer, "totalMs");
        v.Infinite = Truthy(timer, "infinite");
        v.Anchor = DateTime.UtcNow;
      }
      var bans = Get(d, "bans") as Dictionary<string, object>;
      if (bans != null) {
        v.BansNum = (int)Num(bans, "num");
        foreach (var o in Arr(bans, "mine")) v.BansMine.Add(IdOf(o));
        foreach (var o in Arr(bans, "theirs")) v.BansTheirs.Add(IdOf(o));
      }
      foreach (var o in Arr(d, "myTeam")) v.Mine.Add(RowOf(o));
      foreach (var o in Arr(d, "theirTeam")) v.Theirs.Add(RowOf(o));

      var ex = Get(d, "extras") as Dictionary<string, object>;
      if (ex != null) {
        var bits = new List<string>();
        long rerolls = Num(ex, "rerolls");
        if (Truthy(ex, "benchEnabled")) bits.Add("ARAM bench");
        if (rerolls > 0) bits.Add("rerolls " + rerolls);
        if (Truthy(ex, "custom")) bits.Add("custom game");
        if (Truthy(ex, "spectating")) bits.Add("spectating");
        long trades = Num(ex, "trades"), swaps = Num(ex, "swaps");
        if (trades > 0) bits.Add(trades + (trades == 1 ? " trade" : " trades"));
        if (swaps > 0) bits.Add(swaps + (swaps == 1 ? " swap" : " swaps"));
        v.Extras = string.Join("  ·  ", bits.ToArray());
      }
      return v;
    }

    Row RowOf(object o) {
      var r = new Row();
      var d = o as Dictionary<string, object>;
      if (d == null) return r;
      r.Cell = Num(d, "cell");
      r.Self = Truthy(d, "self");
      r.Locked = Truthy(d, "locked");
      var c = Get(d, "champ") as Dictionary<string, object>;
      if (c != null) { r.ChampId = Num(c, "id"); r.Champ = Str(c, "name"); }
      var it = Get(d, "intent") as Dictionary<string, object>;
      if (it != null) { r.IntentId = Num(it, "id"); r.Intent = Str(it, "name"); }
      r.Pos = Str(d, "pos"); r.Player = Str(d, "player"); r.Tag = Str(d, "tag");
      r.Skin = Str(d, "skin");
      var sp = Get(d, "spells") as object[];
      if (sp != null && sp.Length > 0) {
        var s1 = sp[0] as Dictionary<string, object>;
        if (s1 != null) { r.Spell1 = Num(s1, "id"); r.Spell1Name = Str(s1, "name"); }
        if (sp.Length > 1) {
          var s2 = sp[1] as Dictionary<string, object>;
          if (s2 != null) { r.Spell2 = Num(s2, "id"); r.Spell2Name = Str(s2, "name"); }
        }
      }
      return r;
    }

    static object Get(Dictionary<string, object> d, string k) {
      object v; return d.TryGetValue(k, out v) ? v : null;
    }
    static object[] Arr(Dictionary<string, object> d, string k) {
      return (Get(d, k) as object[]) ?? new object[0];
    }
    static string Str(Dictionary<string, object> d, string k) {
      var v = Get(d, k); return v == null ? "" : Convert.ToString(v);
    }
    static long Num(Dictionary<string, object> d, string k) {
      var v = Get(d, k); if (v == null) return 0;
      try { return Convert.ToInt64(v); } catch { return 0; }
    }
    static bool Truthy(Dictionary<string, object> d, string k) {
      var v = Get(d, k); return v is bool && (bool)v;
    }
    static long IdOf(object o) {
      var d = o as Dictionary<string, object>;
      return d == null ? 0 : Num(d, "id");
    }

    // ------------------------------------------------------------------ icons
    // Fetched off the UI thread; the paint draws a placeholder until the
    // bytes land and the repaint after arrival fills it in.
    Image IconOf(Dictionary<long, Image> cache, long id, bool spell) {
      if (id <= 0) return null;
      lock (cache) { Image img; if (cache.TryGetValue(id, out img)) return img; }
      string key = (spell ? "s" : "c") + id;
      lock (_fetching) { if (_fetching.Contains(key)) return null; _fetching.Add(key); }
      ThreadPool.QueueUserWorkItem(delegate {
        try {
          byte[] png = spell ? DraftWatch.SpellIconPng(id) : DraftWatch.ChampIconPng(id);
          if (png != null) {
            var img = Image.FromStream(new MemoryStream(png));
            lock (cache) cache[id] = img;
            try { BeginInvoke((MethodInvoker)delegate { Invalidate(); }); } catch { }
          }
        } catch { }
        finally { lock (_fetching) _fetching.Remove(key); }
      });
      return null;
    }

    // ------------------------------------------------------------------ paint
    protected override void OnPaint(PaintEventArgs e) {
      var g = e.Graphics;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.Clear(BgCol);
      using (var border = new Pen(LineCol)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

      var v = _view;
      using (var f10 = new Font("Segoe UI", 8f))
      using (var f10b = new Font("Segoe UI", 8f, FontStyle.Bold))
      using (var f12b = new Font("Segoe UI", 10f, FontStyle.Bold))
      using (var f16b = new Font("Segoe UI", 14f, FontStyle.Bold)) {

        // header: DRAFT + countdown + x
        using (var b = new SolidBrush(DimCol)) g.DrawString("D R A F T", f10b, b, 16, 14);
        string t = "";
        if (v != null && v.Active) {
          if (v.Infinite) t = "∞";
          else {
            double left = v.LeftMs - (DateTime.UtcNow - v.Anchor).TotalMilliseconds;
            if (left < 0) left = 0;
            t = ((int)Math.Ceiling(left / 1000.0)).ToString();
          }
        }
        using (var b = new SolidBrush(AccentCol))
          g.DrawString(t, f16b, b, Width / 2 - g.MeasureString(t, f16b).Width / 2, 8);
        using (var b = new SolidBrush(DimCol)) {
          string ph = v == null ? "" : PrettyPhase(v.TimerPhase);
          var sz = g.MeasureString(ph, f10);
          g.DrawString(ph, f10, b, Width / 2 - sz.Width / 2, 32);
          g.DrawString("✕", f12b, b, Width - 28, 10);
        }
        // timer bar
        if (v != null && v.Active && v.TotalMs > 0 && !v.Infinite) {
          double left = v.LeftMs - (DateTime.UtcNow - v.Anchor).TotalMilliseconds;
          double frac = Math.Max(0, Math.Min(1, left / v.TotalMs));
          using (var b = new SolidBrush(LineCol)) g.FillRectangle(b, 16, 50, Width - 32, 3);
          using (var b = new SolidBrush(AccentCol)) g.FillRectangle(b, 16, 50, (int)((Width - 32) * frac), 3);
        }

        if (v == null || !v.Active) {
          using (var b = new SolidBrush(DimCol))
            g.DrawString("Waiting for champ select...", f10, b, 16, 70);
          return;
        }

        // bans: mine left, theirs right
        int by = 62;
        DrawBans(g, v.BansMine, v.BansNum, 16, by, false);
        DrawBans(g, v.BansTheirs, v.BansNum, Width - 16 - BansWidth(v.BansNum), by, true);

        // teams
        int top = by + 40;
        int rowH = 52, colW = (Width - 40) / 2;
        DrawTeam(g, v.Mine, 16, top, colW, rowH, AccentCol, f10, f10b, f12b);
        DrawTeam(g, v.Theirs, Width - 16 - colW, top, colW, rowH, EnemyCol, f10, f10b, f12b);

        // extras footer
        if (v.Extras.Length > 0) {
          int fy = top + rowH * Math.Max(v.Mine.Count, v.Theirs.Count) + 10;
          using (var b = new SolidBrush(DimCol)) g.DrawString(v.Extras, f10, b, 16, fy);
        }
      }
    }

    static int BansWidth(int num) { return num * 30 - 4; }

    void DrawBans(Graphics g, List<long> ids, int num, int x, int y, bool enemy) {
      if (num <= 0) num = Math.Max(ids.Count, 5);
      for (int i = 0; i < num; i++) {
        var rect = new Rectangle(x + i * 30, y, 26, 26);
        long id = i < ids.Count ? ids[i] : 0;
        var img = id > 0 ? IconOf(_champImg, id, false) : null;
        if (img != null) {
          g.DrawImage(img, rect);
          using (var p = new Pen(EnemyCol, 2f))
            g.DrawLine(p, rect.Left + 3, rect.Bottom - 3, rect.Right - 3, rect.Top + 3);
        } else {
          using (var b = new SolidBrush(PlateCol)) g.FillRectangle(b, rect);
        }
        using (var p = new Pen(LineCol)) g.DrawRectangle(p, rect);
      }
    }

    void DrawTeam(Graphics g, List<Row> team, int x, int top, int w, int rowH,
                  Color edge, Font f10, Font f10b, Font f12b) {
      for (int i = 0; i < team.Count; i++) {
        var r = team[i];
        int y = top + i * rowH;
        var plate = new Rectangle(x, y, w, rowH - 6);
        using (var b = new SolidBrush(r.Self ? Color.FromArgb(36, 40, 46) : PlateCol))
          g.FillRectangle(b, plate);
        using (var p = new Pen(r.Self ? AccentCol : LineCol, r.Self ? 2f : 1f))
          g.DrawLine(p, plate.Left, plate.Top, plate.Left, plate.Bottom);
        using (var p = new Pen(Color.FromArgb(90, edge)))
          g.DrawRectangle(p, plate);

        // icon: locked champ full, hover dimmed, else empty slot
        var ic = new Rectangle(x + 8, y + 4, 38, 38);
        long iconId = r.ChampId > 0 ? r.ChampId : r.IntentId;
        var img = IconOf(_champImg, iconId, false);
        if (img != null) {
          if (r.ChampId > 0) g.DrawImage(img, ic);
          else {
            // hovering: the intent at half strength
            var ia = new ImageAttributes();
            var cm = new ColorMatrix(); cm.Matrix33 = 0.5f;
            ia.SetColorMatrix(cm);
            g.DrawImage(img, ic, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
          }
        } else {
          using (var b = new SolidBrush(BgCol)) g.FillRectangle(b, ic);
          using (var p = new Pen(LineCol)) g.DrawRectangle(p, ic);
        }

        // text: champion (or hover), then player + pos
        string main = r.ChampId > 0 ? r.Champ : (r.IntentId > 0 ? r.Intent + " ?" : "—");
        using (var b = new SolidBrush(r.ChampId > 0 ? Color.White : DimCol))
          g.DrawString(main, f10b, b, x + 52, y + 5);
        string sub = "";
        if (r.Player.Length > 0) sub = r.Player;
        string pos = PrettyPos(r.Pos);
        if (pos.Length > 0) sub = (sub.Length > 0 ? sub + "  ·  " : "") + pos;
        if (r.Skin.Length > 0) sub = (sub.Length > 0 ? sub + "  ·  " : "") + r.Skin;
        if (sub.Length > 0)
          using (var b = new SolidBrush(DimCol)) {
            g.SetClip(new Rectangle(x + 52, y + 22, w - 96, 18));
            g.DrawString(sub, f10, b, x + 52, y + 22);
            g.ResetClip();
          }

        // spells stacked right
        DrawSpell(g, r.Spell1, new Rectangle(x + w - 26, y + 5, 17, 17));
        DrawSpell(g, r.Spell2, new Rectangle(x + w - 26, y + 24, 17, 17));
      }
    }

    void DrawSpell(Graphics g, long id, Rectangle rect) {
      var img = id > 0 ? IconOf(_spellImg, id, true) : null;
      if (img != null) g.DrawImage(img, rect);
      else { using (var b = new SolidBrush(PlateCol)) g.FillRectangle(b, rect); }
      using (var p = new Pen(LineCol)) g.DrawRectangle(p, rect);
    }

    static string PrettyPhase(string p) {
      if (p == "BAN_PICK") return "Pick & ban";
      if (p == "PLANNING") return "Declaring intent";
      if (p == "FINALIZATION") return "Finalizing";
      if (p == "GAME_STARTING") return "Game starting";
      return p;
    }
    static string PrettyPos(string p) {
      if (p == "top") return "TOP";
      if (p == "jungle") return "JGL";
      if (p == "middle") return "MID";
      if (p == "bottom") return "BOT";
      if (p == "utility") return "SUP";
      return "";
    }

    // ------------------------------------------------------------------ input
    void OnDown(object s, MouseEventArgs e) {
      // the x, top-right
      if (e.Y < 34 && e.X > Width - 40) {
        _demo = false;
        Hide();
        return;
      }
      _dragging = true; _dragAt = e.Location;
    }
    void OnMove(object s, MouseEventArgs e) {
      if (!_dragging) return;
      Location = new Point(Location.X + e.X - _dragAt.X, Location.Y + e.Y - _dragAt.Y);
    }
  }
}
