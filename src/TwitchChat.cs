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
    static string _botRefresh = "";   // written by the bot setup flow; empty for hand-made tokens

    // "off" until configured, then: disabled, connecting, live, bad-token, error.
    // disabled is distinct from off on purpose - "you switched it off" and "it
    // was never set up" need different things said about them.
    static volatile string _status = "off";
    static volatile string _detail = "";
    static volatile string _connectedAt = "";
    static volatile string _lastNotice = "";     // Twitch's last NOTICE, verbatim
    static volatile string _lastNoticeAt = "";
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
    // The live socket, so a stop can close it out from under the blocking
    // read. Without that, a disabled bot would sit in ReadLine and keep
    // answering commands until the next line of chat happened to arrive.
    static volatile TcpClient _tcp;

    static void CloseSocket() {
      try { var t = _tcp; if (t != null) t.Close(); } catch { }
    }

    // ---------------------------------------------------------- follow thanks
    // The bot thanks new followers in chat. Driven by the EventSub follow
    // event from TwitchEvents, not by anything a viewer can type - which is
    // what makes it different from a command: chat cannot trigger it at all.
    // The two abuse paths that remain are follow/unfollow toggling and
    // follow-bot waves, and both are closed here: a name is thanked at most
    // once per app run, and a burst becomes one message naming a few people,
    // never a message per follow.
    static volatile bool _followThanks = true;
    static string _followTemplate = "Thanks for the follow, {user}!";
    static readonly object _thanksLock = new object();
    static readonly List<string> _pendingThanks = new List<string>();
    static DateTime _oldestPendingAt;
    static DateTime _lastThanksAt = DateTime.MinValue;
    static readonly HashSet<string> _thanked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static readonly Queue<string> _thankedOrder = new Queue<string>();

    // The live connection's writer, so the thanks flusher can speak while the
    // read loop sits in its blocking ReadLine. One reader plus one writer on
    // an SslStream is fine; two writers are not, so every PRIVMSG/PONG the
    // read loop sends goes through _sendLock too.
    static volatile StreamWriter _wr;
    static readonly object _sendLock = new object();

    public static void OnFollow(string user) {
      if (!_enabled || !_followThanks || string.IsNullOrEmpty(user)) return;
      lock (_thanksLock) {
        if (_thanked.Contains(user)) return;         // refollow toggling: one thanks, ever
        foreach (var p in _pendingThanks)
          if (string.Equals(p, user, StringComparison.OrdinalIgnoreCase)) return;
        if (_pendingThanks.Count >= 50) return;      // a bot wave past this is just noise
        if (_pendingThanks.Count == 0) _oldestPendingAt = DateTime.UtcNow;
        _pendingThanks.Add(user);
      }
    }

    // One line, however many followed: "a, b and 3 more". The cap on named
    // people keeps a raid's thank-you shorter than the raid.
    static string BuildThanks(List<string> names) {
      string who;
      if (names.Count == 1) who = names[0];
      else if (names.Count <= 3)
        who = string.Join(", ", names.GetRange(0, names.Count - 1).ToArray())
            + " and " + names[names.Count - 1];
      else
        who = names[0] + ", " + names[1] + " and " + (names.Count - 2) + " more";
      return _followTemplate.Replace("{user}", who);
    }

    static Thread _thanksThread;

    static void StartThanksThread() {
      if (_thanksThread != null && _thanksThread.IsAlive) return;
      _thanksThread = new Thread(ThanksLoop);
      _thanksThread.IsBackground = true;
      _thanksThread.Start();
    }

    static void ThanksLoop() {
      while (true) {
        Thread.Sleep(1000);
        try {
          if (!_enabled || _status != "live" || _wr == null) {
            // A thank-you delivered half an hour late reads as a glitch, so
            // anything that has waited out a long disconnect is let go. The
            // names were never marked thanked, so a genuinely new follow
            // while the bot was down simply goes unthanked, not double-thanked.
            lock (_thanksLock) {
              if (_pendingThanks.Count > 0
                  && (DateTime.UtcNow - _oldestPendingAt).TotalMinutes > 5)
                _pendingThanks.Clear();
            }
            continue;
          }
          string msg = null;
          lock (_thanksLock) {
            if (_pendingThanks.Count == 0) continue;
            // Linger a few seconds so a burst lands as one message, and never
            // thank more often than every 15s no matter what Twitch sends.
            if ((DateTime.UtcNow - _oldestPendingAt).TotalSeconds < 4) continue;
            if ((DateTime.UtcNow - _lastThanksAt).TotalSeconds < 15) continue;
            msg = BuildThanks(_pendingThanks);
            foreach (var n in _pendingThanks) {
              if (!_thanked.Add(n)) continue;
              _thankedOrder.Enqueue(n);
              while (_thankedOrder.Count > 500) _thanked.Remove(_thankedOrder.Dequeue());
            }
            _pendingThanks.Clear();
            _lastThanksAt = DateTime.UtcNow;
          }
          var wr = _wr;
          if (wr != null && msg.Length > 0) {
            lock (_sendLock) {
              wr.WriteLine("PRIVMSG #" + _channel + " :" + Dedup(msg));
              _lastSent = msg;
            }
            _sent++;
            Note("(new follower)", "follow-thanks", msg);
          }
        } catch {
          // a socket dying mid-write is the reconnect loop's problem, not ours
        }
      }
    }

    public static void SetFollowThanks(bool on, string template) {
      _followThanks = on;
      if (template != null) {
        template = template.Trim().Replace("\\n", " ").Replace("\n", " ");
        if (template.Length == 0) template = "Thanks for the follow, {user}!";
        if (template.Length > 400) template = template.Substring(0, 400);
        // One chat line, always: splitting is for commands; this feature's
        // whole reason to exist is staying unspammable.
        _followTemplate = template;
      }
      TwitchEvents.SaveBotFollowThanks(_followThanks, _followTemplate);
      AppLog.Write("chat: follow thanks " + (on ? "on" : "off"));
    }

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
        _botRefresh = Cfg(cfg, "botRefreshToken").Trim();

        // Follow thank-you: on unless the config says otherwise (it only
        // speaks when the bot itself is on, so "on by default" costs nothing
        // for anyone who never enables the bot).
        string ft = Cfg(cfg, "followThanks").Trim().ToLowerInvariant();
        _followThanks = !(ft == "0" || ft == "false" || ft == "off");
        string ftt = Cfg(cfg, "followThanksTemplate").Trim();
        if (ftt.Length > 0) _followTemplate = ftt;

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
        // Same leak as TwitchEvents.LoadConfig: the raw parse-error message
        // carries the whole file, tokens included, onto diagnostic surfaces.
        _status = "error"; _detail = "could not read twitch-config.json: " + TwitchEvents.SafeParseError(e);
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
      StartThanksThread();
      if (_enabled && Configured) StartThread();
    }

    // Called by the settings loader before Start, so a bot switched on last time
    // comes back on by itself rather than needing a click after every restart.
    public static void RestoreEnabled(bool on) { _enabled = on; }

    // Called from the bot OAuth callback after new credentials land in the
    // config. The IRC thread reads its credentials once at connect, so a
    // running bot has to be cycled; one that was never configured just loads
    // and sits ready for the switch. The old thread can take a few seconds to
    // notice the stop flag (its read timeout), so the wait happens off-thread
    // rather than stalling the OAuth redirect.
    public static void ReloadAfterSetup() {
      var old = _thread;
      if (old != null && old.IsAlive) {
        _stopFlag = true;
        CloseSocket();
        var t = new Thread(() => {
          try { old.Join(15000); } catch { }
          LoadConfig();
          CheckScopes();
          if (_enabled && Configured) StartThread();
        });
        t.IsBackground = true;
        t.Start();
      } else {
        LoadConfig();
        CheckScopes();
        if (_enabled && Configured) StartThread();
      }
    }

    static void StartThread() {
      if (!Configured) { AppLog.Write("chat: cannot start, not configured - " + _detail); return; }
      var old = _thread;
      if (old != null && old.IsAlive) {
        if (!_stopFlag) return;              // already running and staying up
        // A stop was requested moments ago and the old thread hasn't noticed
        // yet - its read timeout is several seconds. Returning here is how a
        // quick off-on flick used to end with the switch on and no thread at
        // all; instead, wait out the old thread and then start, unless the
        // user flicked off again in the meantime.
        _status = "connecting";
        var w = new Thread(() => {
          try { old.Join(15000); } catch { }
          if (_enabled) StartThread();
        });
        w.IsBackground = true;
        w.Start();
        return;
      }
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
      CloseSocket();      // wake the blocking read so the stop takes effect now
      _status = "disabled";
      _detail = "";
      _connectedAt = "";
    }

    // ---------------------------------------------------------- token refresh
    // Chat tokens minted by the setup flow expire in hours, exactly like the
    // Helix one in TwitchEvents. A live IRC connection survives past expiry -
    // Twitch only checks PASS at login - so the moment this matters is the
    // next (re)connect, which is where AUTHFAIL lands. Hand-made tokens have
    // no refresh half and keep today's behaviour: a clear bad-token message.
    static readonly object _botRefreshLock = new object();
    static int _authFails;

    static bool TryRefreshBotToken() {
      lock (_botRefreshLock) {
        if (_botRefresh.Length == 0) return false;
        string access, refresh;
        if (!TwitchEvents.RefreshWithApp(_botRefresh, out access, out refresh)) return false;
        _token = "oauth:" + access;
        if (refresh.Length > 0) _botRefresh = refresh;
        TwitchEvents.SaveBotTokens(access, _botRefresh);
        AppLog.Write("chat: bot token refreshed automatically");
        return true;
      }
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
          _tcp = tcp;
          ssl = new SslStream(tcp.GetStream(), false);
          // TLS 1.2 named explicitly. The parameterless overload negotiates
          // with SslStream's legacy defaults (TLS 1.0) on machines that lack
          // the .NET strong-crypto registry keys, and Twitch kills that
          // handshake immediately - which surfaces as "connection forcibly
          // closed by the remote host" right after connect, or an SSPI
          // failure, on some PCs while others connect fine.
          var protos = System.Security.Authentication.SslProtocols.Tls12;
          // 1.3 as well wherever the runtime knows it (4.8+), so a future
          // Twitch 1.2 shutoff is a non-event here too.
          if (Enum.IsDefined(typeof(System.Security.Authentication.SslProtocols), 12288))
            protos |= (System.Security.Authentication.SslProtocols)12288;
          ssl.AuthenticateAsClient("irc.chat.twitch.tv", null, protos, false);
          // A read timeout on SslStream is FATAL: .NET documents that a
          // timed-out SslStream is out of sync and reading on returns
          // garbage. This loop once used a 6-second timeout as a stop-flag
          // poll and kept reading - so in a QUIET channel the stream was
          // dead six seconds after login: the bot stayed "logged in", heard
          // no one, answered no PING, and Twitch cut it ~10 minutes later.
          // Busy channels never showed it, because chat arrived before the
          // first timeout ever fired. Reads now block; six minutes of total
          // silence (Twitch pings every ~5) means the connection is gone,
          // and that timeout abandons the stream like the docs demand.
          ssl.ReadTimeout = 360000;

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
            catch (IOException) {
              // Six minutes of dead air, or the transport failed under us.
              // Either way this stream must not be read again - reconnect.
              if (!_stopFlag) AppLog.Write("chat: connection went quiet or broke - reconnecting");
              break;
            }
            catch (ObjectDisposedException) { break; }  // stop closed the socket
            if (line == null) break;                 // server closed the socket

            var ev = Parse(line);
            switch (ev.Type) {
              case "PING":
                lock (_sendLock) wr.WriteLine("PONG :" + ev.Text);
                break;

              case "WELCOME":
                _status = "live";
                _detail = "";
                _connectedAt = DateTime.Now.ToString("HH:mm:ss");
                backoff = 5;
                _authFails = 0;
                _wr = wr;      // from here the thanks flusher may speak too
                AppLog.Write("chat: logged in as " + _botUser);
                break;

              case "AUTHFAIL":
                // An expired token and a revoked one look identical from here.
                // If the setup flow left a refresh token, spend it once and try
                // the login again; a second rejection in a row means the
                // credential is genuinely dead, and hammering Twitch with a bad
                // one is how an account gets rate limited.
                if (_authFails++ == 0 && TryRefreshBotToken()) {
                  AppLog.Write("chat: login rejected, retrying with a freshly refreshed token");
                  goto reconnect;
                }
                _status = "bad-token";
                _detail = "Twitch rejected the bot login (" + ev.Text + "). The bot account's "
                        + "oauthToken needs chat:read and chat:edit, and tokens expire.";
                AppLog.Write("chat: AUTH FAILED - " + _detail);
                _stopFlag = true;
                break;

              case "NOTICE":
                if (ev.Text.Length > 0) {
                  _lastNotice = ev.Text;
                  _lastNoticeAt = DateTime.Now.ToString("HH:mm:ss");
                  AppLog.Write("chat: server notice: " + ev.Text);
                }
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
          _wr = null;         // the flusher must never write to a dead stream
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
        // Every other NOTICE is Twitch explaining itself, and some of them are
        // the only evidence of a message being dropped: "requires a verified
        // email", followers-only mode, rate limits. Swallowing them turns
        // "the bot replied but nothing appeared in chat" into a mystery.
        // Matched by prefix, not IndexOf - a viewer typing the word NOTICE in
        // a PRIVMSG must not be mistaken for the server speaking.
        if (line.StartsWith(":tmi.twitch.tv NOTICE ", StringComparison.Ordinal)) {
          e.Type = "NOTICE";
          int nc = line.IndexOf(':', 1);
          e.Text = nc >= 0 ? line.Substring(nc + 1) : "";
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

      // A reply may be several chat messages: "\n" in the command text splits
      // it. Capped at five - Twitch rate-limits at 20 messages per 30 seconds
      // and temporarily mutes an account that sails past it, so a runaway
      // paste must not turn one command into a flood.
      int sent = 0;
      foreach (string raw in reply.Split('\n')) {
        string one = raw.Trim();
        if (one.Length == 0) continue;
        if (sent == 5) break;
        // Paced, not rapid-fire: Twitch's edge silently swallows back-to-back
        // messages from an ordinary account - the first line lands and the
        // rest just never appear, no NOTICE, nothing. Same filter that mutes
        // an account past the rate limit, and the reason every chat bot
        // library paces its outgoing queue (tmi.js ships at 600ms). Sleeping
        // on the read loop is fine at this scale: five lines hold it for two
        // seconds against a PING cadence of five minutes.
        if (sent > 0) Thread.Sleep(600);
        // _sendLock: the follow-thanks flusher writes on its own thread, and
        // two writers interleaving on one SslStream corrupt the TLS stream.
        lock (_sendLock) {
          wr.WriteLine("PRIVMSG #" + _channel + " :" + Dedup(one));
          _lastSent = one;
        }
        _sent++;
        sent++;
      }
      if (sent > 0) Note(ev.Nick, matched, reply);
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
        // Two passes at most: if the first answer is 401 and a refresh token
        // exists, an expired token is a non-event - renew it and ask again.
        // That covers the common case of the app having been closed for days.
        for (int attempt = 0; ; attempt++) {
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
              return;
            }
          } catch (System.Net.WebException we) {
            var r = we.Response as System.Net.HttpWebResponse;
            bool unauthorized = r != null && (int)r.StatusCode == 401;
            if (unauthorized && attempt == 0 && TryRefreshBotToken()) continue;
            _scopeState = unauthorized ? "bad" : "error";
            AppLog.Write("chat: token validation failed (" + _scopeState + ")");
            return;
          } catch { _scopeState = "error"; return; }
        }
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
      if (_scopeState == "bad") return "Twitch rejected this token (401). It has expired or was revoked - press the setup button below to reconnect as " + _botUser + ".";
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
      sb.Append("\"followThanks\":").Append(_followThanks ? "true" : "false").Append(',');
      sb.Append("\"followTemplate\":").Append(Qs(_followTemplate)).Append(',');
      sb.Append("\"configured\":").Append(Configured ? "true" : "false").Append(',');
      sb.Append("\"channel\":").Append(Qs(_channel)).Append(',');
      sb.Append("\"botUser\":").Append(Qs(_botUser)).Append(',');
      sb.Append("\"connectedAt\":").Append(Qs(_connectedAt)).Append(',');
      sb.Append("\"reconnects\":").Append(_reconnects).Append(',');
      sb.Append("\"sent\":").Append(_sent).Append(',');
      sb.Append("\"lastNotice\":").Append(Qs(_lastNotice)).Append(',');
      sb.Append("\"lastNoticeAt\":").Append(Qs(_lastNoticeAt)).Append(',');
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
