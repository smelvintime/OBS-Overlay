// Twitch chat bot, in-process.
//
// This is a port of twitch-bot.ps1 into the exe, so that one launch gives you
// the overlay, the alerts and the bot instead of a second console window that
// has to be started and kept alive separately. The .ps1 stays for anyone
// running the PowerShell server, and the two are deliberately kept behaviourally
// identical where it matters.
//
// Chat is IRC, not EventSub: a TLS socket and a line protocol, nothing more.
// It is a completely separate connection and a separate credential from the
// EventSub socket in TwitchEvents - that one reads follows as the broadcaster,
// this one speaks as the bot account.
//
// The bot is off until switched on. It posts publicly under someone's name, so
// starting on its own the first time the exe runs would be a surprise, and a
// surprise that everyone in the channel can see.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace NowPlaying {

  static class TwitchChat {

    // ---------------------------------------------------------------- config
    static string _channel = "";
    static string _botUser = "";
    static string _token = "";        // carries the oauth: prefix, unlike the Helix one

    // "off" until configured, then: disabled, connecting, live, bad-token, error.
    // disabled is distinct from off on purpose - "you switched it off" and "it
    // was never set up" need different things said about them.
    static volatile string _status = "off";
    static volatile string _detail = "";
    static volatile string _connectedAt = "";
    static volatile int _reconnects;
    static volatile int _sent;

    public static string Status { get { return _status; } }
    public static string Detail { get { return _detail; } }
    public static bool Configured {
      get { return _channel.Length > 0 && _botUser.Length > 0 && _token.Length > 0; }
    }

    static volatile bool _enabled;
    static Thread _thread;
    static volatile bool _stopFlag;

    // ------------------------------------------------------------- recent log
    // What it actually said, so the dashboard can show that it is working
    // without anyone having to watch the channel.
    class Reply { public string At; public string Who; public string Cmd; public string Text; }
    static readonly List<Reply> _recent = new List<Reply>();

    static void Note(string who, string cmd, string text) {
      lock (_recent) {
        _recent.Add(new Reply {
          At = DateTime.Now.ToString("HH:mm:ss"), Who = who, Cmd = cmd, Text = text
        });
        while (_recent.Count > 25) _recent.RemoveAt(0);
      }
    }

    // ------------------------------------------------------------ config load
    public static void LoadConfig() {
      string p = TwitchEvents.FindConfigPath();
      if (p == null) { _status = "off"; _detail = "no twitch-config.json found"; return; }
      try {
        var cfg = TwitchEvents.ReadConfig(p);
        if (cfg == null) { _status = "error"; _detail = "twitch-config.json is not valid JSON"; return; }

        _channel = Cfg(cfg, "channel").Trim().TrimStart('#').ToLowerInvariant();
        _botUser = Cfg(cfg, "botUsername").Trim().ToLowerInvariant();
        _token = Cfg(cfg, "oauthToken").Trim();

        // Chat wants the oauth: prefix; the Helix token must not have it. Accept
        // either spelling here and normalise, because the two tokens sit next to
        // each other in the same file and get pasted into the wrong slot.
        if (_token.Length > 0 && !_token.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase))
          _token = "oauth:" + _token;

        if (_channel.Length == 0 || _channel.StartsWith("your_")) {
          _status = "off"; _detail = "channel is not set in twitch-config.json"; return;
        }
        if (_botUser.Length == 0 || _botUser.StartsWith("your_")) {
          _status = "off"; _detail = "botUsername is not set - the bot needs an account to speak as"; return;
        }
        if (_token.Length <= 6 || _token.IndexOf("PASTE", StringComparison.OrdinalIgnoreCase) >= 0) {
          _status = "off"; _detail = "oauthToken is not set - needs chat:read and chat:edit for the bot account"; return;
        }
        _status = "disabled";
        _detail = "";
      } catch (Exception e) {
        _status = "error"; _detail = "could not read twitch-config.json: " + e.Message;
      }
    }

    static string Cfg(Dictionary<string, object> d, string key) {
      object v;
      if (d != null && d.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
      return "";
    }

    // ----------------------------------------------------------------- switch
    public static bool Enabled { get { return _enabled; } }

    public static void SetEnabled(bool on) {
      if (on == _enabled) return;
      _enabled = on;
      AppLog.Write("chat: " + (on ? "enabled" : "disabled") + " by user");
      if (on) StartThread();
      else StopThread();
    }

    public static void Start() {
      LoadConfig();
      BotCommands.Load();
      CheckScopes();
      if (_enabled && Configured) StartThread();
    }

    // Called by the settings loader before Start, so a bot switched on last time
    // comes back on by itself rather than needing a click after every restart.
    public static void RestoreEnabled(bool on) { _enabled = on; }

    static void StartThread() {
      if (!Configured) { AppLog.Write("chat: cannot start, not configured - " + _detail); return; }
      if (_thread != null && _thread.IsAlive) return;
      _stopFlag = false;
      _status = "connecting";
      // Wrapped, because an unhandled exception on any thread takes the whole
      // process down - the exact fault that used to make one bad Twitch response
      // kill the song overlay too.
      _thread = new Thread(() => {
        try { IrcLoop(); }
        catch (Exception ex) {
          _status = "error"; _detail = "chat thread stopped: " + ex.Message;
          AppLog.Write("chat: thread exception: " + ex);
        }
      });
      _thread.IsBackground = true;
      _thread.Start();
    }

    static void StopThread() {
      _stopFlag = true;
      _status = "disabled";
      _detail = "";
      _connectedAt = "";
    }

    // ------------------------------------------------------------- connection
    static void IrcLoop() {
      int backoff = 5;

      while (!_stopFlag) {
        TcpClient tcp = null;
        SslStream ssl = null;
        StreamReader rd = null;
        StreamWriter wr = null;

        try {
          tcp = new TcpClient();
          tcp.Connect("irc.chat.twitch.tv", 6697);
          ssl = new SslStream(tcp.GetStream(), false);
          ssl.AuthenticateAsClient("irc.chat.twitch.tv");
          ssl.ReadTimeout = 6000;

          var enc = new UTF8Encoding(false);
          rd = new StreamReader(ssl, enc);
          wr = new StreamWriter(ssl, enc) { AutoFlush = true };

          // tags carries the mod/broadcaster badges, which is the only way to
          // tell who is allowed to run a mod-only command. commands carries
          // RECONNECT and the NOTICE that reports a rejected login.
          wr.WriteLine("CAP REQ :twitch.tv/tags twitch.tv/commands");
          wr.WriteLine("PASS " + _token);
          wr.WriteLine("NICK " + _botUser);
          wr.WriteLine("JOIN #" + _channel);

          AppLog.Write("chat: connected, joining #" + _channel + " as " + _botUser);

          while (!_stopFlag && tcp.Connected) {
            string line;
            try { line = rd.ReadLine(); }
            catch (IOException) { continue; }        // read timeout, keep waiting
            if (line == null) break;                 // server closed the socket

            var ev = Parse(line);
            switch (ev.Type) {
              case "PING":
                wr.WriteLine("PONG :" + ev.Text);
                break;

              case "WELCOME":
                _status = "live";
                _detail = "";
                _connectedAt = DateTime.Now.ToString("HH:mm:ss");
                backoff = 5;
                AppLog.Write("chat: logged in as " + _botUser);
                break;

              case "AUTHFAIL":
                // No amount of retrying fixes a rejected token, and hammering
                // Twitch with a bad one is how an account gets rate limited.
                _status = "bad-token";
                _detail = "Twitch rejected the bot login (" + ev.Text + "). The bot account's "
                        + "oauthToken needs chat:read and chat:edit, and tokens expire.";
                AppLog.Write("chat: AUTH FAILED - " + _detail);
                _stopFlag = true;
                break;

              case "RECONNECT":
                AppLog.Write("chat: server asked us to reconnect");
                goto reconnect;

              case "PRIVMSG":
                if (ev.Nick == _botUser) break;      // never answer ourselves
                HandleMessage(ev, wr);
                break;
            }
          }
        } catch (Exception ex) {
          if (!_stopFlag) AppLog.Write("chat: connection problem: " + ex.Message);
        } finally {
          try { if (rd != null) rd.Dispose(); } catch { }
          try { if (wr != null) wr.Dispose(); } catch { }
          try { if (ssl != null) ssl.Dispose(); } catch { }
          try { if (tcp != null) tcp.Close(); } catch { }
        }

        reconnect:
        if (_stopFlag) break;
        if (_status == "live") { _status = "connecting"; _connectedAt = ""; }
        _reconnects++;
        Thread.Sleep(backoff * 1000);
        backoff = Math.Min(backoff * 2, 60);
      }

      if (_status != "bad-token" && _status != "error") _status = "disabled";
      _connectedAt = "";
      AppLog.Write("chat: loop ended (status=" + _status + ")");
    }

    // ------------------------------------------------------------ line parsing
    // Kept as a plain function over a string so the shapes below can be reasoned
    // about without a socket in the way.
    internal class IrcEvent {
      public string Type = "OTHER";
      public string Nick = "";
      public string Text = "";
      public string Message = "";
      public string UserId = "";
      public bool IsMod;
    }

    internal static IrcEvent Parse(string line) {
      var e = new IrcEvent();
      if (string.IsNullOrEmpty(line)) return e;

      string tags = "";
      if (line[0] == '@') {
        int sp = line.IndexOf(' ');
        if (sp < 0) return e;
        tags = line.Substring(1, sp - 1);
        line = line.Substring(sp + 1);
      }

      if (line.StartsWith("PING")) {
        e.Type = "PING";
        int c = line.IndexOf(':');
        e.Text = c >= 0 ? line.Substring(c + 1) : "tmi.twitch.tv";
        return e;
      }
      if (line.StartsWith("RECONNECT")) { e.Type = "RECONNECT"; return e; }

      if (line.IndexOf(" 001 ", StringComparison.Ordinal) >= 0) { e.Type = "WELCOME"; return e; }

      if (line.IndexOf("NOTICE", StringComparison.Ordinal) >= 0) {
        if (line.IndexOf("Login authentication failed", StringComparison.OrdinalIgnoreCase) >= 0
         || line.IndexOf("Improperly formatted auth", StringComparison.OrdinalIgnoreCase) >= 0
         || line.IndexOf("Invalid NICK", StringComparison.OrdinalIgnoreCase) >= 0) {
          e.Type = "AUTHFAIL";
          int c = line.IndexOf(':', 1);
          e.Text = c >= 0 ? line.Substring(c + 1) : "rejected";
          return e;
        }
      }

      int pm = line.IndexOf(" PRIVMSG ", StringComparison.Ordinal);
      if (pm > 0 && line.Length > 0 && line[0] == ':') {
        int bang = line.IndexOf('!');
        if (bang > 1 && bang < pm) {
          e.Nick = line.Substring(1, bang - 1).ToLowerInvariant();
          int msgColon = line.IndexOf(':', pm);
          e.Message = msgColon >= 0 ? line.Substring(msgColon + 1) : "";
          e.Type = "PRIVMSG";

          foreach (var pair in tags.Split(';')) {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string k = pair.Substring(0, eq), v = pair.Substring(eq + 1);
            if (k == "user-id") e.UserId = v;
            else if (k == "mod" && v == "1") e.IsMod = true;
            // The broadcaster carries no mod=1 flag but obviously outranks one.
            else if (k == "badges" && v.IndexOf("broadcaster/", StringComparison.Ordinal) >= 0)
              e.IsMod = true;
          }
        }
      }
      return e;
    }

    // -------------------------------------------------------------- responding
    // Twitch silently drops a message identical to the previous one within about
    // 30 seconds, so a second !song on the same track would look like the bot
    // had died. An invisible tag character alternates to keep repeats visible.
    static string _lastSent = "";
    static bool _altToggle;

    static string Dedup(string text) {
      if (text == _lastSent) {
        _altToggle = !_altToggle;
        if (_altToggle) return text + " " + char.ConvertFromUtf32(0xE0000);
      }
      return text;
    }

    static void HandleMessage(IrcEvent ev, StreamWriter wr) {
      string reply, matched;
      if (!BotCommands.TryRespond(ev, out reply, out matched)) return;
      if (string.IsNullOrEmpty(reply)) return;

      string send = Dedup(reply);
      wr.WriteLine("PRIVMSG #" + _channel + " :" + send);
      _lastSent = reply;
      _sent++;
      Note(ev.Nick, matched, reply);
    }

    // ------------------------------------------------------------- token scopes
    // Twitch will tell you exactly what a token can do, and the answer is worth
    // asking for: "the bot won't connect" and "this token was issued without
    // chat:edit" look identical from the outside, and only one of them is fixed
    // by regenerating anything. Checked once in the background at startup so a
    // slow or unreachable Twitch never delays the overlay itself.
    static volatile string _scopes = "";
    static volatile string _scopeUser = "";
    static volatile string _scopeState = "unchecked";   // unchecked|ok|bad|error

    public static void CheckScopes() {
      if (!Configured) return;
      var t = new Thread(() => {
        try {
          var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(
            "https://id.twitch.tv/oauth2/validate");
          req.Timeout = 10000;
          // The validate endpoint wants "OAuth", not the "Bearer" Helix uses.
          req.Headers["Authorization"] = "OAuth " + _token.Substring(6);
          using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
          using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) {
            string body = sr.ReadToEnd();
            _scopeUser = TwitchEvents.SNavPublic(TwitchEvents.NavPublic(body), "login");
            var arr = TwitchEvents.NavPublic(body, "scopes") as object[];
            var list = new List<string>();
            if (arr != null) foreach (var s in arr) list.Add(Convert.ToString(s));
            _scopes = string.Join(",", list.ToArray());
            _scopeState = "ok";
            AppLog.Write("chat: token validates as \"" + _scopeUser + "\" with scopes [" + _scopes + "]");
          }
        } catch (System.Net.WebException we) {
          var r = we.Response as System.Net.HttpWebResponse;
          _scopeState = (r != null && (int)r.StatusCode == 401) ? "bad" : "error";
          AppLog.Write("chat: token validation failed (" + _scopeState + ")");
        } catch { _scopeState = "error"; }
      });
      t.IsBackground = true;
      t.Start();
    }

    static bool HasScope(string want) {
      foreach (var s in _scopes.Split(',')) if (s.Trim() == want) return true;
      return false;
    }

    // What the dashboard needs to say something useful rather than "it didn't work".
    static string ScopeProblem() {
      if (_scopeState == "unchecked") return "";
      if (_scopeState == "bad") return "Twitch rejected this token (401). It has expired or was revoked - generate a new one for " + _botUser + ".";
      if (_scopeState == "error") return "";      // network trouble, not the token's fault
      var missing = new List<string>();
      if (!HasScope("chat:read")) missing.Add("chat:read");
      if (!HasScope("chat:edit")) missing.Add("chat:edit");
      if (missing.Count > 0)
        return "This token is missing " + string.Join(" and ", missing.ToArray())
             + ", so the bot cannot " + (missing.Contains("chat:edit") ? "post" : "listen")
             + ". Regenerate it for " + _botUser + " with both scopes.";
      if (_scopeUser.Length > 0 && _botUser.Length > 0 && _scopeUser != _botUser)
        return "This token belongs to \"" + _scopeUser + "\" but botUsername says \""
             + _botUser + "\". Twitch will reject the login - make them match.";
      return "";
    }

    // ------------------------------------------------------------- test fire
    // Builds a genuine tagged IRC line and puts it through the same Parse and
    // the same dispatch a real message takes, then reports what the bot would
    // have said without sending anything. Chat cannot be rehearsed against a
    // live channel without an audience watching, and a test that skipped the
    // parsing would prove only that the reply text exists - not that "!song"
    // typed by a real viewer is recognised as the song command.
    public static string TestLine(string message, bool asMod, string nick) {
      if (string.IsNullOrEmpty(nick)) nick = "testviewer";
      nick = nick.ToLowerInvariant();

      string line = "@badge-info=;badges=" + (asMod ? "moderator/1" : "")
                  + ";display-name=" + nick + ";mod=" + (asMod ? "1" : "0")
                  + ";user-id=999999 :" + nick + "!" + nick + "@" + nick
                  + ".tmi.twitch.tv PRIVMSG #" + (_channel.Length > 0 ? _channel : "channel")
                  + " :" + message;

      var ev = Parse(line);
      if (ev.Type != "PRIVMSG")
        return "{\"parsed\":false,\"why\":\"line did not parse as a chat message\"}";

      string reply, matched;
      bool ok = BotCommands.TryRespond(ev, out reply, out matched);
      return "{\"parsed\":true,\"isMod\":" + (ev.IsMod ? "true" : "false")
           + ",\"matched\":" + Qs(matched ?? "")
           + ",\"answered\":" + (ok ? "true" : "false")
           + ",\"reply\":" + Qs(reply ?? "") + "}";
    }

    // ------------------------------------------------------------------ status
    public static string StatusJson() {
      var sb = new StringBuilder();
      sb.Append('{');
      sb.Append("\"status\":").Append(Qs(_status)).Append(',');
      sb.Append("\"detail\":").Append(Qs(_detail)).Append(',');
      sb.Append("\"enabled\":").Append(_enabled ? "true" : "false").Append(',');
      sb.Append("\"configured\":").Append(Configured ? "true" : "false").Append(',');
      sb.Append("\"channel\":").Append(Qs(_channel)).Append(',');
      sb.Append("\"botUser\":").Append(Qs(_botUser)).Append(',');
      sb.Append("\"connectedAt\":").Append(Qs(_connectedAt)).Append(',');
      sb.Append("\"reconnects\":").Append(_reconnects).Append(',');
      sb.Append("\"sent\":").Append(_sent).Append(',');
      sb.Append("\"scopeState\":").Append(Qs(_scopeState)).Append(',');
      sb.Append("\"scopeUser\":").Append(Qs(_scopeUser)).Append(',');
      sb.Append("\"scopeProblem\":").Append(Qs(ScopeProblem())).Append(',');
      sb.Append("\"recent\":[");
      lock (_recent) {
        for (int i = _recent.Count - 1, n = 0; i >= 0; i--, n++) {
          if (n > 0) sb.Append(',');
          var r = _recent[i];
          sb.Append("{\"at\":").Append(Qs(r.At))
            .Append(",\"who\":").Append(Qs(r.Who))
            .Append(",\"cmd\":").Append(Qs(r.Cmd))
            .Append(",\"text\":").Append(Qs(r.Text)).Append('}');
        }
      }
      sb.Append("],");
      sb.Append("\"commands\":").Append(BotCommands.Json());
      sb.Append('}');
      return sb.ToString();
    }

    internal static string Qs(string s) {
      if (s == null) return "\"\"";
      var sb = new StringBuilder("\"");
      foreach (char c in s) {
        if (c == '"' || c == '\\') sb.Append('\\').Append(c);
        else if (c == '\n') sb.Append("\\n");
        else if (c == '\r') sb.Append("\\r");
        else if (c == '\t') sb.Append("\\t");
        else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
        else sb.Append(c);
      }
      return sb.Append('"').ToString();
    }
  }
}
