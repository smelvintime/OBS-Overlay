// League of Legends game results, read from the League client's own local
// API (the "LCU") on this PC.
//
// Why this and not the Riot web API: dev keys expire every 24 hours and a
// permanent key needs an approved application - unusable for someone who
// just downloaded an overlay. The client that is already running on the
// streaming PC serves the same answers locally with no key, no sign-in and
// no internet dependency, which is the same reasoning that picked the
// Windows media session over per-service music APIs.
//
// The API is unofficial and can shift between patches, so everything here is
// best-effort in the TwitchEvents mould: a missing client, a moved endpoint
// or a weird payload must leave the bot silent with a readable status on the
// dashboard - never a crash, never a wrong announcement.
//
// Like iTunes: the client is only ever read while its process is already
// running. Nothing here can start League.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace NowPlaying {

  static class LeagueStats {

    // ------------------------------------------------------------------ state
    static volatile bool _enabled;                 // driven by the bot's Game stats switch
    static volatile string _status = "off";        // off | no-client | live | error
    static volatile string _detail = "";
    static volatile string _summoner = "";

    static readonly object _resultLock = new object();
    static string _record = "";                    // "W W L L W", newest first
    static string _lastLine = "";                  // "Victory (12/3/8)"
    static long _newestGameId;
    static string _newestAt = "";

    public static string Status { get { return _status; } }

    // A monotonically-increasing marker for "a new game landed", so the chat
    // side can tell a fresh result from the one it already posted.
    static long _resultSeq;
    public static long ResultSeq { get { return Interlocked.Read(ref _resultSeq); } }

    public static void SetEnabled(bool on) {
      if (_enabled == on) return;
      _enabled = on;
      if (!on) { _status = "off"; _detail = ""; }
      AppLog.Write("league: tracking " + (on ? "on" : "off"));
    }

    // ------------------------------------------------------------- discovery
    // The lockfile sits beside LeagueClient.exe and exists only while the
    // client runs; it carries the local API's port and password. Found via
    // the live process rather than a hard-coded install path, so a D:\ or
    // moved install works the same.
    static bool FindLockfile(out int port, out string password) {
      port = 0; password = "";
      foreach (var name in new[] { "LeagueClient", "LeagueClientUx" }) {
        System.Diagnostics.Process[] procs = null;
        try {
          procs = System.Diagnostics.Process.GetProcessesByName(name);
          foreach (var p in procs) {
            try {
              string dir = Path.GetDirectoryName(p.MainModule.FileName);
              string lf = Path.Combine(dir, "lockfile");
              if (!File.Exists(lf)) continue;
              // The client keeps the lockfile open for writing; a plain read
              // throws a sharing violation, so share generously.
              string text;
              using (var fs = new FileStream(lf, FileMode.Open, FileAccess.Read,
                                             FileShare.ReadWrite | FileShare.Delete))
              using (var sr = new StreamReader(fs, Encoding.ASCII))
                text = sr.ReadToEnd();
              // ProcessName:PID:port:password:protocol
              var parts = text.Trim().Split(':');
              if (parts.Length >= 5 && int.TryParse(parts[2], out port) && port > 0) {
                password = parts[3];
                return true;
              }
            } catch { }     // an elevated client hides MainModule; try the next
          }
        } catch { }
        finally { if (procs != null) foreach (var p in procs) { try { p.Dispose(); } catch { } } }
      }
      return false;
    }

    // ----------------------------------------------------------------- HTTPS
    // Raw SslStream rather than HttpWebRequest, for two reasons that turned
    // out to be the same reason. HttpWebRequest's TLS settings are process
    // GLOBAL (ServicePointManager), so accepting the LCU's self-signed cert
    // would have meant a global validation callback that every Twitch call
    // also flows through - and in practice the global path could not even
    // complete a handshake with the LCU (SecureChannelFailure before
    // validation ever ran). A per-connection SslStream pins trust to exactly
    // one loopback socket and nothing else in the process, the same pattern
    // TwitchChat already uses for IRC-over-TLS.
    //
    // The last transport-level failure rides into the status detail -
    // "starting up" and "the handshake failed" need different people to act.
    static volatile string _lastHttpError = "";

    static string LcuGet(int port, string password, string path) {
      try {
        using (var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", port)) {
          tcp.ReceiveTimeout = 5000;
          tcp.SendTimeout = 5000;
          using (var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false,
                   // Self-signed by design and reachable only over loopback;
                   // this trust never leaves this one connection.
                   delegate { return true; })) {
            ssl.AuthenticateAsClient("127.0.0.1", null,
              System.Security.Authentication.SslProtocols.Tls12, false);

            string reqText = "GET " + path + " HTTP/1.1\r\n"
              + "Host: 127.0.0.1:" + port + "\r\n"
              + "Authorization: Basic " + Convert.ToBase64String(
                  Encoding.ASCII.GetBytes("riot:" + password)) + "\r\n"
              + "Accept: application/json\r\n"
              + "User-Agent: NowPlayingOverlay\r\n"
              + "Connection: close\r\n\r\n";
            var reqBytes = Encoding.ASCII.GetBytes(reqText);
            ssl.Write(reqBytes, 0, reqBytes.Length);
            ssl.Flush();

            // Connection: close means "read until the server hangs up" is the
            // whole framing story, bar chunked encoding handled below.
            var ms = new MemoryStream();
            var buf = new byte[16384];
            int n;
            try { while ((n = ssl.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n); }
            catch (IOException) { }      // close without close_notify is normal here

            byte[] all = ms.ToArray();
            int hdrEnd = IndexOfHeaderEnd(all);
            if (hdrEnd < 0) { _lastHttpError = "malformed response"; return null; }
            string head = Encoding.ASCII.GetString(all, 0, hdrEnd);
            string statusLine = head.Split('\r')[0];
            if (statusLine.IndexOf(" 200", StringComparison.Ordinal) < 0) {
              _lastHttpError = statusLine;
              return null;
            }
            int bodyStart = hdrEnd + 4;
            byte[] body;
            if (head.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0) {
              body = DeChunk(all, bodyStart);
            } else {
              body = new byte[all.Length - bodyStart];
              Array.Copy(all, bodyStart, body, 0, body.Length);
            }
            return Encoding.UTF8.GetString(body);
          }
        }
      } catch (Exception ex) {
        _lastHttpError = ex.GetType().Name + ": " + ex.Message;
        return null;
      }
    }

    static int IndexOfHeaderEnd(byte[] b) {
      for (int i = 0; i + 3 < b.Length; i++)
        if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
      return -1;
    }

    // Minimal HTTP/1.1 chunked-transfer decoding, walked in BYTES: a chunk
    // size is an octet count (RFC 7230), so framing must happen before any
    // text decoding - one multi-byte character in a payload would otherwise
    // shift every offset after it. On anything unexpected, return what has
    // been assembled; the JSON parse upstream fails gracefully.
    static byte[] DeChunk(byte[] all, int pos) {
      var outMs = new MemoryStream();
      try {
        while (true) {
          int nl = -1;
          for (int i = pos; i + 1 < all.Length; i++)
            if (all[i] == 13 && all[i + 1] == 10) { nl = i; break; }
          if (nl < 0) break;
          string sizeHex = Encoding.ASCII.GetString(all, pos, nl - pos).Trim();
          int semi = sizeHex.IndexOf(';');
          if (semi >= 0) sizeHex = sizeHex.Substring(0, semi);
          int size = Convert.ToInt32(sizeHex, 16);
          if (size <= 0) break;
          if (nl + 2 + size > all.Length) break;
          outMs.Write(all, nl + 2, size);
          pos = nl + 2 + size + 2;       // skip the chunk's trailing CRLF
        }
      } catch { }
      return outMs.ToArray();
    }

    // ----------------------------------------------------------------- parse
    // Pure: JSON in, results out, no state touched - so /league/test can push
    // a canned payload through the exact code the live path uses.
    internal static bool ParseHistory(string json, out string record, out string lastLine,
                                      out long newestId, out string newestAt) {
      record = ""; lastLine = ""; newestId = 0; newestAt = "";
      try {
        var games = TwitchEvents.NavPublic(json, "games", "games") as object[];
        if (games == null || games.Length == 0) return false;

        // Sorted here rather than trusting the endpoint's order - the LCU has
        // returned this list both oldest-first and newest-first over the
        // years, and a flipped record reads as five wrong results.
        var list = new List<object>(games);
        list.Sort(delegate(object a, object b) {
          return LNav(b, "gameCreation").CompareTo(LNav(a, "gameCreation"));
        });

        var letters = new List<string>();
        for (int i = 0; i < list.Count && letters.Count < 5; i++) {
          object stats = FirstParticipantStats(list[i]);
          if (stats == null) continue;
          // stats.win is a JSON boolean (verified against the live client);
          // Convert.ToString(bool) yields "True"/"False".
          bool win = TwitchEvents.SNavPublic(stats, "win").Equals("True", StringComparison.OrdinalIgnoreCase);
          letters.Add(win ? "W" : "L");
          if (letters.Count == 1) {
            newestId = LNav(list[i], "gameId");
            newestAt = TwitchEvents.SNavPublic(list[i], "gameCreationDate");
            string k = TwitchEvents.SNavPublic(stats, "kills");
            string d = TwitchEvents.SNavPublic(stats, "deaths");
            string s = TwitchEvents.SNavPublic(stats, "assists");
            lastLine = (win ? "Victory" : "Defeat")
                     + (k.Length > 0 ? " (" + k + "/" + d + "/" + s + ")" : "");
          }
        }
        if (letters.Count == 0) return false;
        record = string.Join(" ", letters.ToArray());
        return true;
      } catch { return false; }
    }

    // This endpoint's participants[] holds only the current player, so the
    // first entry's stats block is the streamer's own numbers.
    static object FirstParticipantStats(object game) {
      var parts = Nav(game, "participants") as object[];
      if (parts == null || parts.Length == 0) return null;
      return Nav(parts[0], "stats");
    }

    static object Nav(object o, params string[] path) {
      foreach (var key in path) {
        var d = o as Dictionary<string, object>;
        if (d == null) return null;
        if (!d.TryGetValue(key, out o)) return null;
      }
      return o;
    }

    static long LNav(object o, string key) {
      var v = Nav(o, key);
      if (v == null) return 0;
      try { return Convert.ToInt64(v); } catch { return 0; }
    }

    // ------------------------------------------------------------------ loop
    public static void Start() {
      var t = new Thread(Loop);
      t.IsBackground = true;
      t.Start();
    }

    static void Loop() {
      string phase = "";
      int freshPolls = 0;        // >0 = a game just ended; chase the updated history
      DateTime lastHistory = DateTime.MinValue;

      while (true) {
        try {
          if (!_enabled) {
            // Re-asserted every pass, not just in SetEnabled: a poll that was
            // mid-flight when the switch flipped can land afterwards and
            // stamp "live" over the off state - this wins the race by being
            // repeated.
            _status = "off"; _detail = "";
            Thread.Sleep(2000);
            continue;
          }

          int port; string pw;
          if (!FindLockfile(out port, out pw)) {
            _status = "no-client";
            _detail = "the League client is not running on this PC";
            phase = ""; freshPolls = 0;
            Thread.Sleep(10000);
            continue;
          }
          // Phase first: it is the cheap call, and the InProgress -> ended
          // transition is the moment the announcement exists for.
          string ph = LcuGet(port, pw, "/lol-gameflow/v1/gameflow-phase");
          if (ph == null) {
            // Client up but API not answering (still booting, or the port
            // just changed). The transport error rides along so a machine
            // that never connects can be diagnosed from the dashboard.
            _status = "no-client";
            _detail = "the League client is starting up"
                    + (_lastHttpError.Length > 0 ? " (" + _lastHttpError + ")" : "");
            Thread.Sleep(10000);
            continue;
          }
          ph = ph.Trim().Trim('"');
          bool gameJustEnded = phase == "InProgress" && ph != "InProgress";
          phase = ph;
          if (gameJustEnded) {
            AppLog.Write("league: game ended (phase -> " + ph + ")");
            freshPolls = 9;      // history lags the end screen; chase it ~90s
          }

          if (_summoner.Length == 0) {
            string me = LcuGet(port, pw, "/lol-summoner/v1/current-summoner");
            if (me != null) {
              _summoner = TwitchEvents.SNavPublic(TwitchEvents.NavPublic(me), "displayName");
              if (_summoner.Length == 0)
                _summoner = TwitchEvents.SNavPublic(TwitchEvents.NavPublic(me), "gameName");
            }
          }

          bool wantHistory = freshPolls > 0
                          || (DateTime.UtcNow - lastHistory).TotalSeconds > 300
                          || _newestGameId == 0;
          if (wantHistory) {
            if (freshPolls > 0) freshPolls--;
            string hist = LcuGet(port, pw,
              "/lol-match-history/v1/products/lol/current-summoner/matches?begIndex=0&endIndex=6");
            if (hist != null) {
              lastHistory = DateTime.UtcNow;
              string record, lastLine, at; long newest;
              if (ParseHistory(hist, out record, out lastLine, out newest, out at)) {
                bool isNew;
                lock (_resultLock) {
                  isNew = _newestGameId != 0 && newest != 0 && newest != _newestGameId;
                  _record = record; _lastLine = lastLine;
                  if (newest != 0) _newestGameId = newest;
                  _newestAt = at;
                }
                _status = "live"; _detail = "";
                if (isNew) {
                  Interlocked.Increment(ref _resultSeq);
                  freshPolls = 0;              // found the new game; stop chasing
                  TwitchChat.OnGameEnded(ChatLine());
                }
              } else if (_status != "live") {
                _status = "error";
                _detail = "the League client answered, but the match history had an unexpected shape";
              }
            }
          }
          if (_status == "no-client") { _status = "live"; _detail = ""; }
        } catch (Exception ex) {
          _status = "error"; _detail = ex.Message;
        }
        Thread.Sleep(10000);
      }
    }

    // ------------------------------------------------------------------ output
    // The one sentence both the announcer and !record say.
    public static string ChatLine() {
      lock (_resultLock) {
        if (_record.Length == 0) return "";
        return (_lastLine.Length > 0 ? "Last game: " + _lastLine + " - " : "")
             + "past " + _record.Split(' ').Length + ": " + _record;
      }
    }

    // What !record answers when there is nothing to say yet.
    public static string CommandLine() {
      string line = ChatLine();
      if (line.Length > 0) return line;
      if (!_enabled) return "Game stats are switched off.";
      return _status == "no-client"
        ? "No games tracked yet - the League client isn't running."
        : "No games tracked yet this session.";
    }

    public static string StatusJson() {
      string record, last;
      lock (_resultLock) { record = _record; last = _lastLine; }
      var sb = new StringBuilder();
      sb.Append('{');
      sb.Append("\"enabled\":").Append(_enabled ? "true" : "false").Append(',');
      sb.Append("\"status\":").Append(TwitchChat.Qs(_status)).Append(',');
      sb.Append("\"detail\":").Append(TwitchChat.Qs(_detail)).Append(',');
      sb.Append("\"summoner\":").Append(TwitchChat.Qs(_summoner)).Append(',');
      sb.Append("\"record\":").Append(TwitchChat.Qs(record)).Append(',');
      sb.Append("\"last\":").Append(TwitchChat.Qs(last));
      sb.Append('}');
      return sb.ToString();
    }

    // ------------------------------------------------------------------- test
    // A canned history in the endpoint's real shape, pushed through the same
    // ParseHistory the live loop uses. Deliberately out of chronological
    // order, so a pass also proves the sort. Expected: "W L W L W", last game
    // Victory (12/3/8) - the newest by gameCreation, not by array position.
    public static string TestParse() {
      string fixture = "{\"games\":{\"games\":["
        + FixGame("104", "1700000400000", "true",  "5",  "1", "9")
        + "," + FixGame("101", "1700000100000", "false", "2", "7", "3")
        + "," + FixGame("105", "1700000500000", "true",  "12", "3", "8")
        + "," + FixGame("102", "1700000200000", "true",  "8",  "4", "6")
        + "," + FixGame("103", "1700000300000", "false", "4",  "6", "2")
        + "]}}";
      string record, lastLine, at; long newest;
      bool ok = ParseHistory(fixture, out record, out lastLine, out newest, out at);
      return "{\"ok\":" + (ok ? "true" : "false")
           + ",\"record\":" + TwitchChat.Qs(record)
           + ",\"last\":" + TwitchChat.Qs(lastLine)
           + ",\"newestGameId\":" + newest
           + ",\"expected\":\"record W W L W L, newest 105\"}";
    }

    const string FixTemplate = "{{\"gameId\":{0},\"gameCreation\":{1},"
      + "\"participants\":[{{\"stats\":{{\"win\":{2},\"kills\":{3},\"deaths\":{4},\"assists\":{5}}}}}]}}";

    static string FixGame(string id, string created, string win, string k, string d, string a) {
      return string.Format(FixTemplate, id, created, win, k, d, a);
    }
  }
}
