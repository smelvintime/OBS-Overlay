// Twitch follower / subscriber events and totals.
//
// Two halves that answer two different questions:
//
//   EventSub over a websocket  - "who just followed?"  Real time, push.
//   Helix polled every 60s     - "how many in total?"  Slow, pull.
//
// The split matters. Alerts cannot be polled: by the time a 60s poll noticed a
// follow the moment has passed on stream. Totals cannot be pushed: EventSub has
// no "the count changed" event. So neither half can do the other's job.
//
// It also solves a problem polling alone cannot. A Helix subscription object
// carries no timestamp - there is no field to sort by - so "who subscribed most
// recently" is not a question the REST API can answer at all. The event stream
// answers it by construction, because it arrives when it happens.
//
// Everything here is best-effort. A missing config, an expired token or a dead
// socket must leave the overlay showing a song and no stats, never a crash and
// never a fake zero.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace NowPlaying {

  static class TwitchEvents {

    // ---------------------------------------------------------------- config
    static string _channel = "";
    static string _clientId = "";
    static string _token = "";
    static string _clientSecret = "";
    static string _refreshToken = "";
    static string _configPath;
    static int _followerGoal;          // 0 = round up automatically
    static int _subGoal;

    // Both optional: a config with only clientId/apiToken behaves exactly as
    // before, expiring every few hours with the tray/customizer telling the
    // user to regenerate it by hand. Add these two and that stops mattering.
    static bool CanRefresh { get { return _clientSecret.Length > 0 && _refreshToken.Length > 0; } }

    static string _broadcasterId = "";

    // "off" until configured, then one of: connecting, live, bad-token,
    // missing-scope, not-affiliate, error. The pages render each differently -
    // an expired token has to look different from "nobody has followed yet".
    static volatile string _status = "off";
    static volatile string _detail = "";

    public static bool Configured { get { return _clientId.Length > 0 && _token.Length > 0; } }
    public static string Status { get { return _status; } }
    public static string Detail { get { return _detail; } }

    // ----------------------------------------------------------------- state
    static volatile int _followerTotal = -1;   // -1 = not known yet, distinct from 0
    static volatile int _subTotal = -1;
    static volatile int _subPoints = -1;

    static readonly object _stateLock = new object();
    static string _lastFollower = "";
    static string _lastFollowerAt = "";
    static string _lastSub = "";
    static string _lastSubAt = "";
    static string _lastSubTier = "";

    // ---------------------------------------------------------------- alerts
    // A ring of recent alerts with monotonic sequence numbers. Each SSE client
    // remembers the last sequence it was sent, so a client that connects late
    // or reconnects mid-stream resumes without replaying the whole history and
    // without needing a queue of its own.
    class Alert { public long Seq; public string Json; }
    static readonly List<Alert> _alerts = new List<Alert>();
    static long _seq;
    const int MaxAlerts = 100;

    static void Push(string json) {
      lock (_alerts) {
        _seq++;
        _alerts.Add(new Alert { Seq = _seq, Json = json });
        while (_alerts.Count > MaxAlerts) _alerts.RemoveAt(0);
      }
    }

    public static string[] Since(long lastSeq, out long newSeq) {
      lock (_alerts) {
        newSeq = _seq;
        if (lastSeq >= _seq) return new string[0];
        var outp = new List<string>();
        foreach (var a in _alerts) if (a.Seq > lastSeq) outp.Add(a.Json);
        return outp.ToArray();
      }
    }

    public static long CurrentSeq { get { lock (_alerts) { return _seq; } } }

    // ------------------------------------------------------------ config I/O
    // The exe normally lives in dist\ while twitch-config.json sits in the repo
    // root beside twitch-bot.ps1, so walk up a few levels rather than forcing
    // the user to keep two copies in sync.
    static string FindConfig() {
      try {
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        for (int i = 0; i < 4 && dir != null; i++) {
          string p = Path.Combine(dir, "twitch-config.json");
          if (File.Exists(p)) return p;
          var parent = Directory.GetParent(dir);
          dir = parent == null ? null : parent.FullName;
        }
      } catch { }
      return null;
    }

    // The chat bot reads the same file for its own credentials, and the Helix
    // helpers below are the only place that knows how to talk to Twitch's REST
    // API correctly (client-id pairing, error bodies never returned as data).
    // Sharing them beats a second, subtly different copy.
    internal static string FindConfigPath() { return FindConfig(); }

    // ------------------------------------------------------------ setup wizard
    // The /setup page drives everything here. The goal is that a person who has
    // never seen a terminal gets from a fresh download to working alerts by
    // filling in three fields and clicking one Twitch consent screen: the app
    // itself runs the OAuth authorization-code flow against its own local port,
    // so no CLI, no token generator sites, and nothing pasted by hand.

    // Where to create the config when none exists yet: beside the exe, which is
    // the layout a downloaded release has. FindConfig() walking upward still
    // honours a repo-root file when developing.
    static string ConfigPathOrDefault() {
      string p = FindConfig();
      if (p != null) return p;
      try {
        return Path.Combine(
          Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
          "twitch-config.json");
      } catch { return "twitch-config.json"; }
    }

    // True after the OAuth flow succeeds while the Twitch side was already
    // running with older credentials - threads mid-flight hold the old token,
    // and a clean restart is simpler and better tested than hot-swapping it.
    static volatile bool _needsRestart;

    internal static string RedirectUri(int port) {
      return "http://localhost:" + port + "/oauth/callback";
    }

    internal static string SetupStateJson(int port) {
      var sb = new StringBuilder();
      sb.Append('{');
      sb.Append("\"configured\":").Append(Configured ? "true" : "false").Append(',');
      sb.Append("\"status\":").Append(Q(_status)).Append(',');
      sb.Append("\"detail\":").Append(Q(_detail)).Append(',');
      sb.Append("\"channel\":").Append(Q(_channel)).Append(',');
      sb.Append("\"clientId\":").Append(Q(_clientId)).Append(',');
      sb.Append("\"hasSecret\":").Append(_clientSecret.Length > 0 ? "true" : "false").Append(',');
      sb.Append("\"hasToken\":").Append(_token.Length > 0 ? "true" : "false").Append(',');
      sb.Append("\"hasRefresh\":").Append(_refreshToken.Length > 0 ? "true" : "false").Append(',');
      sb.Append("\"needsRestart\":").Append(_needsRestart ? "true" : "false").Append(',');
      sb.Append("\"redirectUri\":").Append(Q(RedirectUri(port))).Append(',');
      // Only the file name, never the path. The full path carries the Windows
      // account name, and the wizard only needs to say "it is saved next to the
      // app" - /diag is the place that shows the real location, to someone who
      // has already opened it deliberately.
      sb.Append("\"configFile\":").Append(Q(FindConfigPath() == null ? "" : "twitch-config.json"));
      sb.Append('}');
      return sb.ToString();
    }

    // Writes the identity half of the config (channel + app credentials). The
    // token half arrives later through the OAuth callback. Values are merged
    // into whatever file exists so a hand-maintained config keeps its comments
    // and its chat-bot half untouched.
    internal static bool SetupSave(string channel, string clientId, string clientSecret, out string error) {
      error = "";
      channel = (channel ?? "").Trim().TrimStart('#');
      clientId = (clientId ?? "").Trim();
      clientSecret = (clientSecret ?? "").Trim();
      if (channel.Length == 0) { error = "channel name is required"; return false; }
      if (clientId.Length == 0) { error = "Client ID is required"; return false; }
      try {
        string p = ConfigPathOrDefault();
        Dictionary<string, object> cfg = null;
        if (File.Exists(p)) {
          try { cfg = ReadConfig(p); } catch { cfg = null; }
          if (cfg == null) {
            // "Fix or delete it first" was a dead end for anyone who doesn't
            // edit JSON - and an unreadable config's tokens are already
            // unusable, so there is nothing left in it worth blocking on.
            // Move it aside (kept as evidence, never deleted) and start clean.
            try {
              string broken = p + ".broken";
              if (File.Exists(broken)) File.Delete(broken);
              File.Move(p, broken);
              AppLog.Write("setup: unreadable twitch-config.json moved aside to " + broken);
              cfg = new Dictionary<string, object>();
              cfg["followerGoal"] = 0;
              cfg["subGoal"] = 0;
            } catch (Exception mex) {
              error = "the existing twitch-config.json could not be read, and moving it aside failed ("
                    + mex.Message + ") - delete the file by hand and try again";
              return false;
            }
          }
        } else {
          cfg = new Dictionary<string, object>();
          cfg["followerGoal"] = 0;
          cfg["subGoal"] = 0;
        }
        cfg["channel"] = channel;
        cfg["clientId"] = clientId;
        if (clientSecret.Length > 0) cfg["clientSecret"] = clientSecret;
        Files.WriteAtomic(p, WriteConfig(cfg));
        _configPath = p;
        LoadConfig();               // pick the new identity up immediately
        AppLog.Write("setup: saved channel/client for \"" + channel + "\"");
        return true;
      } catch (Exception ex) {
        error = "could not write twitch-config.json: " + ex.Message;
        return false;
      }
    }

    // One-shot nonce tying the callback to a start we actually issued.
    static string _oauthState = "";

    internal static string OAuthStartUrl(int port) {
      _oauthState = Guid.NewGuid().ToString("N");
      return "https://id.twitch.tv/oauth2/authorize"
        + "?response_type=code"
        + "&client_id=" + Uri.EscapeDataString(_clientId)
        + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri(port))
        + "&scope=" + Uri.EscapeDataString("moderator:read:followers channel:read:subscriptions")
        + "&state=" + _oauthState;
    }

    internal static bool OAuthReady { get { return _clientId.Length > 0 && _clientSecret.Length > 0; } }

    // The bot's consent flow: same Twitch app, same callback URL (Twitch
    // matches the redirect to the letter, so a second URL would mean everyone
    // re-editing their app registration), but chat scopes and its own state
    // nonce so the callback can tell the two flows apart. force_verify makes
    // Twitch actually show the account screen instead of silently reusing the
    // broadcaster's session - the whole point is signing in as the account the
    // bot should speak as, which is usually not the one the browser holds.
    static string _botOauthState = "";

    internal static string BotOAuthStartUrl(int port) {
      _botOauthState = Guid.NewGuid().ToString("N");
      return "https://id.twitch.tv/oauth2/authorize"
        + "?response_type=code"
        + "&client_id=" + Uri.EscapeDataString(_clientId)
        + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri(port))
        + "&scope=" + Uri.EscapeDataString("chat:read chat:edit")
        + "&force_verify=true"
        + "&state=" + _botOauthState;
    }

    internal static bool IsBotOAuthState(string state) {
      return _botOauthState.Length > 0 && state == _botOauthState;
    }

    // Exchanges an authorization code for a token pair. Shared by both consent
    // flows, which differ only in the scopes asked for and where the result
    // is stored.
    static bool ExchangeCode(int port, string code, out string access, out string refresh, out string error) {
      access = ""; refresh = ""; error = "";
      try {
        string body = "grant_type=authorization_code"
          + "&code=" + Uri.EscapeDataString(code ?? "")
          + "&client_id=" + Uri.EscapeDataString(_clientId)
          + "&client_secret=" + Uri.EscapeDataString(_clientSecret)
          + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri(port));
        var req = (HttpWebRequest)WebRequest.Create("https://id.twitch.tv/oauth2/token");
        req.Method = "POST";
        req.Timeout = 15000;
        req.ContentType = "application/x-www-form-urlencoded";
        req.UserAgent = "NowPlayingOverlay";
        var bytes = Encoding.UTF8.GetBytes(body);
        req.ContentLength = bytes.Length;
        using (var rs = req.GetRequestStream()) rs.Write(bytes, 0, bytes.Length);
        string resp;
        using (var wr = (HttpWebResponse)req.GetResponse())
        using (var sr = new StreamReader(wr.GetResponseStream(), Encoding.UTF8))
          resp = sr.ReadToEnd();

        var root = _ser.DeserializeObject(resp);
        access = SNav(root, "access_token");
        refresh = SNav(root, "refresh_token");
        if (access.Length == 0) { error = "Twitch's reply carried no token"; return false; }
        return true;
      } catch (WebException we) {
        string detail = "";
        try {
          var r = we.Response as HttpWebResponse;
          if (r != null) using (var sr = new StreamReader(r.GetResponseStream())) detail = sr.ReadToEnd();
        } catch { }
        // The error body here is Twitch's own ("invalid client", "invalid
        // grant") and carries no secrets - it is safe and useful to show.
        error = "Twitch refused the exchange: " + we.Message + (detail.Length > 0 ? " - " + detail : "");
        return false;
      } catch (Exception ex) {
        error = "unexpected failure: " + ex.Message;
        return false;
      }
    }

    // Which account said yes, straight from Twitch. Fills a blank channel,
    // catches authorizing as the wrong account, and names the bot.
    static string WhoAuthorized(string access) {
      try {
        var vreq = (HttpWebRequest)WebRequest.Create("https://id.twitch.tv/oauth2/validate");
        vreq.Timeout = 10000;
        vreq.Headers["Authorization"] = "OAuth " + access;
        vreq.UserAgent = "NowPlayingOverlay";
        using (var vr = (HttpWebResponse)vreq.GetResponse())
        using (var sr = new StreamReader(vr.GetResponseStream(), Encoding.UTF8))
          return SNav(_ser.DeserializeObject(sr.ReadToEnd()), "login");
      } catch { return ""; }
    }

    // The broadcaster's callback half: learns which account authorized (so a
    // blank channel can be filled in from it), persists the token pair, and
    // either starts the feed (first-time setup) or asks for a restart.
    internal static bool OAuthComplete(int port, string code, string state, out string error) {
      error = "";
      if (state != _oauthState || _oauthState.Length == 0) {
        error = "this authorization did not come from this app's setup page (state mismatch) - start again from Setup";
        return false;
      }
      _oauthState = "";
      if (!OAuthReady) { error = "Client ID and Secret must be saved before connecting"; return false; }
      string access, refresh;
      if (!ExchangeCode(port, code, out access, out refresh, out error)) {
        AppLog.Write("setup: OAuth exchange failed: " + error);
        return false;
      }
      string login = WhoAuthorized(access);
      // This flow reuses whatever Twitch session the browser already holds
      // (no force_verify - re-consent should stay one click), which makes
      // "connected as the wrong account" the easiest mistake to make and the
      // most expensive to find: count polls work with anyone's token, so the
      // numbers keep moving, while the follow subscription needs the
      // channel's own account and dies quietly - alerts and the newest
      // follower just never update. Refuse the mismatch here, with the fix
      // in the message, instead of letting it half-work for weeks.
      if (login.Length > 0 && _channel.Length > 0
          && !login.Equals(_channel, StringComparison.OrdinalIgnoreCase)) {
        error = "Twitch was signed in as \"" + login + "\" but this channel is \"" + _channel
              + "\". Alerts need the channel's own account: open twitch.tv, switch to "
              + _channel + " (the \"Not you?\" link on Twitch's page), then press Connect again.";
        AppLog.Write("setup: OAuth account mismatch - authorized \"" + login
                   + "\", channel \"" + _channel + "\" - tokens not saved");
        return false;
      }
      try {
        string p = ConfigPathOrDefault();
        Dictionary<string, object> cfg = File.Exists(p) ? ReadConfig(p) : new Dictionary<string, object>();
        if (cfg == null) cfg = new Dictionary<string, object>();
        cfg["apiToken"] = access;
        cfg["refreshToken"] = refresh;
        if (SNav2(cfg, "channel").Length == 0 && login.Length > 0) cfg["channel"] = login;
        Files.WriteAtomic(p, WriteConfig(cfg));
        _configPath = p;
      } catch (Exception ex) {
        error = "could not write twitch-config.json: " + ex.Message;
        AppLog.Write("setup: OAuth save failed: " + ex.Message);
        return false;
      }

      bool wasRunning = _status != "off";
      LoadConfig();
      AppLog.Write("setup: OAuth connected as \"" + (login.Length > 0 ? login : "(unknown)")
                 + "\", channel \"" + _channel + "\"");
      if (wasRunning) {
        // Threads already hold the old credentials; a restart is the clean
        // way to apply the new ones everywhere at once.
        _needsRestart = true;
      } else {
        Start();
      }
      return true;
    }

    // The bot's callback half. The account that authorized IS the bot's
    // username - the one fact the manual setup made people type, and the one
    // they most often got wrong (a token minted on the main account with the
    // bot's name typed in the box, or the reverse). Chat is self-contained
    // enough to cycle in place, so no restart is asked for.
    internal static bool OAuthCompleteBot(int port, string code, string state, out string error) {
      error = "";
      if (state != _botOauthState || _botOauthState.Length == 0) {
        error = "this authorization did not come from this app's bot setup (state mismatch) - start again from the Chat bot tab";
        return false;
      }
      _botOauthState = "";
      if (!OAuthReady) { error = "the Twitch app's Client ID and Secret must be saved first - run the main Twitch setup"; return false; }
      string access, refresh;
      if (!ExchangeCode(port, code, out access, out refresh, out error)) {
        AppLog.Write("setup: bot OAuth exchange failed: " + error);
        return false;
      }
      string login = WhoAuthorized(access);
      if (login.Length == 0) {
        // Without the login there is no NICK to connect with; better to say so
        // now than to write a config that fails with a worse message later.
        error = "Twitch accepted the connection but would not say which account authorized - try again";
        return false;
      }
      try {
        string p = ConfigPathOrDefault();
        Dictionary<string, object> cfg = File.Exists(p) ? ReadConfig(p) : new Dictionary<string, object>();
        if (cfg == null) cfg = new Dictionary<string, object>();
        cfg["botUsername"] = login;
        cfg["oauthToken"] = access;
        cfg["botRefreshToken"] = refresh;
        Files.WriteAtomic(p, WriteConfig(cfg));
        _configPath = p;
      } catch (Exception ex) {
        error = "could not write twitch-config.json: " + ex.Message;
        AppLog.Write("setup: bot OAuth save failed: " + ex.Message);
        return false;
      }
      AppLog.Write("setup: bot OAuth connected as \"" + login + "\"");
      TwitchChat.ReloadAfterSetup();
      return true;
    }

    // "I don't use Twitch": write a minimal config so the file exists and the
    // first-run wizard stops opening, while everything Twitch stays cleanly
    // off. Never touches an existing file.
    internal static void SetupSkip() {
      try {
        if (FindConfig() != null) return;
        var cfg = new Dictionary<string, object>();
        cfg["channel"] = "";
        cfg["followerGoal"] = 0;
        cfg["subGoal"] = 0;
        Files.WriteAtomic(ConfigPathOrDefault(), WriteConfig(cfg));
        AppLog.Write("setup: skipped - minimal config written");
      } catch (Exception ex) {
        AppLog.Write("setup: skip failed: " + ex.Message);
      }
    }

    static string SNav2(Dictionary<string, object> d, string key) {
      object v;
      if (d != null && d.TryGetValue(key, out v) && v != null) return Convert.ToString(v).Trim();
      return "";
    }

    internal static Dictionary<string, object> ReadConfig(string path) {
      return new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path))
             as Dictionary<string, object>;
    }

    internal static string Helix(string url, out int httpStatus) {
      return HelixGetAuth(url, out httpStatus);
    }

    internal static string BroadcasterId { get { return _broadcasterId; } }
    internal static bool ApiReady { get { return _broadcasterId.Length > 0; } }

    internal static object NavPublic(string json, params string[] path) {
      try { return Nav(_ser.DeserializeObject(json), path); } catch { return null; }
    }
    internal static string SNavPublic(object o, params string[] path) { return SNav(o, path); }

    static string StatePath() {
      string dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NowPlayingOverlay");
      Directory.CreateDirectory(dir);
      return Path.Combine(dir, "twitch-state.txt");
    }

    // The most recent subscriber only ever arrives as an event, so without this
    // a restart would blank that half of the stats box until someone happened to
    // subscribe again. Followers are re-seeded from Helix instead.
    static void LoadState() {
      try {
        string p = StatePath();
        if (!File.Exists(p)) return;
        foreach (var line in File.ReadAllLines(p)) {
          int eq = line.IndexOf('=');
          if (eq <= 0) continue;
          string k = line.Substring(0, eq), v = line.Substring(eq + 1);
          if (k == "lastSub") _lastSub = v;
          else if (k == "lastSubAt") _lastSubAt = v;
          else if (k == "lastSubTier") _lastSubTier = v;
        }
      } catch { }
    }

    static void SaveState() {
      try {
        string body;
        lock (_stateLock) {
          body = "lastSub=" + _lastSub + "\r\n"
               + "lastSubAt=" + _lastSubAt + "\r\n"
               + "lastSubTier=" + _lastSubTier + "\r\n";
        }
        Files.WriteAtomic(StatePath(), body);
      } catch { }
    }

    static void LoadConfig() {
      string p = FindConfig();
      if (p == null) { _status = "off"; _detail = "no twitch-config.json found"; return; }
      try {
        var ser = new JavaScriptSerializer();
        var cfg = ser.DeserializeObject(File.ReadAllText(p)) as Dictionary<string, object>;
        if (cfg == null) { _status = "error"; _detail = "twitch-config.json is not valid JSON"; return; }

        _configPath = p;
        _channel = Str(cfg, "channel").Trim().TrimStart('#').ToLowerInvariant();
        _clientId = Str(cfg, "clientId").Trim();
        _token = Str(cfg, "apiToken").Trim();
        // Token generators hand these out with the chat prefix attached; Helix
        // wants the bare token, so accept either and normalise.
        if (_token.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase)) _token = _token.Substring(6);
        _clientSecret = Str(cfg, "clientSecret").Trim();
        _refreshToken = Str(cfg, "refreshToken").Trim();
        _followerGoal = Num(cfg, "followerGoal");
        _subGoal = Num(cfg, "subGoal");

        if (_channel.Length == 0 || _channel.StartsWith("your_")) {
          _status = "off"; _detail = "channel is not set in twitch-config.json"; return;
        }
        if (_clientId.Length == 0 || _clientId.IndexOf("PASTE", StringComparison.OrdinalIgnoreCase) >= 0) {
          _status = "off"; _detail = "clientId is not set in twitch-config.json"; return;
        }
        if (_token.Length == 0 || _token.IndexOf("PASTE", StringComparison.OrdinalIgnoreCase) >= 0) {
          _status = "off"; _detail = "apiToken is not set in twitch-config.json"; return;
        }
      } catch (Exception e) {
        _status = "error"; _detail = "could not read twitch-config.json: " + SafeParseError(e);
      }
    }

    // JavaScriptSerializer's parse-error message embeds the ENTIRE input string
    // after the position marker - which for this file means the client secret,
    // API token and refresh token, verbatim, surfaced on /diag and /twitch and
    // then pasted into a chat or an email the moment someone asks for help
    // (which is literally what the /diag copy block is for). Keep the useful
    // half - what was wrong and where - and drop everything after the first
    // brace or line break, which is where the raw file starts.
    internal static string SafeParseError(Exception e) {
      string m = e.Message ?? "";
      int cut = m.IndexOf('{');
      if (cut < 0) cut = m.IndexOf('\n');
      if (cut >= 0) m = m.Substring(0, cut);
      m = m.Trim();
      if (m.Length > 200) m = m.Substring(0, 200);
      if (m.Length == 0) m = e.GetType().Name;
      return m + " (the file's contents are not shown here because they contain tokens - "
           + "open it in a text editor and look near the position number above, usually a missing comma)";
    }

    static string Str(Dictionary<string, object> d, string key) {
      object v;
      if (d != null && d.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
      return "";
    }

    static int Num(Dictionary<string, object> d, string key) {
      object v;
      if (d != null && d.TryGetValue(key, out v) && v != null) {
        try { return Convert.ToInt32(v); } catch { }
      }
      return 0;
    }

    // ------------------------------------------------------------------ JSON
    static readonly JavaScriptSerializer _ser = new JavaScriptSerializer();

    static object Nav(object o, params string[] path) {
      foreach (var key in path) {
        var d = o as Dictionary<string, object>;
        if (d == null) return null;
        if (!d.TryGetValue(key, out o)) return null;
      }
      return o;
    }

    static string SNav(object o, params string[] path) {
      var v = Nav(o, path);
      return v == null ? "" : Convert.ToString(v);
    }

    static int INav(object o, params string[] path) {
      var v = Nav(o, path);
      if (v == null) return 0;
      try { return Convert.ToInt32(v); } catch { return 0; }
    }

    static bool BNav(object o, params string[] path) {
      var v = Nav(o, path);
      if (v is bool) return (bool)v;
      return false;
    }

    static string Q(string s) {
      if (s == null) return "\"\"";
      var sb = new StringBuilder("\"");
      foreach (char c in s) {
        if (c == '"') sb.Append("\\\"");
        else if (c == '\\') sb.Append("\\\\");
        else if (c == '\n') sb.Append("\\n");
        else if (c == '\r') sb.Append("\\r");
        else if (c == '\t') sb.Append("\\t");
        else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
        else sb.Append(c);
      }
      return sb.Append('"').ToString();
    }

    // ----------------------------------------------------------------- Helix
    static string HelixGet(string url, out int httpStatus) {
      httpStatus = 0;
      try {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = "GET";
        req.Timeout = 10000;
        req.Headers["Client-Id"] = _clientId;
        req.Headers["Authorization"] = "Bearer " + _token;
        req.UserAgent = "NowPlayingOverlay";
        using (var resp = (HttpWebResponse)req.GetResponse()) {
          httpStatus = (int)resp.StatusCode;
          using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return sr.ReadToEnd();
        }
      } catch (WebException we) {
        // Only the status is kept, never the error body. Returning the body here
        // would make a failure look like a successful response with no data, and
        // callers would report a missing channel where the real problem is a
        // rejected token.
        var r = we.Response as HttpWebResponse;
        if (r != null) httpStatus = (int)r.StatusCode;
        return null;
      } catch { return null; }
    }

    static string HelixPost(string url, string body, out int httpStatus) {
      httpStatus = 0;
      try {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = "POST";
        req.Timeout = 10000;
        req.ContentType = "application/json";
        req.Headers["Client-Id"] = _clientId;
        req.Headers["Authorization"] = "Bearer " + _token;
        req.UserAgent = "NowPlayingOverlay";
        var bytes = Encoding.UTF8.GetBytes(body);
        req.ContentLength = bytes.Length;
        using (var rs = req.GetRequestStream()) rs.Write(bytes, 0, bytes.Length);
        using (var resp = (HttpWebResponse)req.GetResponse()) {
          httpStatus = (int)resp.StatusCode;
          using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return sr.ReadToEnd();
        }
      } catch (WebException we) {
        var r = we.Response as HttpWebResponse;
        if (r != null) httpStatus = (int)r.StatusCode;
        return null;
      } catch { return null; }
    }

    // ------------------------------------------------------------ auto-refresh
    // Twitch user tokens are short-lived by design (hours, not days), so a
    // channel left running unattended - the whole point of a stream overlay -
    // would otherwise go dark on a timer nobody controls. Refreshing needs a
    // Client Secret (from the same Developer Console registration as the
    // Client ID) plus a refresh token, which the CLI prints right alongside
    // the access token; without both, this is simply unavailable and the
    // existing "regenerate it by hand" behaviour is unchanged.
    static readonly object _refreshLock = new object();

    // Twitch invalidates the refresh token itself the instant it's used, so
    // the response's new one MUST be captured or the second refresh ever
    // attempted fails outright with the account otherwise perfectly healthy.
    static bool TryRefreshToken() {
      if (!CanRefresh) return false;
      string newAccess, newRefresh;
      if (!RefreshWithApp(_refreshToken, out newAccess, out newRefresh)) return false;
      _token = newAccess;
      if (newRefresh.Length > 0) _refreshToken = newRefresh;
      PersistRefreshedTokens();
      AppLog.Write("twitch: access token refreshed automatically");
      return true;
    }

    // The raw refresh call, shared with the chat bot's token in TwitchChat -
    // both credentials come from the same Twitch app and expire on the same
    // clock; only where the result lives differs.
    internal static bool RefreshWithApp(string refreshToken, out string newAccess, out string newRefresh) {
      newAccess = ""; newRefresh = "";
      if (_clientSecret.Length == 0 || refreshToken.Length == 0) return false;
      try {
        string body = "grant_type=refresh_token"
          + "&refresh_token=" + Uri.EscapeDataString(refreshToken)
          + "&client_id=" + Uri.EscapeDataString(_clientId)
          + "&client_secret=" + Uri.EscapeDataString(_clientSecret);
        var req = (HttpWebRequest)WebRequest.Create("https://id.twitch.tv/oauth2/token");
        req.Method = "POST";
        req.Timeout = 10000;
        req.ContentType = "application/x-www-form-urlencoded";
        req.UserAgent = "NowPlayingOverlay";
        var bytes = Encoding.UTF8.GetBytes(body);
        req.ContentLength = bytes.Length;
        using (var rs = req.GetRequestStream()) rs.Write(bytes, 0, bytes.Length);
        string resp;
        using (var wr = (HttpWebResponse)req.GetResponse())
        using (var sr = new StreamReader(wr.GetResponseStream(), Encoding.UTF8))
          resp = sr.ReadToEnd();

        var root = _ser.DeserializeObject(resp);
        newAccess = SNav(root, "access_token");
        newRefresh = SNav(root, "refresh_token");
        if (newAccess.Length == 0) {
          AppLog.Write("twitch: refresh response carried no access_token");
          return false;
        }
        return true;
      } catch (WebException we) {
        // A refresh token is one-time use; this fires if it was already spent
        // (two processes sharing one config, or Twitch revoked it) or if the
        // client secret is wrong. Nothing left to try but a manual regenerate.
        string body = "";
        try {
          var r = we.Response as HttpWebResponse;
          if (r != null) using (var sr = new StreamReader(r.GetResponseStream())) body = sr.ReadToEnd();
        } catch { }
        AppLog.Write("twitch: token refresh failed: " + we.Message + (body.Length > 0 ? " - " + body : ""));
        return false;
      } catch (Exception ex) {
        AppLog.Write("twitch: token refresh exception: " + ex.Message);
        return false;
      }
    }

    // Called after any Helix call comes back 401. Takes the token that just
    // failed so two threads hitting 401 around the same moment don't both
    // spend a refresh: whichever gets the lock second sees _token has already
    // moved on and simply signals its caller to retry with what's current.
    static bool RecoverFromUnauthorized(string failedToken) {
      if (!CanRefresh) return false;
      lock (_refreshLock) {
        if (_token != failedToken) return true;      // someone else already refreshed it
        return TryRefreshToken();
      }
    }

    static string HelixGetAuth(string url, out int httpStatus) {
      string body = HelixGet(url, out httpStatus);
      if (body == null && httpStatus == 401) {
        if (RecoverFromUnauthorized(_token)) body = HelixGet(url, out httpStatus);
      }
      return body;
    }

    static string HelixPostAuth(string url, string reqBody, out int httpStatus) {
      string body = HelixPost(url, reqBody, out httpStatus);
      if (body == null && httpStatus == 401) {
        if (RecoverFromUnauthorized(_token)) body = HelixPost(url, reqBody, out httpStatus);
      }
      return body;
    }

    // The config file stays the single source of truth for both tokens, so a
    // hand-pasted replacement (the user ran the CLI again) and an automatic
    // refresh can never disagree about which token is current. Rewritten by
    // hand rather than through JavaScriptSerializer.Serialize, which would
    // collapse the whole file to one unreadable line - this still has to be
    // comfortable to open and hand-edit after an automatic rewrite.
    static readonly string[] PreferredKeyOrder = {
      "channel",
      "_comment_chat", "botUsername", "oauthToken", "botRefreshToken", "command", "cooldownSeconds", "npUrl",
      "responseTemplate", "pausedTemplate", "notPlayingMessage",
      "followThanks", "followThanksTemplate",
      "gameStats", "gameStatsAnnounce", "gameStatsTimerMinutes",
      "_comment_api", "clientId", "clientSecret", "apiToken", "refreshToken",
      "_comment_goals", "followerGoal", "subGoal"
    };

    // Retries where every other write in the app fails fast, because what is
    // being persisted here is unlike everything else: Twitch refresh tokens
    // are single-use, and the moment one is spent its replacement exists only
    // in memory until this write lands. A transiently locked config file - an
    // antivirus scan, a cloud-sync client, the file open in an editor - would
    // otherwise cost the token silently, and the bill arrives weeks later as
    // "bad token" on the next restart with nothing left to do but reconnect.
    static void PersistTokens(string path, Action<Dictionary<string, object>> apply, string what) {
      for (int attempt = 0; ; attempt++) {
        try {
          var cfg = ReadConfig(path);
          if (cfg == null) return;
          apply(cfg);
          Files.WriteAtomic(path, WriteConfig(cfg));
          if (attempt > 0) AppLog.Write("twitch: saved " + what + " after " + (attempt + 1) + " tries");
          return;
        } catch (Exception ex) {
          if (attempt >= 4) {
            AppLog.Write("twitch: could not save " + what + " after retries: " + ex.Message
              + " - if the app restarts before the next successful refresh, reconnect via Setup.");
            return;
          }
          Thread.Sleep(300 * (attempt + 1));
        }
      }
    }

    static void PersistRefreshedTokens() {
      if (_configPath == null) return;
      string tok = _token, rt = _refreshToken;
      PersistTokens(_configPath, delegate(Dictionary<string, object> cfg) {
        cfg["apiToken"] = tok;
        cfg["refreshToken"] = rt;
      }, "refreshed token");
    }

    // TwitchChat's version of the same persistence. It lives here because this
    // class owns the config file's path and format; the bare token goes in and
    // LoadConfig's oauth:-normalisation puts the prefix back on the way out.
    internal static void SaveBotTokens(string access, string refresh) {
      string p = _configPath;
      if (p == null) p = FindConfigPath();
      if (p == null) return;
      PersistTokens(p, delegate(Dictionary<string, object> cfg) {
        cfg["oauthToken"] = access;
        if (refresh.Length > 0) cfg["botRefreshToken"] = refresh;
      }, "refreshed bot token");
    }

    // The game-stats switches, written by the bot dashboard. Same ownership
    // rule as SaveBotFollowThanks below.
    internal static void SaveGameStats(bool on, bool announce, int timerMin) {
      string p = _configPath;
      if (p == null) p = FindConfigPath();
      if (p == null) return;
      try {
        var cfg = ReadConfig(p);
        if (cfg == null) return;
        cfg["gameStats"] = on;
        cfg["gameStatsAnnounce"] = announce;
        cfg["gameStatsTimerMinutes"] = timerMin;
        Files.WriteAtomic(p, WriteConfig(cfg));
      } catch (Exception ex) {
        AppLog.Write("chat: could not save game-stats settings: " + ex.Message);
      }
    }

    // The follow thank-you's two settings, written by the bot dashboard. Lives
    // here for the same reason SaveBotTokens does: this class owns the config
    // file's path and hand-readable format.
    internal static void SaveBotFollowThanks(bool on, string template) {
      string p = _configPath;
      if (p == null) p = FindConfigPath();
      if (p == null) return;
      try {
        var cfg = ReadConfig(p);
        if (cfg == null) return;
        cfg["followThanks"] = on;
        cfg["followThanksTemplate"] = template ?? "";
        Files.WriteAtomic(p, WriteConfig(cfg));
      } catch (Exception ex) {
        AppLog.Write("chat: could not save follow-thanks settings: " + ex.Message);
      }
    }

    static string WriteConfig(Dictionary<string, object> cfg) {
      var keys = new List<string>(PreferredKeyOrder);
      foreach (var k in cfg.Keys) if (!keys.Contains(k)) keys.Add(k);

      var sb = new StringBuilder("{\r\n");
      bool first = true;
      var done = new HashSet<string>();
      foreach (var k in keys) {
        object v;
        if (!done.Add(k) || !cfg.TryGetValue(k, out v)) continue;
        if (!first) sb.Append(",\r\n");
        first = false;
        sb.Append("  ").Append(Q(k)).Append(": ").Append(JsonValue(v));
      }
      return sb.Append("\r\n}\r\n").ToString();
    }

    static string JsonValue(object v) {
      if (v == null) return "null";
      if (v is bool) return ((bool)v) ? "true" : "false";
      if (v is int || v is long || v is double || v is float)
        return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
      return Q(Convert.ToString(v));
    }

    // A 401 means the token is dead and no amount of retrying fixes it, so say
    // so plainly rather than letting the pages sit on a stale count forever.
    static bool NoteAuthFailure(int http, string what) {
      if (http == 401) {
        _status = "bad-token";
        // Reaching here means an automatic refresh was already tried and either
        // was not possible or itself failed - so the advice has to differ.
        _detail = "Twitch rejected the token (401) on " + what + ". " + (CanRefresh
          ? "The automatic refresh failed too - check clientSecret and refreshToken in twitch-config.json."
          : "Regenerate it, or add clientSecret and refreshToken to twitch-config.json so this renews itself automatically.");
        return true;
      }
      if (http == 403) {
        _status = "missing-scope";
        _detail = "Twitch refused " + what + " (403). The token is missing a required scope.";
        return true;
      }
      return false;
    }

    static bool ResolveBroadcaster() {
      int http;
      string body = HelixGetAuth("https://api.twitch.tv/helix/users?login=" + Uri.EscapeDataString(_channel), out http);
      if (body == null) {
        if (NoteAuthFailure(http, "/users")) return false;
        if (http == 400) {
          // Twitch validates that the Client-Id issued the token, and a mismatch
          // fails here rather than at the token itself - which is why this says
          // so explicitly instead of blaming the channel name.
          _status = "bad-token";
          _detail = "Twitch rejected the request (400). The clientId must be the client the apiToken was issued to.";
          return false;
        }
        _status = "error"; _detail = "could not reach Twitch (/users)"; return false;
      }
      var data = Nav(_ser.DeserializeObject(body), "data") as object[];
      if (data == null || data.Length == 0) {
        _status = "error"; _detail = "channel \"" + _channel + "\" not found on Twitch"; return false;
      }
      _broadcasterId = SNav(data[0], "id");
      return _broadcasterId.Length > 0;
    }

    static void PollTotals() {
      if (_broadcasterId.Length == 0) return;
      int http;

      string f = HelixGetAuth("https://api.twitch.tv/helix/channels/followers?first=1&broadcaster_id=" + _broadcasterId, out http);
      if (f != null) {
        var root = _ser.DeserializeObject(f);
        _followerTotal = INav(root, "total");
        // Followers come back newest first and carry followed_at, so the latest
        // follower can be seeded here. Subscriptions have no such field, which
        // is exactly why the event stream exists.
        var data = Nav(root, "data") as object[];
        if (data != null && data.Length > 0) {
          lock (_stateLock) {
            if (_lastFollower.Length == 0) {
              _lastFollower = SNav(data[0], "user_name");
              _lastFollowerAt = SNav(data[0], "followed_at");
            }
          }
        }
        if (_status == "connecting" || _status == "error") _status = "live";
      } else if (NoteAuthFailure(http, "/channels/followers")) {
        return;
      }

      string s = HelixGetAuth("https://api.twitch.tv/helix/subscriptions?first=1&broadcaster_id=" + _broadcasterId, out http);
      if (s != null) {
        var root = _ser.DeserializeObject(s);
        _subTotal = INav(root, "total");
        _subPoints = INav(root, "points");
      } else if (http == 400 || http == 401 || http == 403) {
        // Either the channel cannot have subscriptions, or the token was never
        // given the scope. Both mean the same thing to the pages - there is no
        // sub data - and neither is a fault worth interrupting followers over.
        // 401 is included because a missing scope is what Twitch returns it for.
        _subTotal = -2;
      }
    }

    // ------------------------------------------------------------- EventSub
    static readonly HashSet<string> _seenIds = new HashSet<string>();
    static readonly Queue<string> _seenOrder = new Queue<string>();

    static bool AlreadySeen(string id) {
      if (string.IsNullOrEmpty(id)) return false;
      lock (_seenIds) {
        if (_seenIds.Contains(id)) return true;
        _seenIds.Add(id);
        _seenOrder.Enqueue(id);
        while (_seenOrder.Count > 300) _seenIds.Remove(_seenOrder.Dequeue());
        return false;
      }
    }

    static void WsLoop() {
      int backoff = 5;
      string url = "wss://eventsub.wss.twitch.tv/ws";
      bool everConnected = false;

      while (true) {
        ClientWebSocket ws = null;
        try {
          ws = new ClientWebSocket();
          ws.ConnectAsync(new Uri(url), CancellationToken.None).Wait();
          url = "wss://eventsub.wss.twitch.tv/ws";   // reconnect_url is single-use
          if (everConnected) {
            _reconnects++;
            AppLog.Write("twitch: socket reconnected (reconnect #" + _reconnects + ")");
          }
          everConnected = true;

          var buf = new byte[16384];
          var acc = new StringBuilder();
          int keepalive = 10;
          DateTime lastMsg = DateTime.UtcNow;
          bool subscribed = false;

          while (ws.State == WebSocketState.Open) {
            // The read has to time out, otherwise a silently dead socket parks
            // this thread forever and the overlay just quietly stops updating.
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(keepalive * 2 + 5));
            WebSocketReceiveResult r;
            try {
              r = ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token).Result;
            } catch {
              break;   // timeout or socket fault: fall through to reconnect
            }
            if (r.MessageType == WebSocketMessageType.Close) break;
            acc.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            if (!r.EndOfMessage) continue;

            string text = acc.ToString();
            acc.Length = 0;
            lastMsg = DateTime.UtcNow;

            object msg;
            try { msg = _ser.DeserializeObject(text); } catch { continue; }

            string type = SNav(msg, "metadata", "message_type");
            string mid = SNav(msg, "metadata", "message_id");

            if (type == "session_welcome") {
              string sid = SNav(msg, "payload", "session", "id");
              int ka = INav(msg, "payload", "session", "keepalive_timeout_seconds");
              if (ka > 0) keepalive = ka;
              _sessionId = sid;
              _connectedAt = DateTime.Now.ToString("HH:mm:ss");
              // Twitch closes the socket if no subscription is created within
              // 10s of the welcome, so this cannot be deferred.
              subscribed = CreateSubscriptions(sid);
              if (subscribed) { _status = "live"; _detail = ""; backoff = 5; }
              AppLog.Write("twitch: session_welcome, session=" + sid + " status=" + _status);
            } else if (type == "session_keepalive") {
              _lastKeepaliveAt = DateTime.Now.ToString("HH:mm:ss");
            } else if (type == "session_reconnect") {
              string next = SNav(msg, "payload", "session", "reconnect_url");
              if (next.Length > 0) url = next;
              AppLog.Write("twitch: session_reconnect requested by Twitch");
              break;   // reconnect immediately rather than overlapping sockets
            } else if (type == "revocation") {
              _status = "missing-scope";
              _detail = "Twitch revoked a subscription: " + SNav(msg, "payload", "status");
              AppLog.Write("twitch: revocation - " + _detail);
            } else if (type == "notification") {
              if (!AlreadySeen(mid)) HandleNotification(msg);
            }
          }
        } catch (Exception ex) {
          AppLog.Write("twitch: socket loop exception: " + ex.Message);
        } finally {
          _sessionId = "";
          try { if (ws != null) ws.Dispose(); } catch { }
        }

        if (_status == "bad-token" || _status == "off") return;   // retrying cannot help
        if (_status == "live") { _status = "connecting"; }
        Thread.Sleep(backoff * 1000);
        backoff = Math.Min(backoff * 2, 60);
      }
    }

    // Which event types Twitch actually accepted, so the pages can say what is
    // being listened for rather than implying everything works.
    static readonly List<string> _active = new List<string>();

    static bool CreateSubscriptions(string sessionId) {
      if (sessionId.Length == 0) return false;
      lock (_active) _active.Clear();

      // channel.follow is v2 and needs a moderator_user_id; the broadcaster is
      // always a moderator of their own channel, so it doubles as both.
      bool ok = Sub("channel.follow", "2",
        "{\"broadcaster_user_id\":\"" + _broadcasterId + "\",\"moderator_user_id\":\"" + _broadcasterId + "\"}",
        sessionId, true);

      // These three are optional on purpose. A token scoped only for followers,
      // or a channel that cannot have subscribers, fails every one of them - and
      // that must not be mistaken for a dead token, because treating it as one
      // would abandon the socket and take working follow alerts down with it.
      bool subs = Sub("channel.subscribe", "1",
        "{\"broadcaster_user_id\":\"" + _broadcasterId + "\"}", sessionId, false);
      subs |= Sub("channel.subscription.message", "1",
        "{\"broadcaster_user_id\":\"" + _broadcasterId + "\"}", sessionId, false);
      subs |= Sub("channel.subscription.gift", "1",
        "{\"broadcaster_user_id\":\"" + _broadcasterId + "\"}", sessionId, false);
      if (!subs) _subTotal = -2;      // pages hide the subscriber box entirely

      return ok;
    }

    static bool Sub(string type, string version, string condition, string sessionId, bool critical) {
      string body = "{\"type\":\"" + type + "\",\"version\":\"" + version + "\","
                  + "\"condition\":" + condition + ","
                  + "\"transport\":{\"method\":\"websocket\",\"session_id\":\"" + sessionId + "\"}}";
      int http;
      string resp = HelixPostAuth("https://api.twitch.tv/helix/eventsub/subscriptions", body, out http);
      if (resp != null && (http == 200 || http == 202)) {
        lock (_active) _active.Add(type);
        return true;
      }
      if (critical) NoteAuthFailure(http, type);
      return false;
    }

    // A record that something genuinely arrived from Twitch. Without it, "no
    // alert appeared" is ambiguous between nothing being sent, the socket being
    // down, and the page failing to draw - three different things to go and fix.
    static volatile int _eventCount;
    static volatile string _lastEventKind = "";
    static volatile string _lastEventAt = "";

    // Socket health, separate from "received an event" - a channel with no
    // activity yet looks identical to a dead socket unless the connection's
    // own state is visible too. sessionId blanks out between sockets so a
    // reconnect in progress cannot be mistaken for the previous one still
    // holding.
    static volatile string _sessionId = "";
    static volatile string _connectedAt = "";
    static volatile string _lastKeepaliveAt = "";
    static volatile int _reconnects;

    static void HandleNotification(object msg) { HandleNotification(msg, false); }

    // test=true means this came from /twitch/test rather than the socket. The
    // parsing is deliberately identical - that is the whole point of routing
    // tests through here - but nothing that outlives the alert is touched: no
    // counters, no "most recent" state, nothing written to disk. A rehearsal
    // must not leave a fake subscriber on the client's overlay.
    static void HandleNotification(object msg, bool test) {
      string type = SNav(msg, "payload", "subscription", "type");
      object ev = Nav(msg, "payload", "event");
      if (ev == null) return;

      if (!test) {
        _eventCount++;
        _lastEventKind = type;
        _lastEventAt = DateTime.Now.ToString("HH:mm:ss");
      }
      string tail = test ? ",\"test\":true}" : "}";

      string user = SNav(ev, "user_name");
      if (user.Length == 0) user = SNav(ev, "user_login");
      string at = DateTime.UtcNow.ToString("o");

      if (type == "channel.follow") {
        if (!test) {
          lock (_stateLock) { _lastFollower = user; _lastFollowerAt = SNav(ev, "followed_at"); }
          if (_followerTotal >= 0) _followerTotal++;
          // The bot's thank-you in chat. Real follows only - a rehearsal from
          // /twitch/test must never speak in the client's channel. Dedup and
          // burst-coalescing live on the chat side.
          TwitchChat.OnFollow(user);
        }
        Push("{\"kind\":\"follow\",\"user\":" + Q(user) + ",\"at\":" + Q(at) + tail);

      } else if (type == "channel.subscribe") {
        // Every recipient of a gift bomb also produces one of these. Letting
        // them through would fire twenty alerts for one gifting event, so the
        // gift notification is treated as the single source of truth for gifts.
        if (BNav(ev, "is_gift")) return;
        string tier = SNav(ev, "tier");
        if (!test) RecordSub(user, tier, at);
        Push("{\"kind\":\"sub\",\"user\":" + Q(user) + ",\"tier\":" + Q(tier)
           + ",\"months\":1,\"at\":" + Q(at) + tail);

      } else if (type == "channel.subscription.message") {
        string tier = SNav(ev, "tier");
        int months = INav(ev, "cumulative_months");
        if (!test) RecordSub(user, tier, at);
        Push("{\"kind\":\"resub\",\"user\":" + Q(user) + ",\"tier\":" + Q(tier)
           + ",\"months\":" + months
           + ",\"message\":" + Q(SNav(ev, "message", "text")) + ",\"at\":" + Q(at) + tail);

      } else if (type == "channel.subscription.gift") {
        bool anon = BNav(ev, "is_anonymous");
        string gifter = anon ? "Anonymous" : user;
        int count = INav(ev, "total");
        if (count <= 0) count = 1;
        string tier = SNav(ev, "tier");
        if (!test && _subTotal >= 0) _subTotal += count;
        Push("{\"kind\":\"gift\",\"user\":" + Q(gifter) + ",\"tier\":" + Q(tier)
           + ",\"count\":" + count + ",\"anon\":" + (anon ? "true" : "false")
           + ",\"at\":" + Q(at) + tail);
      }
    }

    static void RecordSub(string user, string tier, string at) {
      lock (_stateLock) { _lastSub = user; _lastSubTier = tier; _lastSubAt = at; }
      if (_subTotal >= 0) _subTotal++;
      SaveState();
    }

    // ------------------------------------------------------------- test fire
    //
    // These build genuine EventSub notification envelopes, matching the payloads
    // Twitch documents, and push them through the same HandleNotification the
    // socket uses. An earlier version handed the pages a ready-made alert, which
    // proved the visuals worked and nothing whatsoever about whether the payloads
    // were being read correctly - the half that cannot be rehearsed any other way
    // when the channel has no subscribers to test with.
    //
    // So a test exercises the JSON parsing, the field names, the tier and month
    // handling, the gift de-duplication and the ring buffer. What it cannot cover
    // is the websocket and Twitch's own delivery.
    static int _testSeq;

    static string Envelope(string type, string version, string eventJson) {
      int n = Interlocked.Increment(ref _testSeq);
      return "{\"metadata\":{\"message_id\":\"test-" + DateTime.UtcNow.Ticks + "-" + n + "\","
           + "\"message_type\":\"notification\","
           + "\"message_timestamp\":" + Q(DateTime.UtcNow.ToString("o")) + "},"
           + "\"payload\":{\"subscription\":{\"type\":\"" + type + "\",\"version\":\"" + version
           + "\",\"status\":\"enabled\"},\"event\":" + eventJson + "}}";
    }

    // Real notifications go through exactly this, dedup included.
    static void Inject(string envelope) {
      object msg;
      try { msg = _ser.DeserializeObject(envelope); } catch { return; }
      if (AlreadySeen(SNav(msg, "metadata", "message_id"))) return;
      HandleNotification(msg, true);
    }

    static string Chan() {
      string id = _broadcasterId.Length > 0 ? _broadcasterId : "000000";
      return "\"broadcaster_user_id\":\"" + id + "\","
           + "\"broadcaster_user_login\":" + Q(_channel.Length > 0 ? _channel : "channel") + ","
           + "\"broadcaster_user_name\":" + Q(_channel.Length > 0 ? _channel : "channel") + ",";
    }

    static string Who(string user) {
      return "\"user_id\":\"999999\","
           + "\"user_login\":" + Q(user.ToLowerInvariant()) + ","
           + "\"user_name\":" + Q(user) + ",";
    }

    public static string TestFire(string kind, string user) {
      if (string.IsNullOrEmpty(user)) user = "TestUser";
      long before = CurrentSeq;
      int injected = 0;

      switch (kind) {
        case "sub":
          Inject(Envelope("channel.subscribe", "1",
            "{" + Who(user) + Chan() + "\"tier\":\"1000\",\"is_gift\":false}"));
          injected = 1;
          break;

        case "resub":
          Inject(Envelope("channel.subscription.message", "1",
            "{" + Who(user) + Chan()
            + "\"tier\":\"2000\","
            + "\"message\":{\"text\":" + Q("Eleven months and still here every week.") + ",\"emotes\":[]},"
            + "\"cumulative_months\":11,\"streak_months\":3,\"duration_months\":6}"));
          injected = 1;
          break;

        case "gift":
          // A gift bomb on Twitch is one gift notification plus one
          // channel.subscribe per recipient. Both halves are replayed here, so
          // clicking this proves the recipients are actually being swallowed
          // rather than merely being intended to be - the difference between one
          // alert and six on a client's stream.
          Inject(Envelope("channel.subscription.gift", "1",
            "{" + Who(user) + Chan()
            + "\"total\":5,\"tier\":\"1000\",\"cumulative_total\":50,\"is_anonymous\":false}"));
          injected = 1;
          for (int i = 1; i <= 5; i++) {
            Inject(Envelope("channel.subscribe", "1",
              "{" + Who("GiftRecipient" + i) + Chan() + "\"tier\":\"1000\",\"is_gift\":true}"));
            injected++;
          }
          break;

        default:
          kind = "follow";
          Inject(Envelope("channel.follow", "2",
            "{" + Who(user) + "\"moderator_user_id\":\"000000\"," + Chan()
            + "\"followed_at\":" + Q(DateTime.UtcNow.ToString("o")) + "}"));
          injected = 1;
          break;
      }

      // Reporting both numbers makes the gift case self-checking: six events in,
      // one alert out is the coalescing working.
      return "{\"fired\":" + Q(kind) + ",\"user\":" + Q(user)
           + ",\"injected\":" + injected
           + ",\"alerts\":" + (CurrentSeq - before) + "}";
    }

    // ----------------------------------------------------------------- goals
    // An unset goal tracks the next round number above the current count, so the
    // bar always has somewhere to go without the user maintaining a config file.
    static int AutoGoal(int current) {
      if (current < 0) return 0;
      int step = current < 100 ? 10
               : current < 500 ? 50
               : current < 2000 ? 100
               : current < 10000 ? 500 : 1000;
      return ((current / step) + 1) * step;
    }

    public static string StatusJson() {
      string lf, lfa, ls, lsa, lst;
      lock (_stateLock) {
        lf = _lastFollower; lfa = _lastFollowerAt;
        ls = _lastSub; lsa = _lastSubAt; lst = _lastSubTier;
      }
      int ft = _followerTotal, st = _subTotal;
      int fg = _followerGoal > 0 ? _followerGoal : AutoGoal(ft);
      int sg = _subGoal > 0 ? _subGoal : AutoGoal(st);

      var sb = new StringBuilder();
      sb.Append('{');
      sb.Append("\"status\":").Append(Q(_status)).Append(',');
      sb.Append("\"detail\":").Append(Q(_detail)).Append(',');
      sb.Append("\"autoRefresh\":").Append(CanRefresh ? "true" : "false").Append(',');
      sb.Append("\"channel\":").Append(Q(_channel)).Append(',');
      sb.Append("\"seq\":").Append(CurrentSeq).Append(',');
      sb.Append("\"events\":{\"listening\":[");
      lock (_active) {
        for (int i = 0; i < _active.Count; i++) {
          if (i > 0) sb.Append(',');
          sb.Append(Q(_active[i]));
        }
      }
      sb.Append("],\"received\":").Append(_eventCount)
        .Append(",\"lastKind\":").Append(Q(_lastEventKind))
        .Append(",\"lastAt\":").Append(Q(_lastEventAt)).Append("},");
      // sessionId blank means no socket is currently up - either between
      // reconnect attempts, or the feed never started. Distinguishing that
      // from "connected but Twitch has sent nothing" is the point of exposing
      // it: a socket that connects and instantly drops looks like "live" for
      // an instant and then like nothing happened, with no other visible sign.
      sb.Append("\"socket\":{\"connected\":").Append(_sessionId.Length > 0 ? "true" : "false")
        .Append(",\"connectedAt\":").Append(Q(_connectedAt))
        .Append(",\"lastKeepaliveAt\":").Append(Q(_lastKeepaliveAt))
        .Append(",\"reconnects\":").Append(_reconnects).Append("},");
      sb.Append("\"followers\":{\"total\":").Append(ft)
        .Append(",\"goal\":").Append(fg)
        .Append(",\"latest\":").Append(Q(lf))
        .Append(",\"at\":").Append(Q(lfa)).Append("},");
      // total -2 is the deliberate "this channel cannot have subs" marker; the
      // pages hide the box on it rather than rendering a zero.
      sb.Append("\"subs\":{\"total\":").Append(st)
        .Append(",\"goal\":").Append(sg)
        .Append(",\"points\":").Append(_subPoints)
        .Append(",\"latest\":").Append(Q(ls))
        .Append(",\"tier\":").Append(Q(lst))
        .Append(",\"at\":").Append(Q(lsa)).Append('}');
      sb.Append('}');
      return sb.ToString();
    }

    // ----------------------------------------------------------------- start
    public static void Start() {
      LoadState();
      LoadConfig();     // always runs, even disabled: the chat bot borrows
                        // clientId/clientSecret from here for token refresh
      if (!Program.TwitchFeatureOn) {
        _status = "disabled";
        _detail = "alerts and goal boxes are turned off on the Features page";
        AppLog.Write("twitch: not starting - switched off on the Features page");
        return;
      }
      if (!Configured) return;

      // .NET Framework still negotiates TLS 1.0 by default on some machines and
      // Twitch refuses it, which surfaces as a bare "unable to connect" with no
      // hint that the protocol is the problem. Pin 1.2 up front - and OR in 1.3
      // where the runtime knows it (4.8+), because the day Twitch turns 1.2 off
      // an exact 1.2 pin would break every install at once.
      try {
        var protos = SecurityProtocolType.Tls12;
        if (Enum.IsDefined(typeof(SecurityProtocolType), 12288))
          protos |= (SecurityProtocolType)12288;   // Tls13; named only from 4.8
        ServicePointManager.SecurityProtocol = protos;
      } catch { }

      _status = "connecting";
      AppLog.Write("twitch: starting for channel \"" + _channel + "\"");

      var t = new Thread(() => {
        // ResolveBroadcaster and this first PollTotals only guard against the
        // Twitch responses they know how to recognise (WebException, expected
        // JSON shape). Anything else - a malformed body, an unexpected null -
        // used to propagate out of this thread unhandled, which kills the
        // entire process, not just the Twitch feature. One odd API response
        // should never be the reason the song overlay needs restarting too.
        try {
          if (!ResolveBroadcaster()) {
            AppLog.Write("twitch: ResolveBroadcaster failed, status=" + _status + " detail=" + _detail);
            return;
          }
          PollTotals();
        } catch (Exception ex) {
          _status = "error"; _detail = "unexpected failure starting up: " + ex.Message;
          AppLog.Write("twitch: startup exception: " + ex);
          return;
        }

        var ws = new Thread(WsLoop);
        ws.IsBackground = true;
        ws.Start();

        while (true) {
          Thread.Sleep(60000);
          try { PollTotals(); }
          catch (Exception ex) { AppLog.Write("twitch: PollTotals exception: " + ex); }
        }
      });
      t.IsBackground = true;
      t.Start();
    }
  }
}
