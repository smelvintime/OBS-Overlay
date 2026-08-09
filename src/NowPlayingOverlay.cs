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

    // ---- feature switches ---------------------------------------------------
    // Each of these is a background engine that costs something even when
    // nothing is looking: media detection makes COM/WinRT calls every second,
    // the equaliser captures audio at ~30fps, Twitch holds a socket open and
    // polls Helix every minute. Pages and layouts are deliberately NOT switches
    // - an unused page costs nothing, because the server only does work while
    // something is holding it open. The chat bot has its own switch (/bot/set).
    //
    // Music detection applies live (the poller checks it every tick). The
    // other two start threads that run forever by design, so flipping them
    // takes the same clean self-restart the OAuth flow already uses - simpler
    // and better tested than teaching every loop to stop and start again.
    static volatile bool _featOverlay = true, _featEq = true, _featTwitch = true;
    static bool _bootEq = true, _bootTwitch = true;   // what this process started with

    internal static bool TwitchFeatureOn { get { return _featTwitch; } }

    static bool FeatOn(string v) { return !(v == "0" || v == "off" || v == "false"); }

    static string FeaturesJson() {
      bool restart = (_featEq != _bootEq) || (_featTwitch != _bootTwitch);
      return "{\"overlay\":" + (_featOverlay ? "true" : "false")
           + ",\"eq\":" + (_featEq ? "true" : "false")
           + ",\"twitch\":" + (_featTwitch ? "true" : "false")
           + ",\"restartNeeded\":" + (restart ? "true" : "false") + "}";
    }

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
          else if (k == "feat.overlay") _featOverlay = FeatOn(v);
          else if (k == "feat.eq") _featEq = FeatOn(v);
          else if (k == "feat.twitch") _featTwitch = FeatOn(v);
        }
        if (_mode != "auto" && _pinApp.Length == 0) _mode = "auto";
      } catch { }
    }

    internal static void SaveSettings() {
      try {
        Files.WriteAtomic(SettingsPath(),
          "mode=" + _mode + "\r\napp=" + _pinApp + "\r\n"
          + "bot=" + (TwitchChat.Enabled ? "1" : "0") + "\r\n"
          + "feat.overlay=" + (_featOverlay ? "1" : "0") + "\r\n"
          + "feat.eq=" + (_featEq ? "1" : "0") + "\r\n"
          + "feat.twitch=" + (_featTwitch ? "1" : "0") + "\r\n");
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

    internal static string GetPref(string key) {
      lock (_prefsLock) {
        string v;
        return Prefs().TryGetValue(key, out v) ? v : "";
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
          Files.WriteAtomic(PrefsPath(), sb.ToString());
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

    // Every "open this folder" menu item comes here.
    //
    // The quotes are the whole point. These paths contain the Windows account
    // name, and an account called "Alex Smith" makes
    // C:\Users\Alex Smith\AppData\... - handed to explorer.exe as a bare
    // argument that arrives as TWO arguments, so Explorer tries to open
    // "C:\Users\Alex" and reports the folder as unavailable. A missing pair of
    // quotes presenting as "your files are missing" is a bad way to greet
    // someone, and it only ever reproduces on accounts with a space in the
    // name - never on the machine this was written on.
    //
    // Failures are also no longer silent: a swallowed exception here meant a
    // menu item that did nothing at all, with nothing to look at afterwards.
    static void OpenFolder(string dir) {
      try {
        Directory.CreateDirectory(dir);
        if (!Directory.Exists(dir)) {
          AppLog.Write("open folder: could not create " + dir);
          MessageBox.Show("This folder could not be created:\r\n\r\n" + dir,
                          "Now Playing Overlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
          return;
        }
        // Hand the folder to the shell rather than passing it to explorer.exe
        // as an argument. Explorer parses its own command line and can refuse
        // a path it has only just learned about - a folder created moments
        // earlier by the click itself is exactly that case, and it reports
        // "location is not available" for a folder that demonstrably exists.
        // The shell opens the item directly and has no such trouble.
        System.Diagnostics.Process.Start(
          new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
      } catch (Exception ex) {
        AppLog.Write("open folder failed (" + dir + "): " + ex.Message);
        MessageBox.Show("Could not open:\r\n\r\n" + dir + "\r\n\r\n" + ex.Message,
                        "Now Playing Overlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
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
          : st == "disabled"      ? "Turned off (Features page)"
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
      menu.Items.Add("Open media folder (alert sounds && clips)", null,
                     (s, e) => OpenFolder(MediaDir()));
      menu.Items.Add("Open log folder", null, (s, e) => OpenFolder(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NowPlayingOverlay")));
      menu.Items.Add(new ToolStripSeparator());
      // Sits beside Exit because they are the same kind of thing, and it is
      // the safer of the two: Exit means going to find the exe again, which
      // is the errand this exists to remove.
      menu.Items.Add("Restart", null, (s, e) => { Restart(0, "tray menu"); });
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

    static void HideTray() {
      try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } } catch { }
    }

    static void Shutdown() {
      AppLog.Write("shutdown requested from tray menu");
      HideTray();
      try { Application.Exit(); } catch { }
      Environment.Exit(0);
    }

    // One restart, two doors: the tray menu and /setup/restart. The old copy
    // spawns a fresh one and lets go of the port; Main's bind retry loop on
    // the new side absorbs the moment it is still held, and every page
    // already reconnects on its own, so OBS sources come back within a couple
    // of seconds without anyone touching OBS.
    //
    // Order matters. The spawn happens FIRST and a failure means this copy
    // stays exactly as it was - exiting into a failed spawn (antivirus, the
    // exe renamed while running) would leave a live stream with no overlay
    // and no obvious way back. Only once a replacement is genuinely starting
    // does the icon go, and it must go before the process does: Windows
    // leaves a dead icon in the tray until something repaints it, and the
    // fresh copy adds its own straight away - two icons, one a corpse, looks
    // worse than no restart button at all.
    internal static void Restart(int delayMs, string why) {
      AppLog.Write("restart requested from " + why);
      var t = new Thread(() => {
        if (delayMs > 0) Thread.Sleep(delayMs);
        try {
          System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            ExePath(), string.Join(" ", _relaunchArgs)) { UseShellExecute = true });
        } catch (Exception ex) {
          AppLog.Write("restart: spawn failed, staying up: " + ex.Message);
          return;
        }
        HideTray();
        Environment.Exit(0);
      });
      t.IsBackground = true;
      t.Start();
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
          // Range-checked, because new TcpListener throws
          // ArgumentOutOfRangeException - not SocketException - for anything
          // outside 1-65535, and the bind loop only catches the latter. A typo
          // like "-port 87877" therefore escaped past the friendly "use a
          // different port" dialog that exists for exactly this, straight into
          // the unhandled-exception handler, and the app died at every login
          // if the value had reached the Run key.
          int p; if (int.TryParse(args[i + 1], out p) && p > 0 && p <= 65535) { _port = p; }
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
          int p; if (int.TryParse(a, out p) && p > 0 && p <= 65535) _port = p;
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

      // What this process actually started with, so the Features page can say
      // honestly whether a toggle needs the restart or is already in effect.
      _bootEq = _featEq; _bootTwitch = _featTwitch;

      var poller = new Thread(PollLoop);
      poller.IsBackground = true;
      poller.Start();          // left MTA on purpose - see Await()

      // Make the media folder real at startup instead of on first use. It was
      // created lazily by whatever touched it first, so until someone opened
      // the Customize tab the folder the docs and the tray menu both tell you
      // to put files in did not exist - and the tray item, which created it
      // and opened it in the same breath, handed the shell a folder that was
      // microseconds old and got told the location was unavailable.
      try { MediaDir(); } catch (Exception ex) {
        AppLog.Write("could not create the media folder: " + ex.Message);
      }

      if (_featEq) AudioSpectrum.Start();   // live equaliser; self-heals if the device changes
      else AppLog.Write("equaliser: not started - switched off on the Features page");
      TwitchEvents.Start();    // loads config always (bot shares it); network only if the feature is on
      TwitchChat.Start();      // no-op unless configured AND switched on
      LeagueStats.Start();     // idle unless the bot's Game stats switch is on
      LobbyRanks.Start();      // idle unless !ranks asks for it
      Updater.CleanupAfterSwap();   // sweep the previous build's ".old" leftover, if any
      Updater.StartAuto();          // keeps itself current; installs only when nothing is on air

      var accept = new Thread(() => {
        // A SocketException out of Accept is not the end of the listener, and
        // treating it as one was catastrophic: WSAECONNRESET is a documented,
        // ordinary return when a peer drops between SYN and accept, which is
        // exactly what OBS does when it refreshes several browser sources at
        // once. Breaking here left the process alive - tray icon, tooltip,
        // everything - with every overlay in OBS dead and never coming back,
        // no line in the log, and the port still bound so relaunching failed
        // with "another copy is probably already running". Only a shut-down
        // listener (ObjectDisposedException / InvalidOperationException) ends
        // the loop now; anything else is one bad connection, not the end.
        int consecutive = 0;
        while (true) {
          TcpClient client;
          try {
            client = listener.AcceptTcpClient();
            consecutive = 0;
          } catch (SocketException ex) {
            // A pause on a repeating failure, so a listener wedged in some
            // state that fails instantly cannot spin a core - and a line every
            // hundredth failure after the first few, so a listener that never
            // recovers still says so. Going quiet forever would leave exactly
            // the "process alive, overlays dead, nothing in the log" symptom
            // this is here to end.
            consecutive++;
            if (consecutive <= 3 || consecutive % 100 == 0)
              AppLog.Write("accept failed (" + consecutive + "): " + ex.Message);
            if (consecutive > 3) Thread.Sleep(100);
            continue;
          } catch (Exception ex) {
            AppLog.Write("accept loop ended: " + ex.Message);
            break;
          }
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
          // Music detection off (Features page): publish silence, hold no COM
          // object open. Checked every tick, which is what lets this switch
          // apply instantly without the restart the other engines need.
          if (!_featOverlay) {
            lock (_mediaLock) { DropITunes(); }
            _current = new Snapshot();
            AudioSpectrum.SetTargetApp("");
            Thread.Sleep(1000);
            continue;
          }
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
        // Both of these are WinRT objects holding native buffers and an open
        // handle on the thumbnail, and neither used to be closed. A cover is
        // read on every track change - roughly 160 of them across an evening
        // of music, at a few hundred KB each - and nothing here allocates
        // enough managed memory to provoke the gen-2 collection that would
        // eventually run their finalizers, so the native working set simply
        // climbed for the length of the stream.
        using (var stream = Await(p.Thumbnail.OpenReadAsync())) {
          if (stream == null) return null;
          uint size = (uint)stream.Size;
          if (size == 0) return null;
          var reader = new DataReader(stream);
          try {
            Await(reader.LoadAsync(size));
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
          } finally {
            // Detach first: a DataReader closes the stream it owns, and the
            // using above closes it too. One close each is fine; the second
            // one throwing would be caught below and turn a cover that was
            // read perfectly well into no cover at all - a worse bug than the
            // leak this whole block is here to fix.
            reader.DetachStream();
            reader.Dispose();
          }
        }
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
            //
            // ...but only if the id really does match, which is the part that
            // was missing. The parameter was never read, so the handler always
            // answered with whatever _current happened to hold. The poller
            // swaps _current once a second, so a track change between the /np
            // response and the /art request that followed it pinned the NEW
            // track's bytes under the OLD track's URL - immutably, for 24
            // hours, in a disk cache shared by every browser source and
            // surviving an OBS restart. That song then showed the wrong cover
            // all day and refreshing the source could not clear it.
            // A caller that names no id still gets whatever is current, so
            // hand-typing /art in a browser keeps working - but it does NOT
            // get the long cache, because the argument above only holds for a
            // URL that names its content. Cached immutably, a bare /art would
            // pin one cover to that address for a day, which is the very bug
            // this check exists to close, one URL over.
            string wantId = QueryParam(path, "id");
            bool haveArt = snap.Art != null && snap.Art.Length > 0;
            if (haveArt && wantId == snap.Id)
              Send(ns, 200, snap.ArtMime, snap.Art, "public, max-age=86400, immutable");
            else if (haveArt && wantId == null)
              Send(ns, 200, snap.ArtMime, snap.Art);          // no-store by default
            // Both misses answer 204, and Send's default no-store means
            // neither is cached - so a mismatch is "ask again", not "never".
            else Send(ns, 204, "text/plain", new byte[0]);
          } else if (route == "/features/state") {
            SendPrivate(ns, 200, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(FeaturesJson()));
          } else if (route == "/features/set") {
            // Flips background engines on and off and writes settings to disk,
            // so the same cross-site rule as every other state-writing route.
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            string fw = QueryParam(path, "what") ?? "";
            bool fon = (QueryParam(path, "value") ?? "") == "1";
            if (fw == "overlay") _featOverlay = fon;
            else if (fw == "eq") _featEq = fon;
            else if (fw == "twitch") _featTwitch = fon;
            SaveSettings();
            AppLog.Write("features: " + fw + " -> " + (fon ? "on" : "off"));
            SendPrivate(ns, 200, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(FeaturesJson()));
          } else if (route == "/prefs") {
            Send(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(PrefsJson()));
          } else if (route == "/prefs/set") {
            // Writes prefs.txt to disk, so the same cross-site rule as every
            // other state-writing route. Only our own pages call this.
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
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
            // Same cross-site rule as the state-writing routes, for the same
            // reason: this pushes an alert to every connected source, so a
            // page looping it would spray fake follows over a live stream for
            // as long as the tab stayed open. Our own pages call it fetch()-
            // style from the same origin and are unaffected.
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
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
            } else if (what == "followthanks") {
              // value flips it; template (optional) rewrites the message.
              TwitchChat.SetFollowThanks(on, QueryParam(path, "template"));
            } else if (what == "gamestats") {
              int gtm; if (!int.TryParse(QueryParam(path, "timer") ?? "", out gtm)) gtm = 0;
              TwitchChat.SetGameStats(on, (QueryParam(path, "announce") ?? "1") == "1", gtm);
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
            // This writes _mode and _pinApp AND persists them, so it belongs
            // with every other state-writing route behind the same guard. It
            // was the one that got missed: a plain <img src="/setsource?..."> on
            // any page the user visits needs no preflight and no reply, and
            // could pin the overlay to a player that does not exist - blanking
            // the now-playing card mid-stream, and still blank after a restart
            // because it went to settings.txt.
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
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
            // Reply first, then hand over - the delay is for this response to
            // reach the browser, not for the handover itself.
            Restart(600, "dashboard");
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
          } else if (route == "/league") {
            SendPrivate(ns, 200, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(LeagueStats.StatusJson()));
          } else if (route == "/league-state") {
            // The session tracker's feed. Each request marks the tracker as
            // wanted, which is what keeps the League poll loop alive - no
            // open tracker source, no polling, per the features-page promise.
            LeagueStats.NoteOverlayInterest();
            SendPrivate(ns, 200, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(LeagueStats.OverlayJson()));
          } else if (route == "/update/status") {
            SendPrivate(ns, 200, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(Updater.StatusJson()));
          } else if (route == "/update/check") {
            // Read-only in effect, but it makes this app call out to GitHub -
            // a stranger's tab doesn't get to make this machine phone anywhere.
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            SendPrivate(ns, 200, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(Updater.CheckJson(QueryParam(path, "force") == "1")));
          } else if (route == "/update/auto") {
            // Turning automatic updates off is a decision about this machine,
            // so it is written down like every other one - and behind the same
            // door, because a stranger's tab does not get to decide that a
            // streaming PC stops receiving fixes.
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            string on = QueryParam(path, "on");
            if (on != null) {
              SetPref("autoUpdate", on == "1" ? "1" : "0");
              AppLog.Write("update: automatic updates " + (on == "1" ? "on" : "off"));
            }
            SendPrivate(ns, 200, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(Updater.StatusJson()));
          } else if (route == "/update/run") {
            // Downloads code, rebuilds and replaces the program - the most
            // state-changing route in the app, so the same-origin door is
            // non-negotiable here.
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            string uerr;
            bool uok = Updater.Begin(out uerr);
            SendPrivate(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(
              "{\"ok\":" + (uok ? "true" : "false") + ",\"error\":" + Q(uerr) + "}"));
          } else if (route == "/league/path") {
            // Stores the folder League is installed in, for when discovery
            // can't work it out alone (an elevated client on a machine where
            // WMI is broken). Writes a pref and aims a file probe at a path,
            // so the same cross-site rule as every state-writing route.
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            string lp = (QueryParam(path, "value") ?? "").Trim().Trim('"');
            if (lp.Length > 260) lp = lp.Substring(0, 260);
            SetPref("leaguePath", lp);
            AppLog.Write("league: install folder " + (lp.Length > 0 ? "set by hand" : "cleared"));
            SendPrivate(ns, 200, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(LeagueStats.StatusJson()));
          } else if (route == "/league/test") {
            // Pure: a canned match history through the real parser, touching
            // no state - so the formatting is provable with no League installed.
            SendPrivate(ns, 200, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(LeagueStats.TestParse()));
          } else if (route == "/media-list") {
            SendPrivate(ns, 200, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(MediaListJson()));
          } else if (route == "/media/upload") {
            // Ahead of the /media/ serving branch, which would otherwise read
            // "upload" as a filename. Writes into the media folder, so it sits
            // behind the same guard as every other state-writing route.
            if (!SameOriginRequest(req)) { SendForbidden(ns); return; }
            SendPrivate(ns, 200, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(MediaUpload(ns, req, buf, read, path)));
          } else if (route.StartsWith("/media/", StringComparison.Ordinal)) {
            string mname;
            try { mname = Uri.UnescapeDataString(route.Substring("/media/".Length)); }
            catch { mname = ""; }
            string mime = MediaNameRe.IsMatch(mname) ? MediaMime(mname) : null;
            string mfile = mime == null ? null : Path.Combine(MediaDir(), mname);
            if (mfile != null && File.Exists(mfile)) {
              // Private (no CORS wildcard): the alerts page is same-origin, and
              // the wildcard would let any website the user visits read their
              // media files by guessing names. max-age 60 so replacing a file
              // under the same name shows up within a minute.
              SendPrivate(ns, 200, mime, File.ReadAllBytes(mfile), "max-age=60");
            } else {
              Send(ns, 404, "text/plain", Encoding.UTF8.GetBytes("no such media file"));
            }
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
      sb.Append("{\"active\":").Append((_featEq && AudioSpectrum.Active) ? "true" : "false");
      sb.Append(",\"status\":").Append(Q(_featEq ? AudioSpectrum.Status
                                       : "turned off on the Features page"));
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
      // WebSockets are not bound by CORS at all - any website can open one to
      // a loopback port and read whatever comes back - so the handshake checks
      // Origin itself. A browser always sends it: only our own pages get
      // upgraded. No Origin means a non-browser client (a script, a tool),
      // which the same-origin guard elsewhere also lets through on purpose.
      string origin = HeaderValue(req, "Origin");
      if (origin != null
          && !origin.Equals("http://127.0.0.1:" + _port, StringComparison.OrdinalIgnoreCase)
          && !origin.Equals("http://localhost:" + _port, StringComparison.OrdinalIgnoreCase)) {
        SendForbidden(ns);
        return false;
      }

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
    // 3, 4, 6 and 8 digits, and nothing between them - 5 and 7 are not colours,
    // and a swatch that is not a colour lands in a page as an unset property
    // rather than a fallback.
    static readonly System.Text.RegularExpressions.Regex SwatchRe =
      new System.Text.RegularExpressions.Regex(
        @"^#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})\z");

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
        Files.WriteAtomic(file, new JavaScriptSerializer().Serialize(clean));
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
          Files.WriteAtomic(PresetsPath(), new JavaScriptSerializer().Serialize(list));
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
        try { Files.WriteAtomic(PresetsPath(), new JavaScriptSerializer().Serialize(list)); }
        catch (Exception ex) { AppLog.Write("preset delete failed: " + ex.Message); }
      }
    }

    // ---- user media ------------------------------------------------------------
    // Alert sounds and clips: plain files the user drops into
    // %APPDATA%\NowPlayingOverlay\media (tray menu opens it), served read-only
    // at /media/<name>. The extension whitelist is the security boundary -
    // this must never become "serve any file the URL names".

    internal static string MediaDir() {
      string dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NowPlayingOverlay", "media");
      Directory.CreateDirectory(dir);
      return dir;
    }

    // Starts with a letter or digit (so no dot-names and no ".."), then the
    // characters real downloads actually have. No slashes, no colons -
    // nothing that can leave the media folder.
    //
    // Written as a BLOCKLIST of what is dangerous rather than an allowlist of
    // what is tidy. The old allowlist was [A-Za-z0-9 ._()-], which quietly
    // refused most of a real sound pack - "Don't Stop Me Now.mp3", "Airhorn!.wav",
    // "Cafe.mp3", "bruh - sound effect #2.mp3" - and every non-Latin name
    // outright. Refused with no log line and no entry in /media-list, so the
    // customizer showed an empty picker beside a message naming the folder the
    // user was looking at full of files. Traversal never needed a narrow
    // character set: what it needs is no path separators, no colon (NTFS
    // alternate data streams), no wildcards, and a first character that cannot
    // start a relative path. The extension whitelist below is still the real
    // boundary.
    //
    // The comma stays excluded, and that is now load-bearing: alerts.html
    // splits ?media= and ?sound= on commas to make a group of clips, so a
    // filename containing one could never be addressed anyway. \z rather than
    // $ because .NET's $ also matches before a trailing newline.
    static readonly System.Text.RegularExpressions.Regex MediaNameRe =
      new System.Text.RegularExpressions.Regex(
        @"^[^\x00-\x1f./\\:*?""<>|,~][^\x00-\x1f/\\:*?""<>|,]{0,127}\z");

    static string MediaMime(string name) {
      switch (Path.GetExtension(name).ToLowerInvariant()) {
        case ".mp3": return "audio/mpeg";
        case ".wav": return "audio/wav";
        case ".ogg": case ".oga": return "audio/ogg";
        case ".opus": return "audio/ogg";     // Chromium reads Opus in an Ogg shell
        case ".m4a": case ".aac": return "audio/mp4";
        case ".flac": return "audio/flac";
        case ".mp4": case ".m4v": return "video/mp4";
        case ".webm": return "video/webm";
        case ".gif": return "image/gif";
        case ".png": case ".apng": return "image/png";
        case ".jpg": case ".jpeg": return "image/jpeg";
        case ".webp": return "image/webp";
        case ".avif": return "image/avif";
        // Deliberately no .svg: an SVG opened directly is a scriptable
        // document on this app's origin, and every same-origin route here is
        // unauthenticated by design. An alert clip is not worth that.
        default: return null;      // anything else is not served, full stop
      }
    }

    // ---- uploads --------------------------------------------------------------
    // The other way into the media folder. Copying files into a directory
    // behind a tray menu is a small step for whoever set the app up and a wall
    // for the person actually streaming - it is the step that stopped alert
    // clips being used at all. The customizer takes the file itself now; the
    // folder is still there and still works.
    //
    // Nothing about the security boundary moves: the same name rules and the
    // same extension whitelist that decide what can be SERVED decide what can
    // be written, so an upload cannot put anything in the folder that /media/
    // would then refuse - or worse, agree - to hand out.

    // Generous for a gif, mean enough that a mis-dropped video export is
    // refused with a sentence instead of quietly eating the request thread.
    const int MediaUploadCap = 40 * 1024 * 1024;

    // A name the folder and the group syntax can both live with. This repairs
    // rather than rejects, deliberately: the entire point of the button is that
    // nobody has to think about file names, and "Refused: bad character" would
    // put the wall straight back up. The comma matters most - alerts.html
    // splits ?media= on commas, so a file with one in its name could never be
    // addressed even sitting in the folder.
    static string SafeMediaName(string raw) {
      string name = (raw ?? "").Trim();
      // Drop any directory part by hand, before the character pass. Doing it
      // with Path.GetFileName first looks equivalent and is not: it reads a
      // colon as a drive separator, so "my cool, gif: v2.gif" came back as
      // "v2.gif" and the client's file quietly lost most of its name. Safe,
      // but wrong - and wrong in the one place this feature is judged.
      int cut = name.LastIndexOfAny(new[] { '/', '\\' });
      if (cut >= 0) name = name.Substring(cut + 1);

      var sb = new StringBuilder();
      foreach (char c in name)
        sb.Append(c < 0x20 || "/\\:*?\"<>|,".IndexOf(c) >= 0 ? '-' : c);
      name = sb.ToString().Trim();
      // A leading dot or tilde is the one thing the name rule cannot allow:
      // that is what ".." is made of.
      while (name.Length > 0 && (name[0] == '.' || name[0] == '~'))
        name = name.Substring(1).Trim();
      // The rule allows 128; stopping at 100 leaves room for a " (12)" suffix
      // without the dedupe below pushing a legal name over the edge.
      if (name.Length > 100) {
        string keep = Path.GetExtension(name);
        if (keep.Length > 20) keep = "";                 // not an extension, just a long tail
        name = name.Substring(0, 100 - keep.Length) + keep;
      }
      return name;
    }

    static string MediaUpload(NetworkStream ns, string req, byte[] buf, int read, string path) {
      if (!req.StartsWith("POST ", StringComparison.Ordinal))
        return "{\"ok\":false,\"why\":\"An upload has to be a POST.\"}";

      string name = SafeMediaName(QueryParam(path, "name"));
      if (name.Length == 0 || !MediaNameRe.IsMatch(name))
        return "{\"ok\":false,\"why\":\"That file name can't be used.\"}";
      if (MediaMime(name) == null) {
        string ext = Path.GetExtension(name);
        return "{\"ok\":false,\"why\":" + Q((ext.Length > 0 ? ext + " files" : "Files with no extension")
             + " can't be used as alert media.") + "}";
      }

      string why;
      byte[] body = ReadBody(ns, req, buf, read, MediaUploadCap, out why);
      if (body == null) return "{\"ok\":false,\"why\":" + Q(why) + "}";
      if (body.Length == 0) return "{\"ok\":false,\"why\":\"That file is empty.\"}";

      // Never overwrite. The folder is the user's own and predates this button;
      // a file already in it may be named in a look saved months ago, and
      // silently replacing its contents would change that look with no trace.
      string dir = MediaDir();
      string bare = Path.GetFileNameWithoutExtension(name), ext2 = Path.GetExtension(name);
      string final = name;
      for (int i = 2; i < 1000 && File.Exists(Path.Combine(dir, final)); i++)
        final = bare + " (" + i + ")" + ext2;

      try {
        Files.WriteAtomic(Path.Combine(dir, final), body);
      } catch (Exception ex) {
        AppLog.Write("media: upload of " + final + " failed - " + ex.Message);
        return "{\"ok\":false,\"why\":" + Q("Could not save it - " + ex.Message) + "}";
      }
      AppLog.Write("media: uploaded " + final + " (" + (body.Length / 1024) + " KB)");
      return "{\"ok\":true,\"name\":" + Q(final) + "}";
    }

    // The one request here with a body. Handle's read loop stops at the blank
    // line, so the first slice of the body is already sitting in its buffer and
    // the rest still has to be pulled off the socket.
    static byte[] ReadBody(NetworkStream ns, string req, byte[] buf, int read,
                           int cap, out string why) {
      why = "";
      int hdrEnd = req.IndexOf("\r\n\r\n", StringComparison.Ordinal);
      if (hdrEnd < 0) { why = "The request headers never finished."; return null; }
      // Headers are ASCII and Handle decodes them one byte per char, so this
      // string index is also the byte index into buf.
      int start = hdrEnd + 4;

      int want;
      string cl = HeaderValue(req, "Content-Length");
      if (cl == null || !int.TryParse(cl.Trim(), out want) || want < 0) {
        why = "The upload didn't say how big it was."; return null;
      }
      if (want > cap) {
        why = "That file is " + (want / (1024 * 1024)) + " MB and the limit is "
            + (cap / (1024 * 1024)) + " MB.";
        // Read the body and throw it away before answering. Closing on a peer
        // that is still writing resets the connection, and a reset reaches
        // fetch() as a network failure - so the one refusal whose reason the
        // user most needs to read is the one that would arrive as "the app did
        // not answer". Bounded: past a point an unbounded body is not an
        // upload, and the 4s read timeout ends a stalled one regardless.
        Discard(ns, (long)want - (read - start), 128L * 1024 * 1024);
        return null;
      }

      var body = new byte[want];
      int have = read - start;
      if (have > want) have = want;               // a pipelined next request, not our body
      // System.Buffer spelled out: the WinRT references pull in
      // Windows.Storage.Streams.Buffer and the bare name is ambiguous.
      if (have > 0) System.Buffer.BlockCopy(buf, start, body, 0, have);
      while (have < want) {
        int n = ns.Read(body, have, want - have);
        if (n <= 0) { why = "The upload stopped part-way through."; return null; }
        have += n;
      }
      return body;
    }

    static void Discard(NetworkStream ns, long remaining, long bound) {
      var sink = new byte[64 * 1024];
      try {
        while (remaining > 0 && bound > 0) {
          int n = ns.Read(sink, 0, (int)Math.Min(sink.Length, Math.Min(remaining, bound)));
          if (n <= 0) return;
          remaining -= n; bound -= n;
        }
      } catch { }        // a peer that stops talking has made the point for us
    }

    static string MediaListJson() {
      var sb = new StringBuilder();
      // The folder path rides along so the customizer can say where to put
      // files instead of describing it in prose.
      sb.Append("{\"dir\":").Append(Q(MediaDir())).Append(",\"files\":[");
      bool first = true;
      try {
        var names = new List<string>();
        foreach (var f in Directory.GetFiles(MediaDir())) names.Add(Path.GetFileName(f));
        names.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names) {
          string mime = MediaMime(name);
          if (mime == null || !MediaNameRe.IsMatch(name)) continue;
          if (!first) sb.Append(',');
          first = false;
          string kind = mime.StartsWith("audio/") ? "audio"
                      : mime.StartsWith("video/") ? "video" : "image";
          sb.Append("{\"name\":").Append(Q(name)).Append(",\"kind\":").Append(Q(kind)).Append('}');
        }
      } catch { }
      return sb.Append("]}").ToString();
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

    // Like Send but never cached by default and with an explicit "private"
    // history: this variant predates the CORS-wildcard removal from Send, and
    // stays for the routes that also must not be cached (setup state, presets,
    // media with its own short max-age).
    static void SendPrivate(NetworkStream ns, int code, string contentType, byte[] body) {
      SendPrivate(ns, code, contentType, body, null);
    }

    static void SendPrivate(NetworkStream ns, int code, string contentType, byte[] body,
                            string cacheControl) {
      var sb = new StringBuilder();
      sb.Append("HTTP/1.1 ").Append(code).Append(code == 200 ? " OK" : " Error").Append("\r\n");
      sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
      sb.Append("Content-Length: ").Append(body == null ? 0 : body.Length).Append("\r\n");
      // Every type declared here is correct, and the media folder now takes
      // uploads - so a file whose bytes disagree with its extension must not be
      // re-read by the browser as something scriptable. Every route on this
      // origin is unauthenticated by design; content sniffing is the one way an
      // image could stop being an image.
      sb.Append("X-Content-Type-Options: nosniff\r\n");
      sb.Append("Cache-Control: ")
        .Append(cacheControl ?? "no-cache, no-store, must-revalidate").Append("\r\n");
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
      // No CORS header, deliberately. Every legitimate reader - OBS browser
      // sources, the dashboard, the customizer's frames - loads OUR pages
      // from this same host and port, so their fetches are same-origin and
      // need no CORS at all. The old wildcard here meant any website the
      // user browsed could read /np and /twitch and learn what this machine
      // plays and whose Twitch channel it runs - recon nobody signed up for.
      sb.Append("X-Content-Type-Options: nosniff\r\n");
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
