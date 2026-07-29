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
    static int _followerGoal;          // 0 = round up automatically
    static int _subGoal;

    static string _broadcasterId = "";

    // "off" until configured, then one of: connecting, live, bad-token,
    // missing-scope, not-affiliate, error. The pages render each differently -
    // an expired token has to look different from "nobody has followed yet".
    static volatile string _status = "off";
    static volatile string _detail = "";

    public static bool Configured { get { return _clientId.Length > 0 && _token.Length > 0; } }
    public static string Status { get { return _status; } }

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
        File.WriteAllText(StatePath(), body);
      } catch { }
    }

    static void LoadConfig() {
      string p = FindConfig();
      if (p == null) { _status = "off"; _detail = "no twitch-config.json found"; return; }
      try {
        var ser = new JavaScriptSerializer();
        var cfg = ser.DeserializeObject(File.ReadAllText(p)) as Dictionary<string, object>;
        if (cfg == null) { _status = "error"; _detail = "twitch-config.json is not valid JSON"; return; }

        _channel = Str(cfg, "channel").Trim().TrimStart('#').ToLowerInvariant();
        _clientId = Str(cfg, "clientId").Trim();
        _token = Str(cfg, "apiToken").Trim();
        // Token generators hand these out with the chat prefix attached; Helix
        // wants the bare token, so accept either and normalise.
        if (_token.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase)) _token = _token.Substring(6);
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
        _status = "error"; _detail = "could not read twitch-config.json: " + e.Message;
      }
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

    // A 401 means the token is dead and no amount of retrying fixes it, so say
    // so plainly rather than letting the pages sit on a stale count forever.
    static bool NoteAuthFailure(int http, string what) {
      if (http == 401) {
        _status = "bad-token";
        _detail = "Twitch rejected the token (401) on " + what + ". Regenerate it - user tokens expire.";
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
      string body = HelixGet("https://api.twitch.tv/helix/users?login=" + Uri.EscapeDataString(_channel), out http);
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

      string f = HelixGet("https://api.twitch.tv/helix/channels/followers?first=1&broadcaster_id=" + _broadcasterId, out http);
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

      string s = HelixGet("https://api.twitch.tv/helix/subscriptions?first=1&broadcaster_id=" + _broadcasterId, out http);
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

      while (true) {
        ClientWebSocket ws = null;
        try {
          ws = new ClientWebSocket();
          ws.ConnectAsync(new Uri(url), CancellationToken.None).Wait();
          url = "wss://eventsub.wss.twitch.tv/ws";   // reconnect_url is single-use

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
              // Twitch closes the socket if no subscription is created within
              // 10s of the welcome, so this cannot be deferred.
              subscribed = CreateSubscriptions(sid);
              if (subscribed) { _status = "live"; _detail = ""; backoff = 5; }
            } else if (type == "session_keepalive") {
              // nothing to do; receiving it is the point
            } else if (type == "session_reconnect") {
              string next = SNav(msg, "payload", "session", "reconnect_url");
              if (next.Length > 0) url = next;
              break;   // reconnect immediately rather than overlapping sockets
            } else if (type == "revocation") {
              _status = "missing-scope";
              _detail = "Twitch revoked a subscription: " + SNav(msg, "payload", "status");
            } else if (type == "notification") {
              if (!AlreadySeen(mid)) HandleNotification(msg);
            }
          }
        } catch {
          // fall through to backoff
        } finally {
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
      string resp = HelixPost("https://api.twitch.tv/helix/eventsub/subscriptions", body, out http);
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
      LoadConfig();
      if (!Configured) return;

      // .NET Framework still negotiates TLS 1.0 by default on some machines and
      // Twitch refuses it, which surfaces as a bare "unable to connect" with no
      // hint that the protocol is the problem. Pin 1.2 up front.
      try {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
      } catch { }

      _status = "connecting";

      var t = new Thread(() => {
        if (!ResolveBroadcaster()) return;
        PollTotals();

        var ws = new Thread(WsLoop);
        ws.IsBackground = true;
        ws.Start();

        while (true) {
          Thread.Sleep(60000);
          try { PollTotals(); } catch { }
        }
      });
      t.IsBackground = true;
      t.Start();
    }
  }
}
