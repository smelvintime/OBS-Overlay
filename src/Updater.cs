// Self-update, one click from the dashboard.
//
// The repo distributes SOURCE ONLY. An unsigned exe downloaded from the
// internet collects antivirus reputation flags no matter what is inside it,
// so there has never been a published binary and this file must not become
// the thing that quietly starts shipping one. The update button therefore
// automates the documented manual update exactly: download the source ZIP
// from GitHub, build it locally with the compiler Windows already ships,
// swap the result in over this exe, restart. The program that comes up was
// compiled on this machine, the same as the day it was installed.
//
// The swap needs no helper script. Windows locks a running exe's BYTES but
// not its NAME: the live file can be renamed aside while this process keeps
// running from it, the fresh build copied in under the old name, and
// Restart() - spawn first, exit second - performs the handover it already
// knows how to do. The renamed original doubles as the rollback if anything
// goes wrong before the handover, and is swept up on the next clean start.
//
// Everything here is best-effort with the running app as the safe state: a
// failed download, a failed build or a failed copy leaves the current exe
// exactly where it was, still running, with the failure spelled out in
// /update/status and the log.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace NowPlaying {

  static class Updater {

    const string Repo = "smelvintime/OBS-Overlay";
    const string Branch = "main";

    // ------------------------------------------------------------------ state
    static readonly object _lock = new object();
    static string _state = "idle";    // idle|downloading|building|restarting|error
    static string _detail = "";
    static bool _busy;

    // The last check's answer, kept for half an hour: every dashboard load
    // asks, and GitHub's anonymous rate limit is per-network, not per-app.
    static DateTime _checkedUtc = DateTime.MinValue;
    static bool _available;
    static string _latestWhen = "", _latestNote = "", _checkError = "", _latestSha = "";

    static void Set(string state, string detail) {
      lock (_lock) { _state = state; _detail = detail; }
      AppLog.Write("update: " + detail);
    }

    public static string StatusJson() {
      lock (_lock) {
        return "{\"state\":" + TwitchChat.Qs(_state)
             + ",\"detail\":" + TwitchChat.Qs(_detail)
             + ",\"auto\":" + (AutoEnabled() ? "true" : "false")
             + ",\"autoWhy\":" + TwitchChat.Qs(_autoWhy)
             + ",\"build\":" + TwitchChat.Qs(BuildInfo.Version)
             + ",\"builtUtc\":" + TwitchChat.Qs(BuildInfo.BuiltUtc) + "}";
      }
    }

    // ------------------------------------------------------------------ check
    // "Is there anything newer?" = is the latest commit on main younger than
    // the moment this exe was compiled. The build stamp is the version scheme:
    // whoever builds after a push is up to date by construction, which is
    // exactly right for a source-only distribution.
    public static string CheckJson(bool force) {
      lock (_lock) {
        if (!force && (DateTime.UtcNow - _checkedUtc).TotalMinutes < 30)
          return ComposeCheck(_available, _latestWhen, _latestNote, _checkError);
      }

      bool avail = false; string when = "", note = "", err = "", sha = "";
      try {
        // A forced check exists for the minutes right after a push, which is
        // exactly when GitHub's edge is still serving its ~60s-cached copy of
        // the old head over a kept-alive connection. The header asks caches
        // to revalidate; the throwaway parameter makes the URL one no cache
        // has ever seen, for any cache that keys on it and ignores the ask.
        string url = "https://api.github.com/repos/" + Repo + "/commits/" + Branch
                   + (force ? "?fresh=" + DateTime.UtcNow.Ticks : "");
        string body = HttpGetString(url);
        object root = TwitchEvents.NavPublic(body);
        sha = Str(Nav(root, "sha"));
        string dateStr = Str(Nav(root, "commit", "committer", "date"));
        note = Str(Nav(root, "commit", "message"));
        int nl = note.IndexOfAny(new[] { '\r', '\n' });   // first line is the headline
        if (nl >= 0) note = note.Substring(0, nl);

        DateTime latest;
        if (!DateTime.TryParse(dateStr, CultureInfo.InvariantCulture,
                               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                               out latest))
          throw new Exception("unexpected answer from GitHub");
        avail = latest > BuiltUtc();
        // The date test alone is not safe to act on unattended. If this
        // machine's clock runs behind GitHub's, every build stamp lands before
        // every commit date and "newer" is permanently true - which for a
        // button is a wasted click and for an automatic updater is a restart
        // loop. The sha of the commit last installed ends that: whatever the
        // clocks say, a commit already installed is not an update.
        if (avail && sha.Length > 0
            && sha == (Program.GetPref("updateSha") ?? "").Trim()) avail = false;
        when = latest.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
      } catch (Exception ex) {
        err = Friendly(ex);
      }

      lock (_lock) {
        _checkedUtc = DateTime.UtcNow;
        _available = avail; _latestWhen = when; _latestNote = note; _checkError = err;
        _latestSha = sha;
      }
      return ComposeCheck(avail, when, note, err);
    }

    static string ComposeCheck(bool avail, string when, string note, string err) {
      return "{\"ok\":" + (err.Length == 0 ? "true" : "false")
           + ",\"error\":" + TwitchChat.Qs(err)
           + ",\"update\":" + (avail ? "true" : "false")
           + ",\"build\":" + TwitchChat.Qs(BuildInfo.Version)
           + ",\"latestWhen\":" + TwitchChat.Qs(when)
           + ",\"latestNote\":" + TwitchChat.Qs(note) + "}";
    }

    static DateTime BuiltUtc() {
      // BuildInfo.BuiltUtc is our own build.ps1's constant, so the format is
      // ours to rely on - but a bad parse must not brick the check.
      DateTime t;
      if (DateTime.TryParseExact(BuildInfo.BuiltUtc.Replace(" UTC", ""),
                                 "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                                 DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                 out t))
        return t;
      return DateTime.UtcNow;   // unparseable stamp reads as "current", never as "update forever"
    }

    // ------------------------------------------------------------ automatic
    // The point of this is that nobody ever has to press the button. The
    // problem with it is that an update ends in a restart, and a restart while
    // the overlay is on someone's stream is a browser source going blank in
    // front of an audience. So the interesting question is never "how often" -
    // it is "when is a three second restart free".
    //
    // Live on Twitch is the answer that matters, and it can be asked directly:
    // Helix says whether the channel is broadcasting right now. A League game
    // is the other, because the end-of-game result is read off a screen that
    // is only up for about a minute and a restart through it loses that game's
    // announcement. Neither of those is a guess.
    //
    // If it is never quiet, the update simply waits. A stale build is a much
    // smaller problem than a black source mid-fight, and the wait ends by
    // itself the moment the stream does.

    static DateTime _autoNextUtc = DateTime.MinValue;
    static string _autoWhy = "";

    public static string AutoWhy { get { return _autoWhy; } }

    // Default on. This is the whole feature; a client who has to switch it on
    // is a client who never switches it on.
    internal static bool AutoEnabled() {
      string v = (Program.GetPref("autoUpdate") ?? "").Trim().ToLowerInvariant();
      return v != "0" && v != "off" && v != "false" && v != "no";
    }

    public static void StartAuto() {
      var t = new Thread(AutoLoop);
      t.IsBackground = true;
      t.Start();
    }

    static void AutoLoop() {
      // Let the port bind, the tray icon appear and the first poll land before
      // reaching for the network - and before deciding whether anything is
      // attached, which takes OBS a few seconds to do after a start.
      Thread.Sleep(40000);
      while (true) {
        try { AutoPass(); } catch { /* never let this thread be why the app dies */ }
        Thread.Sleep(4 * 60 * 1000);
      }
    }

    static void AutoPass() {
      if (!AutoEnabled()) { _autoWhy = "automatic updates are switched off"; return; }
      lock (_lock) { if (_busy) return; }
      if (DateTime.UtcNow < _autoNextUtc) return;      // backing off after a failure

      string why;
      if (!QuietNow(out why)) { _autoWhy = "waiting: " + why; return; }

      // The cached answer is fine here. This runs every four minutes and the
      // check caches for thirty; forcing it each pass would be asking GitHub
      // about a repository that changes a few times a week.
      CheckJson(false);
      bool avail; string note;
      lock (_lock) { avail = _available; note = _latestNote; }
      if (!avail) { _autoWhy = "up to date"; return; }

      // Re-tested immediately before starting, because the check can take a
      // few seconds over a slow line and "quiet" is a statement about now.
      if (!QuietNow(out why)) { _autoWhy = "waiting: " + why; return; }

      // An hour before another attempt. A build that fails - a compiler that
      // is not there, a half-pushed commit - would otherwise be retried every
      // four minutes for as long as the machine is on.
      _autoNextUtc = DateTime.UtcNow.AddHours(1);
      _autoWhy = "installing";
      AppLog.Write("update: installing automatically - " + note);
      string err;
      if (!Begin(out err)) _autoWhy = "could not start: " + err;
    }

    static bool QuietNow(out string why) {
      if (LeagueStats.BusyWithGame()) { why = "a League game is in progress"; return false; }

      bool known;
      if (StreamLive(out known)) { why = "the channel is live"; return false; }

      // Twitch could not be asked - not configured, or the API is down. Fall
      // back to the weaker signal: an attached OBS browser source means
      // something of ours is on a canvas somewhere, and without knowing
      // whether that canvas is being broadcast the safe reading is that it is.
      if (!known && (Program.SpectrumClients > 0 || Program.TwitchClients > 0)) {
        why = "OBS sources are attached and Twitch can't be asked if the channel is live";
        return false;
      }
      why = "";
      return true;
    }

    // known=false means the question could not be put, which is different from
    // an answer of "offline" and must not be read as one.
    static bool StreamLive(out bool known) {
      known = false;
      try {
        if (!TwitchEvents.ApiReady) return false;
        int status;
        string body = TwitchEvents.Helix(
          "https://api.twitch.tv/helix/streams?user_id=" + TwitchEvents.BroadcasterId, out status);
        if (status != 200 || body == null) return false;
        var data = Nav(TwitchEvents.NavPublic(body), "data") as object[];
        if (data == null) return false;
        known = true;
        return data.Length > 0;          // one entry = broadcasting right now
      } catch { return false; }
    }

    // -------------------------------------------------------------------- run
    public static bool Begin(out string err) {
      err = "";
      lock (_lock) {
        if (_busy) { err = "an update is already running"; return false; }
        _busy = true; _state = "downloading"; _detail = "Starting the update...";
      }
      var t = new Thread(Run);
      t.IsBackground = true;
      t.Start();
      return true;
    }

    static void Run() {
      try {
        string dir = Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
          "NowPlayingOverlay", "update");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        Directory.CreateDirectory(dir);

        Set("downloading", "Downloading the latest source from GitHub...");
        string zip = Path.Combine(dir, "source.zip");
        HttpGetFile("https://codeload.github.com/" + Repo + "/zip/refs/heads/" + Branch, zip);

        Set("downloading", "Unpacking...");
        string srcRoot = Path.Combine(dir, "source");
        System.IO.Compression.ZipFile.ExtractToDirectory(zip, srcRoot);
        string buildPs = FindBuildScript(srcRoot);
        if (buildPs == null) throw new Exception("the downloaded source has no build.ps1 in it");

        Set("building", "Building the new version on this PC...");
        string outDir = Path.Combine(dir, "dist");
        string newExe = RunBuild(buildPs, outDir);

        // ---- the swap ------------------------------------------------------
        Set("restarting", "Swapping in the new build and restarting...");
        string cur = System.Reflection.Assembly.GetExecutingAssembly().Location;
        string old = cur + ".old";
        try { if (File.Exists(old)) File.Delete(old); } catch { }
        // Legal while running: the lock is on the file's contents, not its
        // directory entry, and this process keeps running from the renamed file.
        File.Move(cur, old);
        try {
          File.Copy(newExe, cur);
        } catch (Exception) {
          // Put the world back before failing - a missing exe at the path the
          // user knows would turn the next manual start into a mystery.
          try { File.Move(old, cur); } catch { }
          throw;
        }

        // Recorded BEFORE the handover, because after it there is no more of
        // this process to record anything. This is what stops a machine whose
        // clock lags GitHub from updating to the same commit forever.
        lock (_lock) { if (_latestSha.Length > 0) Program.SetPref("updateSha", _latestSha); }

        AppLog.Write("update: new build in place, handing over");
        Program.Restart(800, "updater");

        // Restart exits this process; still being here after a grace period
        // means the spawn failed and it chose to stay up (its own rule). The
        // new exe IS in place, so the next manual start runs it - say so
        // instead of showing "restarting" forever.
        Thread.Sleep(15000);
        Set("error", "The new build is installed but the restart didn't take - "
                   + "close the app from the tray and start it once by hand.");
        lock (_lock) _busy = false;
      } catch (Exception ex) {
        Set("error", Friendly(ex));
        lock (_lock) _busy = false;
      }
    }

    static string RunBuild(string buildPs, string outDir) {
      string psExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                  @"WindowsPowerShell\v1.0\powershell.exe");
      var psi = new ProcessStartInfo(psExe,
        "-NoProfile -ExecutionPolicy Bypass -File \"" + buildPs + "\" -OutDir \"" + outDir + "\"");
      psi.UseShellExecute = false;
      psi.CreateNoWindow = true;
      psi.RedirectStandardOutput = true;
      psi.RedirectStandardError = true;

      var sbErr = new StringBuilder();
      string so;
      int code;
      using (var p = Process.Start(psi)) {
        // stderr on its own thread: two ReadToEnd() calls in a row deadlock
        // the moment the second pipe's buffer fills while the first blocks.
        var errT = new Thread(delegate() {
          try { sbErr.Append(p.StandardError.ReadToEnd()); } catch { }
        });
        errT.IsBackground = true;
        errT.Start();
        so = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(240000)) {
          try { p.Kill(); } catch { }
          throw new Exception("the build took too long and was stopped");
        }
        errT.Join(2000);
        code = p.ExitCode;
      }

      string newExe = Path.Combine(outDir, "NowPlayingOverlay.exe");
      if (code != 0 || !File.Exists(newExe))
        throw new Exception("the build failed: " + Tail(so + "\n" + sbErr, 500));
      return newExe;
    }

    // The downloaded ZIP wraps everything in one "<repo>-<branch>" folder;
    // tolerate it being at the root too, in case that ever changes.
    static string FindBuildScript(string root) {
      string direct = Path.Combine(root, "build.ps1");
      if (File.Exists(direct)) return direct;
      foreach (var d in Directory.GetDirectories(root)) {
        string p = Path.Combine(d, "build.ps1");
        if (File.Exists(p)) return p;
      }
      return null;
    }

    // ---------------------------------------------------------------- cleanup
    // The renamed original from the last update. This code running IS the
    // proof the swap worked, so the leftover can go - retried briefly because
    // the old process may still be letting go of the file.
    public static void CleanupAfterSwap() {
      var t = new Thread(delegate() {
        string old = System.Reflection.Assembly.GetExecutingAssembly().Location + ".old";
        for (int i = 0; i < 10; i++) {
          try {
            if (!File.Exists(old)) return;
            File.Delete(old);
            AppLog.Write("update: removed the previous build's leftover");
            return;
          } catch { }
          Thread.Sleep(1000);
        }
      });
      t.IsBackground = true;
      t.Start();
    }

    // ------------------------------------------------------------------- http
    static HttpWebRequest Req(string url) {
      // Twitch's startup already forces TLS 1.2+ process-wide, but the updater
      // must not depend on which feature started first.
      ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
      var r = (HttpWebRequest)WebRequest.Create(url);
      r.UserAgent = "NowPlayingOverlay/" + BuildInfo.Version + " (updater)";
      r.Timeout = 30000;
      r.ReadWriteTimeout = 120000;
      return r;
    }

    static string HttpGetString(string url) {
      var r = Req(url);
      r.Accept = "application/vnd.github+json";
      r.Headers[HttpRequestHeader.CacheControl] = "no-cache";
      using (var resp = (HttpWebResponse)r.GetResponse())
      using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
        return sr.ReadToEnd();
    }

    static void HttpGetFile(string url, string path) {
      var r = Req(url);
      using (var resp = (HttpWebResponse)r.GetResponse())
      using (var s = resp.GetResponseStream())
      using (var f = File.Create(path)) {
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) f.Write(buf, 0, n);
      }
    }

    static string Friendly(Exception ex) {
      var wex = ex as WebException;
      if (wex != null) {
        var resp = wex.Response as HttpWebResponse;
        if (resp != null && ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429))
          return "GitHub is rate-limiting checks from this network - try again in an hour.";
        return "Couldn't reach GitHub - check the internet connection and try again.";
      }
      return ex.Message;
    }

    // ------------------------------------------------------------------- json
    static object Nav(object o, params string[] path) {
      foreach (var key in path) {
        var d = o as Dictionary<string, object>;
        if (d == null) return null;
        if (!d.TryGetValue(key, out o)) return null;
      }
      return o;
    }
    static string Str(object o) { return o == null ? "" : Convert.ToString(o); }

    static string Tail(string s, int n) {
      s = (s ?? "").Trim();
      return s.Length <= n ? s : s.Substring(s.Length - n);
    }
  }
}
