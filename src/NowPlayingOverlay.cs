// Now Playing overlay - standalone Windows executable.
//
// Serves a local web page showing whatever music is playing on this PC, for use
// as an OBS Browser Source. Reads two sources and picks whichever is actually
// playing:
//
//   1. Windows media session (SMTC) - Spotify, Apple Music, browsers, most apps
//   2. iTunes COM automation        - iTunes does not publish to SMTC at all
//
// Requires nothing installed: it targets the .NET Framework that ships with
// Windows, and the overlay pages are embedded in the binary.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Web.Script.Serialization;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace NowPlaying {

  // Immutable view of "what is playing", swapped in wholesale by the poller so
  // request handlers never see a half-updated state.
  sealed class Snapshot {
    public bool Playing;
    public string Title = "";
    public string Artist = "";
    public string Album = "";
    public string App = "";
    public string Source = "none";
    public string Id = "none";
    public bool HasArt;
    public byte[] Art;
    public string ArtMime = "image/jpeg";
  }

  static class Program {

    // ------------------------------------------------------------------ state
    static volatile Snapshot _current = new Snapshot();
    static int _port = 8787;

    // The chat bot answers !song from the same snapshot the overlay draws, so
    // chat and screen can never disagree about what is playing.
    internal static Snapshot CurrentSnapshot() { return _current; }

    // The media APIs (WinRT session manager, iTunes COM) are single shared
    // objects. The poller touches them every second and /sources can touch them
    // from a request thread, so all provider access is serialised through this.
    static readonly object _mediaLock = new object();

    // Which player the overlay follows.
    //   auto   - whatever is actually playing (default)
    //   prefer - _pinApp wins when it has something, otherwise fall back
    //   only   - nothing but _pinApp ever shows
    static volatile string _mode = "auto";
    static volatile string _pinApp = "";

    static string SettingsPath() {
      string dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NowPlayingOverlay");
      Directory.CreateDirectory(dir);
      return Path.Combine(dir, "settings.txt");
    }

    static void LoadSettings() {
      try {
        string p = SettingsPath();
        if (!File.Exists(p)) return;
        foreach (var line in File.ReadAllLines(p)) {
          int eq = line.IndexOf('=');
          if (eq <= 0) continue;
          string k = line.Substring(0, eq).Trim().ToLowerInvariant();
          string v = line.Substring(eq + 1).Trim();
          if (k == "mode") _mode = NormalizeMode(v);
          else if (k == "app") _pinApp = v;
          else if (k == "bot") TwitchChat.RestoreEnabled(v == "1" || v == "on" || v == "true");
        }
        if (_mode != "auto" && _pinApp.Length == 0) _mode = "auto";
      } catch { }
    }

    internal static void SaveSettings() {
      try {
        File.WriteAllText(SettingsPath(),
          "mode=" + _mode + "\r\napp=" + _pinApp + "\r\n"
          + "bot=" + (TwitchChat.Enabled ? "1" : "0") + "\r\n");
      } catch { }
    }

    // ---- shared preferences ---------------------------------------------------
    // The theme used to live in the browser's localStorage and nowhere else,
    // which quietly meant three different things could disagree. localStorage is
    // per-origin, so a theme set on localhost:8787 is invisible on
    // 127.0.0.1:8787; an OBS browser source is a separate profile that never
    // opens the dashboard, so it never saw a theme at all; and clearing site data
    // silently reset everything. Keeping it here instead gives one answer that
    // every page and every OBS source reads, and it survives a restart.
    static readonly object _prefsLock = new object();
    static Dictionary<string, string> _prefs;

    static string PrefsPath() {
      string dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NowPlayingOverlay");
      Directory.CreateDirectory(dir);
      return Path.Combine(dir, "prefs.txt");
    }

    static Dictionary<string, string> Prefs() {
      lock (_prefsLock) {
        if (_prefs != null) return _prefs;
        _prefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try {
          string p = PrefsPath();
          if (File.Exists(p)) {
            foreach (var line in File.ReadAllLines(p)) {
              int eq = line.IndexOf('=');
              if (eq > 0) _prefs[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
          }
        } catch { }
        return _prefs;
      }
    }

    internal static void SetPref(string key, string value) {
      if (string.IsNullOrEmpty(key)) return;
      // One pref per line, so a stray newline in a value would corrupt every
      // pref after it on the next read.
      value = (value ?? "").Replace("\r", "").Replace("\n", "");
      lock (_prefsLock) {
        var d = Prefs();
        d[key] = value;
        try {
          var sb = new StringBuilder();
          foreach (var kv in d) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
          File.WriteAllText(PrefsPath(), sb.ToString());
        } catch (Exception ex) {
          AppLog.Write("could not save prefs: " + ex.Message);
        }
      }
    }

    static string PrefsJson() {
      lock (_prefsLock) {
        var d = Prefs();
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var kv in d) {
          if (!first) sb.Append(',');
          first = false;
          sb.Append(Q(kv.Key)).Append(':').Append(Q(kv.Value));
        }
        return sb.Append('}').ToString();
      }
    }

    static string NormalizeMode(string m) {
      m = (m ?? "").Trim().ToLowerInvariant();
      return (m == "prefer" || m == "only") ? m : "auto";
    }

    // Pin matches on the app id ("Spotify.exe", "Chrome", "iTunes") or the
    // provider name, so "spotify" and "itunes" both do the obvious thing.
    static bool MatchesPin(Snapshot s, string pin) {
      if (string.IsNullOrEmpty(pin)) return false;
      return PinHit(s.App, pin) || PinHit(s.Source, pin);
    }

    // .NET Framework's AsTask() lives in System.Runtime.WindowsRuntime, which
    // needs the union Windows.winmd from the Windows SDK. Driving the completion
    // handler directly works off the per-namespace winmd files alone, so this
    // builds on a machine with no SDK installed.
    // Must run on an MTA thread: on an STA thread the completion callback would
    // marshal back to the very thread we are blocking, and deadlock.
    static TResult Await<TResult>(IAsyncOperation<TResult> op) {
      var done = new ManualResetEventSlim(false);
      op.Completed = (o, s) => done.Set();
      done.Wait();
      return op.GetResults();
    }

    // ------------------------------------------------------------------ entry
    // Built as a windows app so double-clicking shows no console. When it IS
    // launched from a terminal, attach to that terminal so CLI use still prints.
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);
    static bool _hasConsole;

    static void Say(string s) { if (_hasConsole) Console.WriteLine(s); }
    static void SayColor(string s, ConsoleColor c) {
      if (!_hasConsole) return;
      Console.ForegroundColor = c; Console.WriteLine(s); Console.ResetColor();
    }

    // Startup registration lives in the per-user Run key: no admin needed, and
    // it shows up in Task Manager's Startup tab so it can always be turned off.
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "NowPlayingOverlay";

    static string ExePath() {
      return Assembly.GetExecutingAssembly().Location;
    }

    static bool StartupEnabled() {
      try {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
          return k != null && k.GetValue(RunValueName) != null;
      } catch { return false; }
    }

    // The installer offers "start when I sign in" at install time, before the
    // tray exists to own that switch.
    internal static bool SetStartupPublic(bool on) { return SetStartup(on); }

    static bool SetStartup(bool on) {
      try {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKeyPath)) {
          if (k == null) return false;
          if (on) {
            // Preserve a non-default port so autostart matches how it was run.
            string cmd = "\"" + ExePath() + "\"";
            if (_port != 8787) cmd += " -port " + _port;
            k.SetValue(RunValueName, cmd);
          } else {
            k.DeleteValue(RunValueName, false);
          }
        }
        return true;
      } catch { return false; }
    }

    static void OpenUrl(string suffix) {
      try { System.Diagnostics.Process.Start("http://127.0.0.1:" + _port + suffix); } catch { }
    }

    // Drawn rather than shipped as a .ico file, so the build stays a single
    // source file with no binary assets.
    static Icon MakeTrayIcon() {
      try {
        using (var bmp = new Bitmap(32, 32)) {
          using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            // Matches --accent in the pages (#9BD4F5), so the tray icon and the
            // dashboard are recognisably the same product.
            using (var b = new SolidBrush(Color.FromArgb(0x9B, 0xD4, 0xF5)))
              g.FillEllipse(b, 1, 1, 30, 30);
            // Dark glyph on the bright accent, the same pairing the buttons use.
            // White on #9BD4F5 is barely legible at 32px in a taskbar.
            using (var f = new Font("Segoe UI Symbol", 17, FontStyle.Bold))
            using (var w = new SolidBrush(Color.FromArgb(0x04, 0x12, 0x1F))) {
              var sf = new StringFormat { Alignment = StringAlignment.Center,
                                          LineAlignment = StringAlignment.Center };
              g.DrawString("♫", f, w, new RectangleF(0, 0, 32, 32), sf);
            }
          }
          return Icon.FromHandle(bmp.GetHicon());
        }
      } catch { return SystemIcons.Application; }
    }

    static NotifyIcon _tray;

    static void BuildTray() {
      var menu = new ContextMenuStrip();

      // The version belongs where someone can read it without a terminal - the
      // first question about a machine you cannot reach is which build it runs.
      var header = new ToolStripMenuItem("Now Playing Overlay  v" + BuildInfo.Version) { Enabled = false };
      menu.Items.Add(header);
      menu.Items.Add("Open dashboard...", null, (s, e) => OpenUrl("/app"));
      menu.Items.Add(new ToolStripSeparator());

      // Deep links into the same dashboard rather than the old standalone
      // pages - one address to remember, with these as shortcuts to a tab
      // rather than a separate site each.
      menu.Items.Add("Choose which player to follow...", null, (s, e) => OpenUrl("/app#control"));
      menu.Items.Add("Customize the overlay...", null, (s, e) => OpenUrl("/app#customize"));
      menu.Items.Add("Compare layouts...", null, (s, e) => OpenUrl("/app#layouts"));
      menu.Items.Add("Chat bot...", null, (s, e) => OpenUrl("/app#bot"));
      menu.Items.Add("Preview overlay...", null, (s, e) => OpenUrl("/"));
      menu.Items.Add(new ToolStripSeparator());

      var twitch = new ToolStripMenuItem("Twitch alerts and stats");
      twitch.DropDownItems.Add("Set up Twitch...", null, (s, e) => OpenUrl("/setup"));
      twitch.DropDownItems.Add(new ToolStripSeparator());
      twitch.DropDownItems.Add("Preview alerts...", null, (s, e) => OpenUrl("/alerts"));
      twitch.DropDownItems.Add("Preview follower/sub boxes...", null, (s, e) => OpenUrl("/stats"));
      twitch.DropDownItems.Add(new ToolStripSeparator());
      twitch.DropDownItems.Add("Copy alerts source URL", null, (s, e) => {
        try { Clipboard.SetText("http://127.0.0.1:" + _port + "/alerts"); } catch { }
      });
      twitch.DropDownItems.Add("Copy follower/sub source URL", null, (s, e) => {
        try { Clipboard.SetText("http://127.0.0.1:" + _port + "/stats"); } catch { }
      });
      twitch.DropDownItems.Add(new ToolStripSeparator());
      var twitchStatus = new ToolStripMenuItem("") { Enabled = false };
      twitch.DropDownItems.Add(twitchStatus);
      // Opening the menu is the moment the user cares whether it is working, so
      // the connection state is read then rather than cached at build time.
      twitch.DropDownOpening += (s, e) => {
        string st = TwitchEvents.Status;
        twitchStatus.Text =
            st == "live"          ? "Connected to Twitch"
          : st == "connecting"    ? "Connecting to Twitch..."
          : st == "bad-token"     ? "Token expired - regenerate it"
          : st == "missing-scope" ? "Token is missing a required scope"
          : st == "off"           ? "Not set up yet - see README"
                                  : "Twitch connection problem";
      };
      menu.Items.Add(twitch);
      menu.Items.Add(new ToolStripSeparator());

      var copy = new ToolStripMenuItem("Copy OBS browser source URL");
      copy.Click += (s, e) => {
        try { Clipboard.SetText("http://127.0.0.1:" + _port + "/"); } catch { }
      };
      menu.Items.Add(copy);

      var startup = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
      startup.Checked = StartupEnabled();
      startup.Click += (s, e) => {
        if (!SetStartup(startup.Checked)) {
          startup.Checked = StartupEnabled();
          MessageBox.Show("Could not change the startup setting.", "Now Playing Overlay",
                          MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
      };
      menu.Items.Add(startup);
      menu.Items.Add(new ToolStripSeparator());
      menu.Items.Add("Diagnostics...", null, (s, e) => OpenUrl("/diag"));
      menu.Items.Add("Open log folder", null, (s, e) => {
        try {
          string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NowPlayingOverlay");
          Directory.CreateDirectory(dir);
          System.Diagnostics.Process.Start("explorer.exe", dir);
        } catch { }
      });
      menu.Items.Add(new ToolStripSeparator());
      menu.Items.Add("Exit", null, (s, e) => { Shutdown(); });

      // Keep the tooltip showing whatever is on screen right now.
      var timer = new System.Windows.Forms.Timer { Interval = 2000 };
      timer.Tick += (s, e) => {
        var n = _current;
        string t = (n != null && n.Title.Length > 0)
          ? (n.Title + (n.Artist.Length > 0 ? " - " + n.Artist : ""))
          : "Nothing playing";
        if (t.Length > 60) t = t.Substring(0, 57) + "...";   // NotifyIcon caps at 63
        try { _tray.Text = t; } catch { }
      };
      timer.Start();

      _tray = new NotifyIcon {
        Icon = MakeTrayIcon(),
        Text = "Now Playing Overlay",
        Visible = true,
        ContextMenuStrip = menu
      };
      _tray.DoubleClick += (s, e) => OpenUrl("/app");
    }

    static void Shutdown() {
      AppLog.Write("shutdown requested from tray menu");
      try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
      try { Application.Exit(); } catch { }
      Environment.Exit(0);
    }

    static void Fatal(string message) {
      if (_hasConsole) { SayColor("  " + message, ConsoleColor.Red); }
      else MessageBox.Show(message, "Now Playing Overlay",
                           MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    static DateTime _startTime;
    static int _relaunchCount;
    static string[] _relaunchArgs = new string[0];

    // A crash within the first 10s of starting almost certainly repeats
    // immediately on relaunch too - bad config, a port fight, corrupt state -
    // so relaunching forever in that case would spin CPU instead of actually
    // recovering. A short-lived process before the crash is what distinguishes
    // "one bad API response" from "this will just crash again immediately".
    static void TryRelaunch() {
      try {
        if (_relaunchCount >= 5) {
          AppLog.Write("giving up after " + _relaunchCount + " auto-relaunches - start it manually.");
          return;
        }
        if ((DateTime.Now - _startTime).TotalSeconds < 10) {
          AppLog.Write("crashed within 10s of starting - not auto-relaunching (would likely just loop).");
          return;
        }
        string exe = Assembly.GetExecutingAssembly().Location;
        string cmdArgs = string.Join(" ", _relaunchArgs) + " -relaunch:" + (_relaunchCount + 1);
        AppLog.Write("auto-relaunching (attempt " + (_relaunchCount + 1) + "): " + cmdArgs);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, cmdArgs) {
          UseShellExecute = true
        });
      } catch (Exception ex2) {
        AppLog.Write("auto-relaunch itself failed: " + ex2);
      }
    }

    [STAThread]
    static int Main(string[] args) {
      _startTime = DateTime.Now;
      _hasConsole = AttachConsole(-1);      // -1 = parent process

      // An unhandled exception on any thread - the UI thread or a background
      // one - takes the whole process down with it by default, which is why a
      // single bad Twitch API response used to mean restarting OBS to get the
      // song overlay back too. Nothing here can prevent that termination, but
      // logging first turns "it just vanished" into a line pointing at why, and
      // relaunching means the pages (which already retry forever on their own)
      // find a live server again in a couple of seconds without anyone touching
      // OBS - the same URLs come back to life instead of staying dead until a
      // person notices and restarts the exe by hand.
      AppLog.Write("startup: pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);
      AppDomain.CurrentDomain.UnhandledException += (s, e) => {
        AppLog.Write("FATAL unhandled exception (terminating=" + e.IsTerminating + "): "
          + (e.ExceptionObject as Exception));
        if (e.IsTerminating) TryRelaunch();
      };
      Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
      Application.ThreadException += (s, e) => {
        AppLog.Write("UI thread exception (caught, continuing): " + e.Exception);
      };

      Application.EnableVisualStyles();
      LoadSettings();     // command line below overrides the saved choice

      var keepArgs = new List<string>();
      for (int i = 0; i < args.Length; i++) {
        var a = args[i].TrimStart('-', '/').ToLowerInvariant();
        if (a.StartsWith("relaunch:")) {
          int.TryParse(a.Substring("relaunch:".Length), out _relaunchCount);
          continue;   // never carried forward literally; TryRelaunch appends its own
        }
        keepArgs.Add(args[i]);
        if ((a == "port" || a == "p") && i + 1 < args.Length) {
          int p; if (int.TryParse(args[i + 1], out p)) { _port = p; }
          keepArgs.Add(args[i + 1]); i++;
        } else if (a == "prefer" && i + 1 < args.Length) {
          i++; _pinApp = args[i]; _mode = "prefer"; keepArgs.Add(args[i]);
        } else if (a == "only" && i + 1 < args.Length) {
          i++; _pinApp = args[i]; _mode = "only"; keepArgs.Add(args[i]);
        } else if (a == "auto") {
          _pinApp = ""; _mode = "auto";
        } else if (a == "startup" && i + 1 < args.Length) {
          string v = args[++i].Trim().ToLowerInvariant();
          if (v == "on" || v == "off") {
            bool want = (v == "on");
            bool ok = SetStartup(want);
            string msg = ok
              ? ("Start with Windows is now " + (want ? "ON" : "OFF") + ".")
              : "Could not change the startup setting.";
            if (_hasConsole) Say("  " + msg);
            else MessageBox.Show(msg, "Now Playing Overlay", MessageBoxButtons.OK,
                                 ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
          } else {
            Say("  Use -startup on  or  -startup off");
          }
          return 0;
        } else if (a == "help" || a == "h" || a == "?") {
          Say("Usage: NowPlayingOverlay.exe [-port 8787] [-prefer <app> | -only <app> | -auto]");
          Say("                             [-startup on|off]");
          Say("");
          Say("  -prefer spotify   follow Spotify when it has something, else fall back");
          Say("  -only itunes      never show anything but iTunes");
          Say("  -auto             follow whatever is actually playing (default)");
          Say("  -startup on       run automatically when Windows starts");
          Say("");
          Say("  It runs in the system tray - no window. Right-click the tray icon");
          Say("  for settings, or open http://127.0.0.1:8787/control");
          return 0;
        } else {
          int p; if (int.TryParse(a, out p)) _port = p;
        }
      }
      if (_mode != "auto" && _pinApp.Length == 0) _mode = "auto";
      _relaunchArgs = keepArgs.ToArray();
      if (_relaunchCount > 0) AppLog.Write("this is relaunch #" + _relaunchCount + " after a crash");

      // No first-run installer, deliberately - do not add one. Self-copying
      // the exe and creating shortcuts through late-bound shell automation are
      // exactly what dropper malware does, and an unsigned binary cannot do
      // either and look like anything else to antivirus heuristics. The
      // "put this file somewhere permanent" advice lives in setup.html and the
      // README instead, where it costs nothing in the binary. An installer
      // only becomes safe to consider if this project ever code-signs.

      // A crashed instance's socket is not always released the instant the
      // process dies - an auto-relaunch (below) can spawn fast enough to hit
      // that gap and see the port as taken when nothing is really listening on
      // it anymore. A few short retries absorb that without changing what a
      // genuine "another copy is already running" conflict looks like: that
      // case still fails, just a couple of seconds later than before.
      TcpListener listener = null;
      SocketException bindFailure = null;
      for (int attempt = 0; attempt < 10; attempt++) {
        try {
          listener = new TcpListener(IPAddress.Loopback, _port);
          listener.Start();
          bindFailure = null;
          break;
        } catch (SocketException ex) {
          bindFailure = ex;
          listener = null;
          Thread.Sleep(300);
        }
      }
      if (listener == null) {
        AppLog.Write("could not bind port " + _port + " after retrying: " + bindFailure.Message);
        // With no console this would otherwise fail invisibly at every login.
        Fatal("Could not open port " + _port + ".\r\n\r\n" + bindFailure.Message
              + "\r\n\r\nAnother copy is probably already running"
              + " (check the system tray).\r\n"
              + "To use a different port:  NowPlayingOverlay.exe -port " + (_port + 1)
              + "\r\nThen use that same port in the OBS browser source URL.");
        return 1;
      }
      AppLog.Write("listening on port " + _port);

      // Self-heal "Start with Windows". The Run key stores an absolute path,
      // and every ordinary way this app gets updated - a fresh folder from a
      // zip, a moved repo, dist\ replaced wholesale - leaves that path
      // pointing at an exe that no longer exists. Windows then just skips the
      // entry at login, and autostart silently stops working with nothing to
      // see anywhere. If the switch is on, re-point the entry at the copy that
      // is actually running. Done only after the port is bound, so a doomed
      // second instance can never steal the entry from the one that owns it.
      try {
        if (StartupEnabled()) {
          string want = "\"" + ExePath() + "\"";
          if (_port != 8787) want += " -port " + _port;
          string have = null;
          using (var k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            if (k != null) have = k.GetValue(RunValueName) as string;
          if (!string.Equals(have, want, StringComparison.OrdinalIgnoreCase)) {
            if (SetStartup(true))
              AppLog.Write("startup entry re-pointed at this exe (was: " + (have ?? "missing") + ")");
          }
        }
      } catch { }

      // Every streaming handler below holds a ThreadPool worker for as long as its
      // client stays connected: StreamSpectrum and StreamTwitch both sit in a loop
      // until the socket dies, so they are occupied threads rather than busy ones.
      // The pool starts with roughly one worker per core and, once they are all
      // taken, adds further threads only about twice a second - so with a handful
      // of streams held open, an ordinary request for a page or an album cover can
      // end up waiting on thread injection rather than on any actual work.
      //
      // Raising the floor past the worst case - every stream slot taken, plus room
      // for normal traffic - costs a few mostly-idle threads and removes that
      // stall entirely. Only ever raised, never lowered: on a machine whose
      // default is already higher, leave it alone.
      int minWorker, minIo;
      ThreadPool.GetMinThreads(out minWorker, out minIo);
      int wantThreads = MaxSpectrumClients + MaxTwitchClients + 16;
      if (minWorker < wantThreads) {
        try {
          ThreadPool.SetMinThreads(wantThreads, Math.Max(minIo, wantThreads));
          AppLog.Write("threadpool floor raised from " + minWorker + " to " + wantThreads);
        } catch (Exception ex) {
          AppLog.Write("could not raise threadpool floor (continuing): " + ex.Message);
        }
      }

      var poller = new Thread(PollLoop);
      poller.IsBackground = true;
      poller.Start();          // left MTA on purpose - see Await()

      AudioSpectrum.Start();   // live equaliser; self-heals if the device changes
      TwitchEvents.Start();    // no-op unless twitch-config.json has API creds
      TwitchChat.Start();      // no-op unless configured AND switched on

      var accept = new Thread(() => {
        while (true) {
          TcpClient client;
          try { client = listener.AcceptTcpClient(); } catch { break; }
          ThreadPool.QueueUserWorkItem(_ => Handle(client));
        }
      });
      accept.IsBackground = true;
      accept.Start();

      Banner();
      BuildTray();

      // First run: no config file anywhere means this machine has never been
      // set up, so open the wizard rather than sitting silently in the tray
      // looking broken. Only on a completely absent file - a config that
      // exists but has problems surfaces through the tray status instead,
      // because popping a browser at every launch would punish the configured.
      if (TwitchEvents.FindConfigPath() == null) {
        AppLog.Write("first run: no twitch-config.json anywhere - opening /setup");
        OpenUrl("/setup");
      }

      Application.Run();       // tray message loop; Exit comes from the menu
      return 0;
    }

    static void Banner() {
      if (!_hasConsole) return;      // running in the tray, nothing to print to
      Say("");
      SayColor("  Now Playing overlay is running.  v" + BuildInfo.Version
               + "  (built " + BuildInfo.BuiltUtc + ")", ConsoleColor.Green);
      Say("");
      SayColor("    Add this as an OBS Browser Source:", ConsoleColor.Cyan);
      SayColor("        http://127.0.0.1:" + _port + "/", ConsoleColor.Cyan);
      Say("");
      SayColor("    Everything else - customizing, layouts, choosing a source,", ConsoleColor.Cyan);
      SayColor("    Twitch alerts - is one dashboard now:", ConsoleColor.Cyan);
      SayColor("        http://127.0.0.1:" + _port + "/app", ConsoleColor.Cyan);
      Say("");
      if (_mode != "auto") {
        SayColor("    Source: " + (_mode == "only" ? "locked to \"" : "preferring \"")
                 + _pinApp + "\"", ConsoleColor.Yellow);
      }
      SayColor("    Sources: Windows media session + iTunes", ConsoleColor.DarkGray);
      SayColor("    Running in the system tray - right-click the icon to exit.",
               ConsoleColor.DarkGray);
      Say("");
    }

    // ----------------------------------------------------------------- poller
    // All WinRT/COM work happens here on one thread, so request handlers stay
    // fast and there is no cross-thread contention over the media APIs.
    static void PollLoop() {
      while (true) {
        try {
          Snapshot snap;
          lock (_mediaLock) {
            snap = Build();
            var prev = _current;
            // Only refetch art when the track actually changes - it is the
            // expensive part and it does not change within a track.
            if (snap.Id == prev.Id && prev.Art != null) {
              snap.Art = prev.Art;
              snap.ArtMime = prev.ArtMime;
            } else if (snap.HasArt) {
              byte[] art = (snap.Source == "itunes") ? ITunesArt() : SmtcArt();
              if (art != null && art.Length > 0) {
                snap.Art = art;
                snap.ArtMime = SniffMime(art);
              } else {
                snap.HasArt = false;
              }
            }
          }
          _current = snap;
          // Keep the equaliser listening to whatever the overlay is showing, so
          // voice chat or a video in another tab cannot drive the bars.
          AudioSpectrum.SetTargetApp(snap.App);
        } catch {
          // never let a transient media-API failure kill the poller
        }
        Thread.Sleep(1000);
      }
    }

    static Snapshot Build() {
      var list = new List<Snapshot>();
      var smtc = SmtcRead();
      if (smtc != null) list.Add(smtc);
      var itunes = ITunesRead();
      if (itunes != null) list.Add(itunes);
      if (list.Count == 0) return new Snapshot();

      // Apply the pin before scoring, so a pinned-but-paused player still beats
      // a different player that happens to be mid-song.
      string mode = _mode, pin = _pinApp;
      if (mode != "auto" && !string.IsNullOrEmpty(pin)) {
        var matched = new List<Snapshot>();
        foreach (var c in list) if (MatchesPin(c, pin)) matched.Add(c);
        if (matched.Count > 0) list = matched;
        else if (mode == "only") return new Snapshot();   // locked: show nothing else
      }

      // Something actively playing always beats something idle, so a paused
      // iTunes never steals the overlay from a playing Spotify, or the reverse.
      Snapshot best = null; int bestScore = -1;
      foreach (var c in list) {
        int score = 0;
        if (c.Playing) score += 4;
        if (c.Source == "itunes" || IsMusicApp(c.App)) score += 1;
        if (score > bestScore) { bestScore = score; best = c; }
      }
      return best;
    }

    static readonly string[] MusicHints =
      { "spotify", "apple", "itunes", "music", "tidal", "deezer" };

    // Apple has shipped its Windows player under several names - classic iTunes,
    // and now the separate Apple Music app from the Store, whose app id looks
    // nothing like "itunes". A pin written when one was installed has to keep
    // working when the other is, or the overlay silently shows nothing and the
    // setting that caused it is three clicks away in a different page.
    static readonly string[][] PinAliases = {
      new[] { "itunes", "apple", "applemusic", "apple music", "applemusicwin" }
    };

    static bool PinHit(string candidate, string pin) {
      if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(pin)) return false;
      string c = candidate.ToLowerInvariant();
      string p = pin.Trim().ToLowerInvariant();
      if (p.Length == 0) return false;
      if (c.IndexOf(p, StringComparison.Ordinal) >= 0) return true;

      foreach (var group in PinAliases) {
        bool pinInGroup = false;
        foreach (var g in group) {
          // Only let a group member stand in for the pin when the pin is long
          // enough to be meaningful; a two-letter pin would match everything.
          if (p == g || p.IndexOf(g, StringComparison.Ordinal) >= 0
              || (p.Length >= 4 && g.IndexOf(p, StringComparison.Ordinal) >= 0)) {
            pinInGroup = true; break;
          }
        }
        if (!pinInGroup) continue;
        foreach (var g in group)
          if (c.IndexOf(g, StringComparison.Ordinal) >= 0) return true;
      }
      return false;
    }

    static bool IsMusicApp(string aumid) {
      if (string.IsNullOrEmpty(aumid)) return false;
      var l = aumid.ToLowerInvariant();
      foreach (var h in MusicHints) if (l.Contains(h)) return true;
      return false;
    }

    static string MakeId(string t, string a, string al) {
      if (string.IsNullOrEmpty(t)) return "none";
      // FNV-1a: deterministic across runs, unlike string.GetHashCode()
      unchecked {
        uint hash = 2166136261;
        foreach (char c in (t + "|" + a + "|" + al)) { hash ^= c; hash *= 16777619; }
        return hash.ToString();
      }
    }

    // ------------------------------------------------- source 1: media session
    static GlobalSystemMediaTransportControlsSessionManager _mgr;

    static GlobalSystemMediaTransportControlsSessionManager Manager() {
      if (_mgr == null)
        _mgr = Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
      return _mgr;
    }

    // Windows can expose several media sessions at once (Spotify, a browser tab,
    // a game). The pin has to be applied while choosing among them, not merely
    // afterwards: otherwise a playing Spotify would always be picked here and a
    // pinned browser session would never even be considered.
    static GlobalSystemMediaTransportControlsSession BestSession() {
      var mgr = Manager();
      if (mgr == null) return null;
      var sessions = mgr.GetSessions();
      if (sessions == null || sessions.Count == 0) return null;

      string mode = _mode, pin = _pinApp;
      bool pinned = mode != "auto" && !string.IsNullOrEmpty(pin);
      string p = pinned ? pin.ToLowerInvariant() : null;

      GlobalSystemMediaTransportControlsSession best = null; int bestScore = -1;
      foreach (var s in sessions) {
        string aumid = s.SourceAppUserModelId ?? "";
        bool matches = pinned && PinHit(aumid, p);
        if (pinned && mode == "only" && !matches) continue;   // locked out entirely

        int score = 0;
        if (matches) score += 10;      // a pinned app outranks anything merely playing
        try {
          if (s.GetPlaybackInfo().PlaybackStatus ==
              GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) score += 2;
        } catch { }
        if (IsMusicApp(aumid)) score += 1;
        if (score > bestScore) { bestScore = score; best = s; }
      }

      if (best != null) return best;
      // Under "only" a missing match must yield nothing, never a stray fallback.
      if (pinned && mode == "only") return null;
      return mgr.GetCurrentSession();
    }

    // Every session Windows currently knows about, not just the one that won.
    // "My player isn't detected" and "my player is detected but something else
    // outranked it" need completely different fixes, and only the full list can
    // tell them apart - especially when a pin in "only" mode is quietly
    // excluding the very app the user is asking about.
    internal class SessionInfo {
      public string Aumid = "", Title = "", Artist = "", Status = "";
      public bool Musicish, ExcludedByPin;
    }

    internal static List<SessionInfo> AllSessions() {
      var list = new List<SessionInfo>();
      try {
        var mgr = Manager();
        if (mgr == null) return list;
        var sessions = mgr.GetSessions();
        if (sessions == null) return list;

        string mode = _mode, pin = (_pinApp ?? "").ToLowerInvariant();
        bool pinned = mode != "auto" && pin.Length > 0;

        foreach (var s in sessions) {
          var si = new SessionInfo();
          try { si.Aumid = s.SourceAppUserModelId ?? ""; } catch { }
          try {
            var p = Await(s.TryGetMediaPropertiesAsync());
            if (p != null) { si.Title = p.Title ?? ""; si.Artist = p.Artist ?? ""; }
          } catch { }
          try { si.Status = s.GetPlaybackInfo().PlaybackStatus.ToString(); } catch { }
          si.Musicish = IsMusicApp(si.Aumid);
          si.ExcludedByPin = pinned && mode == "only" && !PinHit(si.Aumid, pin);
          list.Add(si);
        }
      } catch { }
      return list;
    }

    static Snapshot SmtcRead() {
      try {
        var s = BestSession();
        if (s == null) return null;
        var p = Await(s.TryGetMediaPropertiesAsync());
        if (p == null || string.IsNullOrEmpty(p.Title)) return null;
        bool playing = s.GetPlaybackInfo().PlaybackStatus ==
                       GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        return new Snapshot {
          Playing = playing,
          Title = p.Title ?? "",
          Artist = p.Artist ?? "",
          Album = p.AlbumTitle ?? "",
          App = s.SourceAppUserModelId ?? "",
          Source = "smtc",
          HasArt = p.Thumbnail != null,
          Id = MakeId(p.Title, p.Artist, p.AlbumTitle)
        };
      } catch {
        _mgr = null;      // rebuild the manager next time round
        return null;
      }
    }

    static byte[] SmtcArt() {
      try {
        var s = BestSession();
        if (s == null) return null;
        var p = Await(s.TryGetMediaPropertiesAsync());
        if (p == null || p.Thumbnail == null) return null;
        var stream = Await(p.Thumbnail.OpenReadAsync());
        uint size = (uint)stream.Size;
        if (size == 0) return null;
        var reader = new DataReader(stream);
        Await(reader.LoadAsync(size));
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
      } catch { return null; }
    }

    // -------------------------------------------------------- source 2: iTunes
    // Creating the iTunes COM object LAUNCHES iTunes, so every entry point gates
    // on the process already running. The overlay must never pop iTunes open.
    static object _itunes;

    static void DropITunes() {
      if (_itunes != null) {
        try { Marshal.ReleaseComObject(_itunes); } catch { }
        _itunes = null;
      }
    }

    static bool ITunesRunning() {
      // This runs every poll tick. Each Process object holds a real OS handle,
      // and leaving them for the GC on a mostly-idle tray app means they pile
      // up between collections - dispose them the moment the answer is known.
      try {
        var procs = System.Diagnostics.Process.GetProcessesByName("iTunes");
        bool found = procs.Length > 0;
        foreach (var p in procs) { try { p.Dispose(); } catch { } }
        return found;
      }
      catch { return false; }
    }

    // Why iTunes could not be reached, surfaced through /sources so this is
    // diagnosable on a machine you cannot poke at directly.
    static volatile string _itunesDetail = "";

    static Type ITunesType() {
      Type t = Type.GetTypeFromProgID("iTunes.Application");
      if (t != null) return t;

      // Classic iTunes for Windows is commonly a 32-bit install, and then its
      // ProgID exists only in the 32-bit registry view - invisible to this
      // 64-bit process. Resolve the CLSID from that view explicitly; iTunes is
      // an out-of-process COM server so cross-bitness activation still works.
      foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 }) {
        try {
          using (var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view))
          using (var key = root.OpenSubKey(@"iTunes.Application\CLSID")) {
            if (key == null) continue;
            var clsid = key.GetValue(null) as string;
            if (string.IsNullOrEmpty(clsid)) continue;
            var tt = Type.GetTypeFromCLSID(new Guid(clsid));
            if (tt != null) {
              _itunesDetail = "resolved via " + view + " registry view";
              return tt;
            }
          }
        } catch (Exception ex) { _itunesDetail = view + " lookup failed: " + ex.Message; }
      }
      return null;
    }

    static dynamic ITunesApp() {
      if (!ITunesRunning()) { DropITunes(); _itunesDetail = "iTunes is not running"; return null; }
      if (_itunes != null) {
        try { var probe = ((dynamic)_itunes).PlayerState; return (dynamic)_itunes; }
        catch { DropITunes(); }        // stale RPC handle, rebuild below
      }
      try {
        Type t = ITunesType();
        if (t == null) {
          _itunesDetail = "iTunes COM class not registered (checked both 32- and 64-bit registry views)";
          return null;
        }
        _itunes = Activator.CreateInstance(t);
        _itunesDetail = "connected";
        return (dynamic)_itunes;
      } catch (Exception ex) {
        // The HRESULT is the part that actually identifies the failure; the
        // message alone is often the same generic sentence for several very
        // different causes, which is useless on a machine you cannot inspect.
        var com = ex as System.Runtime.InteropServices.COMException;
        string hr = "", meaning = "";
        if (com != null) {
          uint code = unchecked((uint)com.ErrorCode);
          hr = " (HRESULT 0x" + code.ToString("X8") + ")";
          if (code == 0x80080005) meaning = " - the iTunes COM server failed to start. This is the "
            + "usual signature of an elevation mismatch between iTunes and this app.";
          else if (code == 0x80040154) meaning = " - class not registered. This iTunes install did "
            + "not register COM automation; the Microsoft Store packaging of iTunes does this.";
          else if (code == 0x800401F3) meaning = " - invalid ProgID, so iTunes is not registered at all.";
          else if (code == 0x80070005) meaning = " - access denied, which is normally an elevation "
            + "mismatch: run iTunes and this app the same way, both normal or both as administrator.";
        }
        // A mismatch here usually means the overlay was started as administrator
        // while iTunes runs normally (or the reverse) - COM refuses across
        // integrity levels.
        _itunesDetail = ex.GetType().Name + hr + ": " + ex.Message + meaning
                      + (IsElevated() ? " [this app IS running elevated - try running it normally]" : "");
        return null;
      }
    }

    static bool IsElevated() {
      try {
        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
          return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
      } catch { return false; }
    }

    static Snapshot ITunesRead() {
      try {
        dynamic app = ITunesApp();
        if (app == null) return null;
        dynamic track = app.CurrentTrack;
        if (track == null) {
          _itunesDetail = "connected, but no track loaded (iTunes is stopped)";
          return null;
        }
        int state = (int)app.PlayerState;    // 1 = playing; 0 = paused/stopped
        string name = (string)track.Name ?? "";
        string artist = "";
        string album = "";
        try { artist = (string)track.Artist ?? ""; } catch { }
        try { album = (string)track.Album ?? ""; } catch { }
        bool hasArt = false;
        try { hasArt = ((int)track.Artwork.Count) > 0; } catch { }
        if (string.IsNullOrEmpty(name)) return null;
        return new Snapshot {
          Playing = (state == 1),
          Title = name, Artist = artist, Album = album,
          App = "iTunes", Source = "itunes",
          HasArt = hasArt, Id = MakeId(name, artist, album)
        };
      } catch { DropITunes(); return null; }
    }

    static byte[] ITunesArt() {
      string tmp = null;
      try {
        dynamic app = ITunesApp();
        if (app == null) return null;
        dynamic track = app.CurrentTrack;
        if (track == null) return null;
        dynamic art = track.Artwork;
        if (art == null || (int)art.Count < 1) return null;
        dynamic item = art.Item(1);
        int fmt = 1;
        try { fmt = (int)item.Format; } catch { }   // 1 JPEG, 2 PNG, 3 BMP
        string ext = fmt == 2 ? ".png" : fmt == 3 ? ".bmp" : ".jpg";
        tmp = Path.Combine(Path.GetTempPath(), "np-art-" + Guid.NewGuid().ToString("N") + ext);
        item.SaveArtworkToFile(tmp);
        if (!File.Exists(tmp)) return null;
        return File.ReadAllBytes(tmp);
      } catch {
        return null;
      } finally {
        try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
      }
    }

    static string QueryParam(string path, string name) {
      int q = path.IndexOf('?');
      if (q < 0) return null;
      foreach (var pair in path.Substring(q + 1).Split('&')) {
        int eq = pair.IndexOf('=');
        if (eq <= 0) continue;
        if (!pair.Substring(0, eq).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
        string v = pair.Substring(eq + 1).Replace('+', ' ');
        try { return Uri.UnescapeDataString(v); } catch { return v; }
      }
      return null;
    }

    static string SniffMime(byte[] b) {
      if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
      if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50) return "image/png";
      if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return "image/bmp";
      return "application/octet-stream";
    }

    // ------------------------------------------------------------------- HTTP
    static void Handle(TcpClient client) {
      try {
        using (client)
        using (var ns = client.GetStream()) {
          ns.ReadTimeout = 4000;
          // A hung peer (a stalled OBS browser process, a machine waking from
          // sleep) stops ACKing without ever closing the socket. The default
          // write timeout is infinite, so the SSE loops below would block in
          // Write forever and their client slots would leak one by one until
          // the cap turned every new OBS source away - while a fresh browser
          // tab kept working, which is exactly the confusing half-dead state
          // this guards against. Five seconds of zero progress on loopback
          // means the peer is gone in every way that matters.
          ns.WriteTimeout = 5000;
          // Read until the header block is complete rather than taking whatever
          // one packet happened to carry. Every request here is a GET with no
          // body, so the blank line is the end of the message and this cannot
          // block waiting for more. It matters because the same-origin guard
          // below reads headers: a set that straddled a TCP boundary would look
          // like headers that simply were not sent.
          var buf = new byte[8192];
          int read = 0;
          string req = "";
          while (read < buf.Length) {
            int n = ns.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
            req = Encoding.ASCII.GetString(buf, 0, read);
            if (req.IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0) break;
          }
          if (read <= 0) return;

          string path = "/";
          int sp1 = req.IndexOf(' ');
          if (sp1 > 0) {
            int sp2 = req.IndexOf(' ', sp1 + 1);
            if (sp2 > sp1) path = req.Substring(sp1 + 1, sp2 - sp1 - 1);
          }
          string route = path.Split('?')[0];
          var snap = _current;

          if (route == "/np") {
            Send(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Json(snap)));
          } else if (route == "/art") {
            // The cover is the one genuinely large thing served here, and the URL
            // already carries ?id=, a hash of title/artist/album - so a different
            // track is a different URL and a cached copy can never be the wrong
            // picture. Blanket no-store made every page re-download it: the
            // overlay preloads into an Image, then points both the <img> and the
            // backdrop at the same URL expecting a cache hit, which under
            // no-store is three fetches per track per page instead of one. On a
            // page with several previews that was most of the traffic, and the
            // first thing to fail when connections were scarce.
            if (snap.Art != null && snap.Art.Length > 0)
              Send(ns, 200, snap.ArtMime, snap.Art, "public, max-age=86400, immutable");
            else Send(ns, 204, "text/plain", new byte[0]);
          } else if (route == "/prefs") {
            Send(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(PrefsJson()));
          } else if (route == "/prefs/set") {
            string pk = QueryParam(path, "key") ?? "";
            string pv = QueryParam(path, "value") ?? "";
            if (pk.Length > 0) SetPref(pk, pv);
            Send(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(PrefsJson()));
          } else if (route == "/sources") {
            // Diagnostic: what each provider reports, and which one won.
            var sb = new StringBuilder();
            Snapshot smtcNow, itunesNow; bool itRunning;
            lock (_mediaLock) {
              itRunning = ITunesRunning();
              smtcNow = SmtcRead();
              itunesNow = ITunesRead();
            }
            sb.Append("{\"chosen\":").Append(Q(snap.Source)).Append(',');
            sb.Append("\"mode\":").Append(Q(_mode)).Append(',');
            sb.Append("\"app\":").Append(Q(_pinApp)).Append(',');
            sb.Append("\"now\":").Append(Json(snap)).Append(',');
            sb.Append("\"itunesRunning\":").Append(itRunning ? "true" : "false").Append(',');
            sb.Append("\"itunesDetail\":").Append(Q(_itunesDetail)).Append(',');
            sb.Append("\"elevated\":").Append(IsElevated() ? "true" : "false").Append(',');
            sb.Append("\"exeBitness\":").Append(Q(IntPtr.Size == 8 ? "64-bit" : "32-bit")).Append(',');
            sb.Append("\"providers\":[");
            sb.Append("{\"name\":\"smtc\",\"found\":").Append(smtcNow != null ? "true" : "false");
            if (smtcNow != null) sb.Append(",\"track\":").Append(Json(smtcNow));
            sb.Append("},{\"name\":\"itunes\",\"found\":").Append(itunesNow != null ? "true" : "false");
            if (itunesNow != null) sb.Append(",\"track\":").Append(Json(itunesNow));
            sb.Append("}]}");
            Send(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(sb.ToString()));
          } else if (route == "/diag" || route == "/diag/") {
            string detail; bool running;
            lock (_mediaLock) {
              running = ITunesRunning();
              ITunesRead();                // populates _itunesDetail as a side effect
              detail = _itunesDetail;
            }
            Send(ns, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(
              Diagnostics.Html(_port, _mode, _pinApp, IsElevated(), detail, running,
                               snap, TwitchEvents.FindConfigPath())));
          } else if (route == "/spectrum") {
            if (IsWebSocket(req)) WsStreamSpectrum(ns, req);
            else StreamSpectrum(ns);     // long-lived; returns on disconnect
          } else if (route == "/twitch") {
            Send(ns, 200, "application/json; charset=utf-8",
                 Encoding.UTF8.GetBytes(TwitchEvents.StatusJson()));
          } else if (route == "/twitch/events") {
            if (IsWebSocket(req)) WsStreamTwitch(ns, req);
            else StreamTwitch(ns);       // long-lived; returns on disconnect
          } else if (route == "/twitch/test") {
            string kind = QueryParam(path, "type") ?? "follow";
            string who = QueryParam(path, "user") ?? "";
            Send(ns, 200, "application/json; charset=utf-8",
                 Encoding.UTF8.GetBytes(TwitchEvents.TestFire(kind, who)));
          } else if (route == "/bot") {
            Send(ns, 200, "application/json; charset=utf-8",
                 Encoding.UTF8.GetBytes(TwitchChat.StatusJson()));
          } else if (route == "/bot/set") {
            // One endpoint for every switch on the page: the master toggle, a
            // per-command toggle, an edit, an add, a delete, a restore. Keeps
            // the browser side to a single fetch helper. It rewrites the
            // command list on disk, so the same cross-site rule as every other
            // state-writing route: only our own pages get to call it.
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            string what = QueryParam(path, "what") ?? "";
            string name = QueryParam(path, "name") ?? "";
            string val = QueryParam(path, "value") ?? "";
            bool on = (val == "1" || val == "true");

            if (what == "bot") {
              TwitchChat.SetEnabled(on);
              SaveSettings();
            } else if (what == "command") {
              BotCommands.SetEnabled(name, on);
            } else if (what == "edit") {
              int cd; if (!int.TryParse(QueryParam(path, "cooldown") ?? "", out cd)) cd = -1;
              BotCommands.Update(name, QueryParam(path, "response"), cd,
                                 (QueryParam(path, "modOnly") ?? "") == "1",
                                 QueryParam(path, "aliases"));
            } else if (what == "add") {
              BotCommands.Add(name, QueryParam(path, "response") ?? "");
            } else if (what == "remove") {
              BotCommands.Remove(name);
            } else if (what == "restore") {
              BotCommands.RestoreMissingDefaults();
            }
            Send(ns, 200, "application/json; charset=utf-8",
                 Encoding.UTF8.GetBytes(TwitchChat.StatusJson()));
          } else if (route == "/bot/test") {
            Send(ns, 200, "application/json; charset=utf-8",
                 Encoding.UTF8.GetBytes(TwitchChat.TestLine(
                   QueryParam(path, "msg") ?? "!song",
                   (QueryParam(path, "mod") ?? "") == "1",
                   QueryParam(path, "nick") ?? "")));
          } else if (route == "/spectrum.json") {
            Send(ns, 200, "application/json; charset=utf-8",
                 Encoding.UTF8.GetBytes(SpectrumJson()));
          } else if (route == "/setsource") {
            string m = NormalizeMode(QueryParam(path, "mode"));
            string app = QueryParam(path, "app") ?? "";
            if (m == "auto") app = "";
            if (m != "auto" && app.Length == 0) m = "auto";
            _mode = m; _pinApp = app;
            SaveSettings();
            var body = "{\"mode\":" + Q(_mode) + ",\"app\":" + Q(_pinApp) + "}";
            Send(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(body));
          } else if (route == "/setup" || route == "/setup/") {
            SendResource(ns, "setup.html");
          } else if (route == "/setup/state") {
            // Same-origin only: this reports whether Twitch is configured and
            // where the config lives, which is recon for the attack the guard
            // above exists to stop, and is nobody else's business regardless.
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            SendPrivate(ns, 200, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(TwitchEvents.SetupStateJson(_port)));
          } else if (route == "/setup/save") {
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            string err;
            TwitchEvents.SetupSave(QueryParam(path, "channel"), QueryParam(path, "clientId"),
                                   QueryParam(path, "clientSecret"), out err);
            SendPrivate(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(
              "{\"ok\":" + (err.Length == 0 ? "true" : "false") + ",\"error\":" + Q(err) + "}"));
          } else if (route == "/oauth/start") {
            // A cross-site navigation here would send the user to consent for
            // whichever Twitch app the config currently names - the second half
            // of the config-clobber chain. Only our own wizard may start it.
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            if (!TwitchEvents.OAuthReady)
              SendRedirect(ns, "/setup?oautherr=" + Uri.EscapeDataString(
                "save the Client ID and Secret first"));
            else
              SendRedirect(ns, TwitchEvents.OAuthStartUrl(_port));
          } else if (route == "/oauth/bot/start") {
            // The chat bot's flow, started from the Chat bot tab. Same
            // cross-site rule as /oauth/start, for the same reason.
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            if (!TwitchEvents.OAuthReady)
              SendRedirect(ns, "/app?botoautherr=" + Uri.EscapeDataString(
                "run the main Twitch setup first - the bot signs in through the same Twitch app") + "#bot");
            else
              SendRedirect(ns, TwitchEvents.BotOAuthStartUrl(_port));
          } else if (route == "/oauth/callback") {
            // Twitch lands the user's browser here after the consent screen.
            // Both flows share this URL (it must match the app registration to
            // the letter); the state nonce says which one a callback belongs
            // to, and each bounces back to the page it started from.
            string denied = QueryParam(path, "error");
            string cbState = QueryParam(path, "state") ?? "";
            bool forBot = TwitchEvents.IsBotOAuthState(cbState);
            if (denied != null && denied.Length > 0) {
              string msg = "you declined the authorization (" + denied + ")";
              SendRedirect(ns, forBot
                ? "/app?botoautherr=" + Uri.EscapeDataString(msg) + "#bot"
                : "/setup?oautherr=" + Uri.EscapeDataString(msg));
            } else if (forBot) {
              string berr;
              bool bok = TwitchEvents.OAuthCompleteBot(_port, QueryParam(path, "code") ?? "",
                                                       cbState, out berr);
              SendRedirect(ns, bok ? "/app?botoauth=ok#bot"
                                   : "/app?botoautherr=" + Uri.EscapeDataString(berr) + "#bot");
            } else {
              string oerr;
              bool ok = TwitchEvents.OAuthComplete(_port, QueryParam(path, "code") ?? "",
                                                   cbState, out oerr);
              SendRedirect(ns, ok ? "/setup?oauth=ok"
                                  : "/setup?oautherr=" + Uri.EscapeDataString(oerr));
            }
          } else if (route == "/setup/skip") {
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            TwitchEvents.SetupSkip();
            SendPrivate(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":true}"));
          } else if (route == "/setup/restart") {
            // The one that would hurt most: a page looping this would keep a
            // live stream's overlays restarting for as long as the tab is open.
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            SendPrivate(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":true}"));
            // Reply first, then hand over: the fresh copy binds the port as
            // soon as this one lets it go, and every page already retries.
            var t = new Thread(() => {
              Thread.Sleep(600);
              try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                  ExePath(), string.Join(" ", _relaunchArgs)) { UseShellExecute = true });
              } catch (Exception ex) { AppLog.Write("setup: restart spawn failed: " + ex.Message); }
              Environment.Exit(0);
            });
            t.IsBackground = true;
            t.Start();
          } else if (route == "/shared.js") {
            SendResource(ns, "shared.js", "application/javascript; charset=utf-8");
          } else if (route == "/themes") {
            Send(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(ThemesJson()));
          } else if (route == "/themes-page" || route == "/themes-page/") {
            SendResource(ns, "themes.html");
          } else if (route == "/looks" || route == "/looks/") {
            SendResource(ns, "looks.html");
          } else if (route == "/themes/save") {
            // Writes a file to disk, so the same cross-site rules as the setup
            // routes: only our own pages get to call it.
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            string terr;
            bool tok = SaveUserTheme(QueryParam(path, "def") ?? "", out terr);
            SendPrivate(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(
              "{\"ok\":" + (tok ? "true" : "false") + ",\"error\":" + Q(terr) + "}"));
          } else if (route == "/themes/delete") {
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            string derr;
            bool dok = DeleteUserTheme(QueryParam(path, "name") ?? "", out derr);
            SendPrivate(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(
              "{\"ok\":" + (dok ? "true" : "false") + ",\"error\":" + Q(derr) + "}"));
          } else if (route == "/presets") {
            // Private: nobody's saved-look list is another website's business,
            // and only our own pages ever read it.
            SendPrivate(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(PresetsJson()));
          } else if (route == "/presets/save") {
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            string perr;
            bool pok = PresetSave(QueryParam(path, "name") ?? "", QueryParam(path, "url") ?? "",
                                  QueryParam(path, "page") ?? "", out perr);
            SendPrivate(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(
              "{\"ok\":" + (pok ? "true" : "false") + ",\"error\":" + Q(perr) + "}"));
          } else if (route == "/presets/delete") {
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            PresetDelete(QueryParam(path, "name") ?? "");
            SendPrivate(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":true}"));
          } else if (route == "/app" || route == "/app/") {
            SendResource(ns, "app.html");
          } else if (route == "/bot-page" || route == "/bot-page/") {
            SendResource(ns, "bot.html");
          } else if (route == "/help" || route == "/help/") {
            SendResource(ns, "help.html");
          } else if (route == "/control" || route == "/control/") {
            SendResource(ns, "control.html");
          } else if (route == "/layouts" || route == "/layouts/") {
            SendResource(ns, "layouts.html");
          } else if (route == "/customize" || route == "/customize/") {
            SendResource(ns, "customize.html");
          } else if (route == "/alerts" || route == "/alerts/") {
            SendResource(ns, "alerts.html");
          } else if (route == "/stats" || route == "/stats/") {
            SendResource(ns, "stats.html");
          } else if (route == "/") {
            SendResource(ns, "overlay.html");
          } else if (IsEmbeddedPage(route)) {
            // Convention route: build.ps1 embeds every .html in the repo root,
            // so a page dropped in there is served at /<name> with no entry in
            // this chain. The explicit routes above always win, and PageNameRe
            // allows no dots or slashes, so there is nothing to traverse with.
            SendResource(ns, route.Trim('/') + ".html");
          } else {
            Send(ns, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
          }
        }
      } catch {
        // a dropped browser connection must not take the server down
      }
    }

    static string SpectrumJson() {
      var bands = AudioSpectrum.Read();
      var sb = new StringBuilder();
      sb.Append("{\"active\":").Append(AudioSpectrum.Active ? "true" : "false");
      sb.Append(",\"status\":").Append(Q(AudioSpectrum.Status));
      sb.Append(",\"target\":").Append(Q(AudioSpectrum.Target));
      sb.Append(",\"bands\":[");
      for (int i = 0; i < bands.Length; i++) {
        if (i > 0) sb.Append(',');
        sb.Append(bands[i]);
      }
      sb.Append("]}");
      return sb.ToString();
    }

    // Server-sent events. A plain socket can do SSE with no framing protocol,
    // unlike websockets, and the browser reconnects on its own if this drops.
    // Polling at 30fps over HTTP would mean a new connection every frame.
    static int _spectrumClients;
    internal const int MaxSpectrumClients = 16;
    internal static int SpectrumClients { get { return _spectrumClients; } }

    static void StreamSpectrum(NetworkStream ns) {
      if (Interlocked.Increment(ref _spectrumClients) > MaxSpectrumClients) {
        Interlocked.Decrement(ref _spectrumClients);
        Send(ns, 503, "text/plain", Encoding.UTF8.GetBytes("too many spectrum clients"));
        return;
      }
      try {
        var head = Encoding.ASCII.GetBytes(
          "HTTP/1.1 200 OK\r\n" +
          "Content-Type: text/event-stream; charset=utf-8\r\n" +
          "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
          "Access-Control-Allow-Origin: *\r\n" +
          "Connection: keep-alive\r\n" +
          "X-Accel-Buffering: no\r\n\r\n");
        ns.Write(head, 0, head.Length);
        ns.Flush();

        while (true) {
          var payload = Encoding.UTF8.GetBytes("data: " + SpectrumJson() + "\n\n");
          ns.Write(payload, 0, payload.Length);   // throws when the tab closes
          ns.Flush();
          Thread.Sleep(33);                       // ~30fps
        }
      } catch {
        // client went away; nothing to do
      } finally {
        Interlocked.Decrement(ref _spectrumClients);
      }
    }

    // Alerts as server-sent events, same shape as the spectrum stream.
    //
    // A new client starts from the current sequence rather than replaying the
    // ring: opening the alerts page in OBS should not fire every follow since
    // the app started. Reconnects lose nothing that matters, because an alert
    // nobody saw within a few seconds of the event has missed its moment.
    static int _twitchClients;
    internal const int MaxTwitchClients = 16;
    internal static int TwitchClients { get { return _twitchClients; } }

    static void StreamTwitch(NetworkStream ns) {
      if (Interlocked.Increment(ref _twitchClients) > MaxTwitchClients) {
        Interlocked.Decrement(ref _twitchClients);
        Send(ns, 503, "text/plain", Encoding.UTF8.GetBytes("too many alert clients"));
        return;
      }
      try {
        var head = Encoding.ASCII.GetBytes(
          "HTTP/1.1 200 OK\r\n" +
          "Content-Type: text/event-stream; charset=utf-8\r\n" +
          "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
          "Access-Control-Allow-Origin: *\r\n" +
          "Connection: keep-alive\r\n" +
          "X-Accel-Buffering: no\r\n\r\n");
        ns.Write(head, 0, head.Length);
        ns.Flush();

        long seen = TwitchEvents.CurrentSeq;
        int sincePing = 0;

        while (true) {
          long now;
          var pending = TwitchEvents.Since(seen, out now);
          seen = now;

          if (pending.Length > 0) {
            var sb = new StringBuilder();
            foreach (var p in pending) sb.Append("data: ").Append(p).Append("\n\n");
            var payload = Encoding.UTF8.GetBytes(sb.ToString());
            ns.Write(payload, 0, payload.Length);
            ns.Flush();
            sincePing = 0;
          } else if (++sincePing >= 40) {
            // Without traffic nothing ever writes, so a closed tab would go
            // unnoticed and the client slot would leak. A comment line is
            // ignored by EventSource but still throws on a dead socket.
            var ping = Encoding.ASCII.GetBytes(": ping\n\n");
            ns.Write(ping, 0, ping.Length);
            ns.Flush();
            sincePing = 0;
          }
          Thread.Sleep(250);
        }
      } catch {
        // client went away; nothing to do
      } finally {
        Interlocked.Decrement(ref _twitchClients);
      }
    }

    // WebSocket variants of the two streams above. OBS loads every browser
    // source into one Chromium instance, which allows six plain HTTP
    // connections per address - and each SSE stream holds one for its whole
    // life. Six overlay sources therefore starve /np and /art behind streams
    // that never finish: the card freezes in OBS while a normal browser (with
    // its own six) stays fine. WebSockets live in a separate, far larger
    // Chromium pool, so the streams move there and the six slots stay free
    // for the short requests. The SSE paths remain for pages from an older
    // exe that are still open mid-upgrade.
    static bool IsWebSocket(string req) {
      return req.IndexOf("upgrade: websocket", StringComparison.OrdinalIgnoreCase) >= 0
          && req.IndexOf("sec-websocket-key", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static bool WsHandshake(NetworkStream ns, string req) {
      string key = null;
      foreach (var line in req.Split(new[] { "\r\n" }, StringSplitOptions.None)) {
        int c = line.IndexOf(':');
        if (c > 0 && line.Substring(0, c).Trim()
              .Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) {
          key = line.Substring(c + 1).Trim();
          break;
        }
      }
      if (key == null) {
        Send(ns, 400, "text/plain", Encoding.UTF8.GetBytes("bad websocket request"));
        return false;
      }
      string accept;
      using (var sha1 = System.Security.Cryptography.SHA1.Create())
        accept = Convert.ToBase64String(sha1.ComputeHash(
          Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
      var head = Encoding.ASCII.GetBytes(
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Upgrade: websocket\r\n" +
        "Connection: Upgrade\r\n" +
        "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
      ns.Write(head, 0, head.Length);
      ns.Flush();
      return true;
    }

    static void WsSendFrame(NetworkStream ns, byte opcode, byte[] data) {
      byte[] head;
      if (data.Length < 126) {
        head = new byte[] { (byte)(0x80 | opcode), (byte)data.Length };
      } else if (data.Length < 65536) {
        head = new byte[] { (byte)(0x80 | opcode), 126,
                            (byte)(data.Length >> 8), (byte)data.Length };
      } else {
        head = new byte[10];
        head[0] = (byte)(0x80 | opcode); head[1] = 127;
        long l = data.Length;
        for (int i = 0; i < 8; i++) head[9 - i] = (byte)(l >> (8 * i));
      }
      ns.Write(head, 0, head.Length);
      ns.Write(data, 0, data.Length);
      ns.Flush();
    }

    static void WsSendText(NetworkStream ns, string text) {
      WsSendFrame(ns, 0x1, Encoding.UTF8.GetBytes(text));
    }

    // The server never acts on anything the page sends, but bytes still arrive
    // (pong replies to our pings, a close frame on teardown) and unread data
    // would slowly fill the socket buffer. Discard them; a zero-byte read
    // means the peer is gone, and throwing hands control to the loop's catch.
    static void WsDrain(NetworkStream ns, byte[] scratch) {
      while (ns.DataAvailable) {
        if (ns.Read(scratch, 0, scratch.Length) <= 0)
          throw new System.IO.IOException("websocket peer closed");
      }
    }

    static void WsStreamSpectrum(NetworkStream ns, string req) {
      if (Interlocked.Increment(ref _spectrumClients) > MaxSpectrumClients) {
        Interlocked.Decrement(ref _spectrumClients);
        Send(ns, 503, "text/plain", Encoding.UTF8.GetBytes("too many spectrum clients"));
        return;
      }
      try {
        if (!WsHandshake(ns, req)) return;
        var scratch = new byte[512];
        while (true) {
          WsDrain(ns, scratch);
          WsSendText(ns, SpectrumJson());     // throws when the source closes
          Thread.Sleep(33);                   // ~30fps
        }
      } catch {
        // client went away; nothing to do
      } finally {
        Interlocked.Decrement(ref _spectrumClients);
      }
    }

    static void WsStreamTwitch(NetworkStream ns, string req) {
      if (Interlocked.Increment(ref _twitchClients) > MaxTwitchClients) {
        Interlocked.Decrement(ref _twitchClients);
        Send(ns, 503, "text/plain", Encoding.UTF8.GetBytes("too many alert clients"));
        return;
      }
      try {
        if (!WsHandshake(ns, req)) return;
        var scratch = new byte[512];
        long seen = TwitchEvents.CurrentSeq;
        int sincePing = 0;
        while (true) {
          WsDrain(ns, scratch);
          long now;
          var pending = TwitchEvents.Since(seen, out now);
          seen = now;
          if (pending.Length > 0) {
            foreach (var p in pending) WsSendText(ns, p);
            sincePing = 0;
          } else if (++sincePing >= 40) {
            // Same dead-socket probe as the SSE loop, as a ping control frame:
            // the page never sees it, but writing it throws once the tab is gone.
            WsSendFrame(ns, 0x9, new byte[0]);
            sincePing = 0;
          }
          Thread.Sleep(250);
        }
      } catch {
        // client went away; nothing to do
      } finally {
        Interlocked.Decrement(ref _twitchClients);
      }
    }

    // The names build.ps1 accepts for pages, which is also what keeps this
    // safe as a URL: lowercase letters, digits and dashes only.
    static readonly System.Text.RegularExpressions.Regex PageNameRe =
      new System.Text.RegularExpressions.Regex("^[a-z0-9-]{1,64}$");

    static bool IsEmbeddedPage(string route) {
      string name = route.Trim('/');
      if (!PageNameRe.IsMatch(name)) return false;
      try {
        return Assembly.GetExecutingAssembly()
          .GetManifestResourceInfo(name + ".html") != null;
      } catch { return false; }
    }

    static void SendResource(NetworkStream ns, string name) {
      SendResource(ns, name, "text/html; charset=utf-8");
    }

    static void SendResource(NetworkStream ns, string name, string contentType) {
      var asm = Assembly.GetExecutingAssembly();
      using (var s = asm.GetManifestResourceStream(name)) {
        if (s == null) {
          Send(ns, 404, "text/plain", Encoding.UTF8.GetBytes(name + " missing from executable"));
          return;
        }
        using (var ms = new MemoryStream()) {
          s.CopyTo(ms);
          Send(ns, 200, contentType, ms.ToArray());
        }
      }
    }

    static string ResourceText(string name) {
      var asm = Assembly.GetExecutingAssembly();
      using (var s = asm.GetManifestResourceStream(name)) {
        if (s == null) return null;
        using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
      }
    }

    // ---- themes as data --------------------------------------------------------
    // A theme is a JSON map of CSS variables (see themes\shadow.json), applied by
    // shared.js. The built-ins are compiled into the binary; anything a user makes
    // or downloads goes in %APPDATA%\NowPlayingOverlay\themes and shows up here on
    // the next request - no restart, because /themes is read fresh every time and
    // the source pages re-ask every 5 seconds anyway.

    internal static string ThemesDir() {
      string dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NowPlayingOverlay", "themes");
      Directory.CreateDirectory(dir);
      return dir;
    }

    // Discovered from the exe rather than listed here: build.ps1 embeds every
    // themes\*.json as theme-<file>, so shipping a new built-in theme is
    // dropping a file in that folder - nothing in this file changes.
    static string[] BuiltinThemes() {
      var list = new List<string>();
      foreach (var n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
        if (n.StartsWith("theme-", StringComparison.Ordinal)
            && n.EndsWith(".json", StringComparison.Ordinal)) list.Add(n);
      list.Sort(StringComparer.Ordinal);       // stable order across runs
      return list.ToArray();
    }

    // "shockblade" -> is theme-shockblade.json compiled into this exe?
    // The guards that used to hard-code the two original names go through this
    // instead, so a new built-in theme is automatically protected from being
    // shadowed or deleted by a user file of the same name.
    static bool IsBuiltinTheme(string name) {
      try {
        return Assembly.GetExecutingAssembly()
          .GetManifestResourceInfo("theme-" + name + ".json") != null;
      } catch { return false; }
    }

    static string ThemesJson() {
      string active;
      lock (_prefsLock) { Prefs().TryGetValue("theme", out active); }
      if (string.IsNullOrEmpty(active)) active = "shockblade";

      var sb = new StringBuilder();
      sb.Append("{\"active\":").Append(Q(active)).Append(",\"themes\":[");
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      bool first = true;

      foreach (var res in BuiltinThemes()) {
        string raw = ResourceText(res);
        if (raw == null) continue;
        if (!first) sb.Append(',');
        first = false;
        sb.Append(raw.Trim());
        try {
          var d = new JavaScriptSerializer().DeserializeObject(raw) as Dictionary<string, object>;
          object nm;
          if (d != null && d.TryGetValue("name", out nm) && nm is string) seen.Add((string)nm);
        } catch { }
      }

      try {
        foreach (var f in Directory.GetFiles(ThemesDir(), "*.json")) {
          try {
            var ser = new JavaScriptSerializer();
            var d = ser.DeserializeObject(File.ReadAllText(f)) as Dictionary<string, object>;
            if (d == null) continue;
            object nm;
            if (!d.TryGetValue("name", out nm) || !(nm is string) || ((string)nm).Length == 0) continue;
            if (!seen.Add((string)nm)) continue;   // a user file cannot shadow a built-in
            d["builtin"] = false;
            if (!first) sb.Append(',');
            first = false;
            // Re-serialised rather than pasted raw, so a hand-edited file that
            // parses cannot smuggle anything structural into the array.
            sb.Append(ser.Serialize(d));
          } catch { }                              // one bad file must not hide the rest
        }
      } catch { }

      sb.Append("]}");
      return sb.ToString();
    }

    // A user theme arrives as one JSON blob in a query parameter. Nothing from
    // it is written verbatim: every field is pulled out, validated, and put
    // into a fresh object, so the file on disk only ever contains the shape
    // the rest of the app expects. The name doubles as the filename, which is
    // why its pattern has no dots or slashes to traverse with.
    static readonly System.Text.RegularExpressions.Regex ThemeNameRe =
      new System.Text.RegularExpressions.Regex("^[a-z0-9][a-z0-9-]{0,31}$");
    static readonly System.Text.RegularExpressions.Regex VarNameRe =
      new System.Text.RegularExpressions.Regex("^--[A-Za-z0-9_-]{1,40}$");
    static readonly System.Text.RegularExpressions.Regex SwatchRe =
      new System.Text.RegularExpressions.Regex("^#[0-9a-fA-F]{3,8}$");

    static Dictionary<string, object> CleanVarMap(object raw) {
      var outMap = new Dictionary<string, object>();
      var d = raw as Dictionary<string, object>;
      if (d == null) return outMap;
      foreach (var kv in d) {
        if (outMap.Count >= 40) break;
        if (!VarNameRe.IsMatch(kv.Key)) continue;
        string v = Convert.ToString(kv.Value) ?? "";
        if (v.Length == 0 || v.Length > 80) continue;
        if (v.IndexOfAny(new[] { ';', '{', '}', '<', '>' }) >= 0) continue;
        outMap[kv.Key] = v;
      }
      return outMap;
    }

    static bool SaveUserTheme(string defJson, out string error) {
      error = "";
      Dictionary<string, object> d;
      try {
        d = new JavaScriptSerializer().DeserializeObject(defJson) as Dictionary<string, object>;
      } catch { d = null; }
      if (d == null) { error = "that is not a theme"; return false; }

      object nmObj;
      string name = d.TryGetValue("name", out nmObj) ? Convert.ToString(nmObj) : null;
      if (name == null || !ThemeNameRe.IsMatch(name)) {
        error = "theme names are 1-32 characters: lowercase letters, digits and dashes";
        return false;
      }
      if (IsBuiltinTheme(name)) {
        error = "that name belongs to a built-in theme";
        return false;
      }

      string label = "";
      object lbObj;
      if (d.TryGetValue("label", out lbObj)) label = (Convert.ToString(lbObj) ?? "").Trim();
      label = label.Replace("\r", " ").Replace("\n", " ");
      if (label.Length > 40) label = label.Substring(0, 40);
      if (label.Length == 0) label = name;

      var swatch = new List<object>();
      object swObj;
      var swArr = d.TryGetValue("swatch", out swObj) ? swObj as object[] : null;
      if (swArr != null) {
        foreach (var s in swArr) {
          if (swatch.Count >= 2) break;
          string c = Convert.ToString(s) ?? "";
          if (SwatchRe.IsMatch(c)) swatch.Add(c);
        }
      }

      object dashObj, srcObj;
      d.TryGetValue("dashboard", out dashObj);
      d.TryGetValue("source", out srcObj);

      var clean = new Dictionary<string, object>();
      clean["name"] = name;
      clean["label"] = label;
      clean["swatch"] = swatch;
      clean["dashboard"] = CleanVarMap(dashObj);
      clean["source"] = CleanVarMap(srcObj);

      try {
        string dir = ThemesDir();
        string file = Path.Combine(dir, name + ".json");
        // A cap, not a quota: this exists so a looping script cannot fill the
        // disk with theme files, and 50 is far past any real collection.
        if (!File.Exists(file) && Directory.GetFiles(dir, "*.json").Length >= 50) {
          error = "theme folder is full (50) - delete one first";
          return false;
        }
        File.WriteAllText(file, new JavaScriptSerializer().Serialize(clean));
        return true;
      } catch (Exception ex) {
        AppLog.Write("theme save failed: " + ex.Message);
        error = "could not write the theme file";
        return false;
      }
    }

    static bool DeleteUserTheme(string name, out string error) {
      error = "";
      if (!ThemeNameRe.IsMatch(name)) { error = "no such theme"; return false; }
      if (IsBuiltinTheme(name)) { error = "built-in themes cannot be deleted"; return false; }
      try {
        string file = Path.Combine(ThemesDir(), name + ".json");
        if (File.Exists(file)) File.Delete(file);
        // Nothing should stay pointed at a palette that no longer exists.
        string active;
        lock (_prefsLock) { Prefs().TryGetValue("theme", out active); }
        if (string.Equals(active, name, StringComparison.OrdinalIgnoreCase))
          SetPref("theme", "shockblade");
        return true;
      } catch (Exception ex) {
        AppLog.Write("theme delete failed: " + ex.Message);
        error = "could not delete the theme file";
        return false;
      }
    }

    // ---- saved looks -----------------------------------------------------------
    // A preset is a customized source URL saved under a name, so a look someone
    // dialled in does not live only in an OBS source nobody can find again.
    // Stored as a JSON list in %APPDATA%, same as everything else the app owns.

    static readonly object _presetsLock = new object();

    static string PresetsPath() {
      string dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NowPlayingOverlay");
      Directory.CreateDirectory(dir);
      return Path.Combine(dir, "presets.json");
    }

    static List<Dictionary<string, object>> LoadPresets() {
      var list = new List<Dictionary<string, object>>();
      try {
        string p = PresetsPath();
        if (!File.Exists(p)) return list;
        var arr = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(p)) as object[];
        if (arr == null) return list;
        foreach (var o in arr) {
          var d = o as Dictionary<string, object>;
          if (d != null) list.Add(d);
        }
      } catch { }                     // an unreadable file means an empty list, not a crash
      return list;
    }

    static string PresetsJson() {
      lock (_presetsLock) {
        return "{\"presets\":" + new JavaScriptSerializer().Serialize(LoadPresets()) + "}";
      }
    }

    static bool PresetSave(string name, string url, string page, out string error) {
      error = "";
      name = (name ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
      if (name.Length == 0) { error = "give the look a name"; return false; }
      if (name.Length > 40) name = name.Substring(0, 40);
      // Only URLs that point at this app get stored: a preset list must never
      // become a place where somebody parks an arbitrary link.
      bool ours = url.StartsWith("http://127.0.0.1:" + _port + "/", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("http://localhost:" + _port + "/", StringComparison.OrdinalIgnoreCase);
      if (!ours || url.Length > 2000) { error = "that is not one of this app's URLs"; return false; }
      page = (page ?? "").Trim();
      if (page.Length > 16) page = page.Substring(0, 16);

      lock (_presetsLock) {
        var list = LoadPresets();
        list.RemoveAll(delegate(Dictionary<string, object> d) {
          object n; return d.TryGetValue("name", out n)
            && string.Equals(Convert.ToString(n), name, StringComparison.OrdinalIgnoreCase);
        });
        if (list.Count >= 100) { error = "preset list is full (100) - delete some first"; return false; }
        var entry = new Dictionary<string, object>();
        entry["name"] = name;
        entry["url"] = url;
        entry["page"] = page;
        entry["saved"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        list.Insert(0, entry);        // newest first, which is also the one just saved
        try {
          File.WriteAllText(PresetsPath(), new JavaScriptSerializer().Serialize(list));
          return true;
        } catch (Exception ex) {
          AppLog.Write("preset save failed: " + ex.Message);
          error = "could not write the presets file";
          return false;
        }
      }
    }

    static void PresetDelete(string name) {
      if (string.IsNullOrEmpty(name)) return;
      lock (_presetsLock) {
        var list = LoadPresets();
        list.RemoveAll(delegate(Dictionary<string, object> d) {
          object n; return d.TryGetValue("name", out n)
            && string.Equals(Convert.ToString(n), name, StringComparison.OrdinalIgnoreCase);
        });
        try { File.WriteAllText(PresetsPath(), new JavaScriptSerializer().Serialize(list)); }
        catch (Exception ex) { AppLog.Write("preset delete failed: " + ex.Message); }
      }
    }

    // Almost everything served here is live state that must never be cached, so
    // that stays the default and callers opt out deliberately rather than opting
    // in - a stale page or a stale /np is a bug that looks like a hardware fault.
    static void Send(NetworkStream ns, int code, string contentType, byte[] body) {
      Send(ns, code, contentType, body, null);
    }

    // ---- cross-site request guard --------------------------------------------
    // This server has no authentication, by design: it is loopback-only and OBS
    // browser sources cannot present credentials. That was harmless while every
    // route was read-mostly. The setup routes are not - they write credentials
    // into a file and restart the process - and "no authentication" means any
    // web page the user happens to be visiting can reach them. A single <img
    // src="http://127.0.0.1:8787/setup/restart"> on any site would kill a live
    // stream, on repeat, with no interaction beyond loading the page.
    //
    // Sec-Fetch-Site is what actually distinguishes those requests: browsers set
    // it themselves and a page cannot forge it. same-origin is our own wizard
    // calling us; none is the user typing the address or the tray opening it.
    // Anything else - cross-site, same-site - is another origin driving the
    // request and has no business here.
    //
    // Requests with no Sec-Fetch-Site at all (curl, PowerShell, an old browser)
    // are allowed through unless they carry a Referer from somewhere else, so
    // scripting the app locally still works while a stale browser's cross-site
    // request is still caught by its Referer.
    static string HeaderValue(string req, string name) {
      foreach (var line in req.Split(new[] { "\r\n" }, StringSplitOptions.None)) {
        if (line.Length == 0) break;                 // end of headers
        int c = line.IndexOf(':');
        if (c <= 0) continue;
        if (line.Substring(0, c).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
          return line.Substring(c + 1).Trim();
      }
      return null;
    }

    static bool SameOriginRequest(string req) {
      // Truncated headers are not "headers that said nothing" - they are an
      // unknown, and an unknown must not open a door.
      if (req.IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0) return false;

      string site = HeaderValue(req, "Sec-Fetch-Site");
      if (site != null)
        return site.Equals("same-origin", StringComparison.OrdinalIgnoreCase)
            || site.Equals("none", StringComparison.OrdinalIgnoreCase);

      // No Fetch Metadata: an older browser, or a script. A same-origin fetch
      // from our own wizard still carries a Referer pointing at us.
      string referer = HeaderValue(req, "Referer");
      if (referer != null)
        return referer.StartsWith("http://127.0.0.1:" + _port, StringComparison.OrdinalIgnoreCase)
            || referer.StartsWith("http://localhost:" + _port, StringComparison.OrdinalIgnoreCase);

      // Neither signal. This used to be allowed as "not a browser", which was
      // wrong: an attacker's page controls whether its own requests carry a
      // Referer (one Referrer-Policy: no-referrer meta tag suppresses it), so
      // on any browser that does not send Sec-Fetch-Site the absent-absent
      // case was reachable on purpose and re-opened the whole attack. Deny by
      // default and let genuine non-browser callers say so explicitly: a web
      // page cannot add a custom header to a cross-origin request without a
      // preflight, and nothing here answers one.
      return HeaderValue(req, "X-Setup-Tool") != null;
    }

    // 403 for a route that only this app's own pages may drive.
    static void SendForbidden(NetworkStream ns) {
      Send(ns, 403, "text/plain",
           Encoding.UTF8.GetBytes("This address can only be used from the app's own pages."));
    }

    // Plain 302. The OAuth flow needs to bounce the user's browser out to
    // Twitch and back, and the callback bounces on to /setup so the wizard is
    // the single place that renders outcomes.
    static void SendRedirect(NetworkStream ns, string url) {
      var head = Encoding.ASCII.GetBytes(
        "HTTP/1.1 302 Found\r\nLocation: " + url + "\r\n"
        + "Content-Length: 0\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
      ns.Write(head, 0, head.Length);
      ns.Flush();
    }

    // No Access-Control-Allow-Origin. The overlay routes carry it because OBS
    // browser sources and the dashboard's frames legitimately read them from
    // another origin; the setup routes never need that, and the wildcard would
    // otherwise let any site read setup state cross-origin.
    static void SendPrivate(NetworkStream ns, int code, string contentType, byte[] body) {
      var sb = new StringBuilder();
      sb.Append("HTTP/1.1 ").Append(code).Append(code == 200 ? " OK" : " Error").Append("\r\n");
      sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
      sb.Append("Content-Length: ").Append(body == null ? 0 : body.Length).Append("\r\n");
      sb.Append("Cache-Control: no-cache, no-store, must-revalidate\r\n");
      sb.Append("Connection: close\r\n\r\n");
      var head = Encoding.ASCII.GetBytes(sb.ToString());
      ns.Write(head, 0, head.Length);
      if (body != null && body.Length > 0) ns.Write(body, 0, body.Length);
      ns.Flush();
    }

    static void Send(NetworkStream ns, int code, string contentType, byte[] body, string cacheControl) {
      string status = code == 200 ? "OK" : code == 204 ? "No Content"
                    : code == 404 ? "Not Found" : "Error";
      var sb = new StringBuilder();
      sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(status).Append("\r\n");
      sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
      sb.Append("Content-Length: ").Append(body == null ? 0 : body.Length).Append("\r\n");
      sb.Append("Access-Control-Allow-Origin: *\r\n");
      sb.Append("Cache-Control: ")
        .Append(cacheControl ?? "no-cache, no-store, must-revalidate").Append("\r\n");
      sb.Append("Connection: close\r\n\r\n");
      var head = Encoding.ASCII.GetBytes(sb.ToString());
      ns.Write(head, 0, head.Length);
      if (body != null && body.Length > 0) ns.Write(body, 0, body.Length);
      ns.Flush();
    }

    static string Json(Snapshot s) {
      var sb = new StringBuilder();
      sb.Append('{');
      sb.Append("\"playing\":").Append(s.Playing ? "true" : "false").Append(',');
      sb.Append("\"title\":").Append(Q(s.Title)).Append(',');
      sb.Append("\"artist\":").Append(Q(s.Artist)).Append(',');
      sb.Append("\"album\":").Append(Q(s.Album)).Append(',');
      sb.Append("\"app\":").Append(Q(s.App)).Append(',');
      sb.Append("\"source\":").Append(Q(s.Source)).Append(',');
      sb.Append("\"id\":").Append(Q(s.Id)).Append(',');
      // The poller clears HasArt if the art fetch fails, so on a published
      // snapshot this already implies the bytes are present.
      sb.Append("\"hasArt\":").Append(s.HasArt ? "true" : "false");
      sb.Append('}');
      return sb.ToString();
    }

    // Song titles legitimately contain quotes, backslashes and emoji, so escape
    // properly rather than trusting the input.
    static string Q(string s) {
      if (s == null) return "\"\"";
      var sb = new StringBuilder("\"");
      foreach (char c in s) {
        switch (c) {
          case '"': sb.Append("\\\""); break;
          case '\\': sb.Append("\\\\"); break;
          case '\n': sb.Append("\\n"); break;
          case '\r': sb.Append("\\r"); break;
          case '\t': sb.Append("\\t"); break;
          default:
            if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
            break;
        }
      }
      return sb.Append('"').ToString();
    }
  }
}
