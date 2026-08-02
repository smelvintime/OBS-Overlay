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

    // Current rank, read from the client rather than typed into a dashboard -
    // a hand-maintained rank is wrong within an hour of playing ranked.
    static string _rankLine = "";                  // "Emerald II - 45 LP (Solo/Duo), 210W 198L this season"
    static DateTime _rankFetchedUtc = DateTime.MinValue;

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

    // Pure like ParseHistory: the ranked-stats payload in, the pieces out.
    // The endpoint reports every queue; solo queue is what "!rank" means to a
    // viewer, with flex as the fallback for a flex-only player. Returns true
    // when the payload parsed, even if it held no ranked entry - "unranked"
    // is an answer, not a failure. Master and above carry division "NA",
    // which is the endpoint's way of saying there isn't one.
    internal static bool ParseRankedStats(string json, out string tier, out string div,
                                          out int lp, out int wins, out int losses,
                                          out string queue) {
      tier = ""; div = ""; lp = 0; wins = 0; losses = 0; queue = "";
      try {
        object root = TwitchEvents.NavPublic(json);
        foreach (var q in new[] { "RANKED_SOLO_5x5", "RANKED_FLEX_SR" }) {
          object entry = Nav(root, "queueMap", q);
          if (entry == null) continue;
          string t = TwitchEvents.SNavPublic(entry, "tier").Trim();
          if (t.Length == 0 || t == "NONE" || t == "UNRANKED") continue;
          tier = t.Substring(0, 1) + t.Substring(1).ToLowerInvariant();   // EMERALD -> Emerald
          string d = TwitchEvents.SNavPublic(entry, "division").Trim();
          div = (d.Length > 0 && d != "NA") ? d : "";
          int.TryParse(TwitchEvents.SNavPublic(entry, "leaguePoints"), out lp);
          int.TryParse(TwitchEvents.SNavPublic(entry, "wins"), out wins);
          int.TryParse(TwitchEvents.SNavPublic(entry, "losses"), out losses);
          queue = q == "RANKED_SOLO_5x5" ? "Solo/Duo" : "Flex";
          return true;
        }
        return true;   // parsed fine, no ranked entry: tier stays ""
      } catch { return false; }
    }

    static string ComposeRankLine(string tier, string div, int lp, int wins, int losses, string queue) {
      if (tier.Length == 0) return "Unranked this season.";
      string line = tier + (div.Length > 0 ? " " + div : "") + " - " + lp + " LP (" + queue + ")";
      if (wins + losses > 0) line += ", " + wins + "W " + losses + "L this season";
      return line;
    }

    // The ladder flattened to one number, so a day's LP movement can be
    // reported across a promotion or demotion without lying at the border.
    // Divisions are 100 LP, tiers are 400; Master and up have no divisions
    // and continue from Diamond's ceiling.
    internal static int AbsoluteLp(string tier, string div, int lp) {
      string t = tier.ToUpperInvariant();
      var tiers = new[] { "IRON", "BRONZE", "SILVER", "GOLD", "PLATINUM", "EMERALD", "DIAMOND" };
      int ti = Array.IndexOf(tiers, t);
      if (ti >= 0) {
        int di = div == "I" ? 3 : div == "II" ? 2 : div == "III" ? 1 : 0;
        return ti * 400 + di * 100 + lp;
      }
      if (t == "MASTER" || t == "GRANDMASTER" || t == "CHALLENGER") return 7 * 400 + lp;
      return -1;
    }

    // One bounded fetch, shared by the poll loop and an on-demand !rank. The
    // on-demand path exists because the rank command must work even with the
    // Game stats announcer switched off - it costs one local call when a
    // viewer asks, which is the resource model the features page promises.
    static void FetchRank() {
      int port; string pw;
      if (!FindLockfile(out port, out pw)) return;
      string body = LcuGet(port, pw, "/lol-ranked/v1/current-ranked-stats");
      if (body == null) return;
      string tier, div, queue; int lp, wins, losses;
      if (!ParseRankedStats(body, out tier, out div, out lp, out wins, out losses, out queue)) return;
      int abs = AbsoluteLp(tier, div, lp);
      lock (_resultLock) {
        _rankTier = tier; _rankDiv = div; _rankLp = lp;
        _rankWins = wins; _rankLosses = losses; _rankQueue = queue;
        _rankLine = ComposeRankLine(tier, div, lp, wins, losses, queue);
      }
      _rankFetchedUtc = DateTime.UtcNow;
      RollDaySnapshot(abs);
    }

    // What !rank says. Answers from a recent read where possible; otherwise
    // asks the client directly, right now, so the reply is never a stale rank
    // - the entire reason this replaced a hand-typed text command.
    public static string RankCommandLine() {
      if ((DateTime.UtcNow - _rankFetchedUtc).TotalSeconds > 120) FetchRank();
      string line;
      lock (_resultLock) { line = _rankLine; }
      if (line.Length > 0) return line.StartsWith("Unranked") ? line : "Rank: " + line;
      return "Can't check the rank right now - the League client isn't running on this PC.";
    }

    // {rank}/{tier}/{lp}... tokens for a custom !rank template.
    public static void RankParts(out string line, out string tier, out string div,
                                 out int lp, out int wins, out int losses) {
      lock (_resultLock) {
        line = _rankLine; tier = _rankTier; div = _rankDiv;
        lp = _rankLp; wins = _rankWins; losses = _rankLosses;
      }
    }

    // {record}/{last} tokens for a custom !record template.
    public static void RecordParts(out string record, out string last) {
      lock (_resultLock) { record = _record; last = _lastLine; }
    }

    // {ranked}/{normals}/{aram}/{other}/{today} tokens: today's per-mode
    // tallies, the same numbers the session tracker draws. The arrays are
    // fresh per parse and never written after storing - read, don't touch.
    public static void TodayParts(out int[] wins, out int[] losses) {
      lock (_resultLock) { wins = _todayWinsB; losses = _todayLossesB; }
    }
    static int[] _todayWinsB = new int[4], _todayLossesB = new int[4];

    // ------------------------------------------------------- day LP snapshot
    // "How much LP today" needs to remember where the day started, and that
    // memory has to survive an app restart mid-session - so it lives in a
    // file, keyed by local date and summoner. A new day or a different
    // account starts a fresh baseline.
    static string _dayKey = "";      // "2026-08-02|SummonerName"
    static int _dayAbs = -1;
    static bool _dayLoaded;

    static string DayPath() {
      string dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NowPlayingOverlay");
      Directory.CreateDirectory(dir);
      return Path.Combine(dir, "league-day.json");
    }

    static void RollDaySnapshot(int absNow) {
      if (absNow < 0) return;                       // unranked: nothing to measure
      string key = DateTime.Today.ToString("yyyy-MM-dd") + "|" + _summoner;
      lock (_resultLock) {
        if (!_dayLoaded) {
          _dayLoaded = true;
          try {
            string p = DayPath();
            if (File.Exists(p)) {
              var d = new System.Web.Script.Serialization.JavaScriptSerializer()
                        .DeserializeObject(File.ReadAllText(p)) as Dictionary<string, object>;
              if (d != null) {
                object k, a;
                if (d.TryGetValue("key", out k) && k != null) _dayKey = Convert.ToString(k);
                if (d.TryGetValue("abs", out a) && a != null) _dayAbs = Convert.ToInt32(a);
              }
            }
          } catch { }
        }
        if (_dayKey != key || _dayAbs < 0) {
          _dayKey = key; _dayAbs = absNow;
          try {
            Files.WriteAtomic(DayPath(), "{\"key\":" + TwitchChat.Qs(key) + ",\"abs\":" + absNow + "}");
          } catch { }
        }
        _lpToday = absNow - _dayAbs;
        _hasLpToday = true;
      }
    }

    static int _lpToday;
    static bool _hasLpToday;
    static string _rankTier = "", _rankDiv = "", _rankQueue = "";
    static int _rankLp, _rankWins, _rankLosses;
    static string _todayJson = "[]";

    // ------------------------------------------------------- overlay support
    // The session tracker polls /league-state every few seconds while it is
    // visible in OBS; each poll stamps this. The loop stays alive for 30s
    // past the last poll, so closing the source really does stop the work.
    static long _wantedUntilTicks;

    public static void NoteOverlayInterest() {
      Interlocked.Exchange(ref _wantedUntilTicks, DateTime.UtcNow.AddSeconds(30).Ticks);
    }

    public static string OverlayJson() {
      string tier, div, queue, line, today, last, record; int lp, w, l, lpT; bool hasLp;
      lock (_resultLock) {
        tier = _rankTier; div = _rankDiv; queue = _rankQueue; line = _rankLine;
        lp = _rankLp; w = _rankWins; l = _rankLosses;
        lpT = _lpToday; hasLp = _hasLpToday;
        today = _todayJson; last = _lastLine; record = _record;
      }
      var sb = new StringBuilder();
      sb.Append('{');
      sb.Append("\"running\":").Append(_status == "live" ? "true" : "false").Append(',');
      sb.Append("\"status\":").Append(TwitchChat.Qs(_status)).Append(',');
      sb.Append("\"detail\":").Append(TwitchChat.Qs(_detail)).Append(',');
      sb.Append("\"summoner\":").Append(TwitchChat.Qs(_summoner)).Append(',');
      sb.Append("\"rank\":{\"tier\":").Append(TwitchChat.Qs(tier))
        .Append(",\"div\":").Append(TwitchChat.Qs(div))
        .Append(",\"lp\":").Append(lp)
        .Append(",\"queue\":").Append(TwitchChat.Qs(queue))
        .Append(",\"wins\":").Append(w)
        .Append(",\"losses\":").Append(l)
        .Append(",\"line\":").Append(TwitchChat.Qs(line)).Append("},");
      sb.Append("\"lpToday\":").Append(hasLp ? _lpTodayStr(lpT) : "null").Append(',');
      sb.Append("\"today\":").Append(today).Append(',');
      sb.Append("\"last\":").Append(TwitchChat.Qs(last)).Append(',');
      sb.Append("\"record\":").Append(TwitchChat.Qs(record));
      sb.Append('}');
      return sb.ToString();
    }

    static string _lpTodayStr(int v) { return v.ToString(System.Globalization.CultureInfo.InvariantCulture); }

    // Today's games from the same history payload, one entry per game with
    // its mode bucket, so the overlay can filter what counts without another
    // trip here. Pure, like the others. "Today" is the local calendar day,
    // and it survives an app restart because it is re-derived from history
    // every time rather than counted as the games happen. Practice tool and
    // tutorials are dropped entirely - a practice-tool "win" is not a game,
    // whatever the history endpoint thinks.
    // wins/losses come back tallied per bucket (ranked, normals, aram, other -
    // the BucketIx order) so !record templates can say "ranked only" without
    // re-walking anything. Fresh arrays every parse; readers treat them as
    // immutable.
    internal static bool ParseToday(string json, long sinceMs, out string gamesJson,
                                    out int[] wins, out int[] losses) {
      gamesJson = "[]";
      wins = new int[4]; losses = new int[4];
      try {
        var games = TwitchEvents.NavPublic(json, "games", "games") as object[];
        if (games == null) return false;
        var list = new List<object>(games);
        list.Sort(delegate(object a, object b) {
          return LNav(b, "gameCreation").CompareTo(LNav(a, "gameCreation"));
        });
        var sb = new StringBuilder("[");
        int kept = 0;
        for (int i = 0; i < list.Count; i++) {
          if (LNav(list[i], "gameCreation") < sinceMs) break;   // newest-first: done
          string bucket = ModeBucket(list[i]);
          if (bucket.Length == 0) continue;
          object stats = FirstParticipantStats(list[i]);
          if (stats == null) continue;
          bool win = TwitchEvents.SNavPublic(stats, "win").Equals("True", StringComparison.OrdinalIgnoreCase);
          int bi = BucketIx(bucket);
          if (win) wins[bi]++; else losses[bi]++;
          // The tally counts every game of the day; the list the overlay
          // draws squares from stays capped, because past twenty the form
          // row is a barcode.
          if (kept < 20) {
            if (kept > 0) sb.Append(',');
            sb.Append("{\"m\":\"").Append(bucket).Append("\",\"win\":").Append(win ? "true" : "false").Append('}');
            kept++;
          }
        }
        gamesJson = sb.Append(']').ToString();
        return true;
      } catch { return false; }
    }

    static int BucketIx(string bucket) {
      return bucket == "ranked" ? 0 : bucket == "normals" ? 1 : bucket == "aram" ? 2 : 3;
    }

    // Queue IDs are the precise signal (game modes lie: ARAM and URF both
    // say their own thing, customs say CLASSIC). Buckets rather than raw IDs
    // because "count normals or don't" is the decision a streamer actually
    // makes; nobody wants to maintain a queue-ID list in an OBS URL.
    static string ModeBucket(object game) {
      string mode = TwitchEvents.SNavPublic(game, "gameMode").Trim().ToUpperInvariant();
      string type = TwitchEvents.SNavPublic(game, "gameType").Trim().ToUpperInvariant();
      if (mode == "PRACTICETOOL" || mode == "TUTORIAL" || type == "TUTORIAL_GAME") return "";
      long q = LNav(game, "queueId");
      if (q == 420 || q == 440) return "ranked";
      if (q == 400 || q == 430 || q == 490) return "normals";
      if (q == 450) return "aram";
      return "other";                               // arena, URF, customs, events
    }

    static long LocalMidnightEpochMs() {
      return (long)(DateTime.Today.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds;
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
          // Two consumers can want this loop: the bot's Game stats switch,
          // and a session-tracker overlay that has polled /league-state in
          // the last half minute. Either keeps it alive; with neither, the
          // League client is left entirely alone - the features-page promise.
          bool active = _enabled
                     || DateTime.UtcNow.Ticks < Interlocked.Read(ref _wantedUntilTicks);
          if (!active) {
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
            // 20 games, not 6: the session tracker counts a full day's worth,
            // and a ranked grinder clears 6 well before dinner.
            string hist = LcuGet(port, pw,
              "/lol-match-history/v1/products/lol/current-summoner/matches?begIndex=0&endIndex=20");
            if (hist != null) {
              lastHistory = DateTime.UtcNow;
              string record, lastLine, at; long newest;
              if (ParseHistory(hist, out record, out lastLine, out newest, out at)) {
                bool isNew;
                string todayJson; int[] tw, tl;
                if (!ParseToday(hist, LocalMidnightEpochMs(), out todayJson, out tw, out tl)) {
                  todayJson = "[]"; tw = new int[4]; tl = new int[4];
                }
                lock (_resultLock) {
                  isNew = _newestGameId != 0 && newest != 0 && newest != _newestGameId;
                  _record = record; _lastLine = lastLine;
                  if (newest != 0) _newestGameId = newest;
                  _newestAt = at;
                  _todayJson = todayJson;
                  _todayWinsB = tw; _todayLossesB = tl;
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
          // Rank rides the same pass: refreshed after a game lands (that is
          // when it moves) and every few minutes otherwise, so !rank and the
          // tracker answer from a warm cache.
          if ((DateTime.UtcNow - _rankFetchedUtc).TotalSeconds > 300 || gameJustEnded)
            FetchRank();
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
      sb.Append("\"last\":").Append(TwitchChat.Qs(last)).Append(',');
      string rankLine;
      lock (_resultLock) { rankLine = _rankLine; }
      sb.Append("\"rank\":").Append(TwitchChat.Qs(rankLine));
      sb.Append('}');
      return sb.ToString();
    }

    // ------------------------------------------------------------------- test
    // Canned payloads in the endpoints' real shapes, pushed through the same
    // parsers the live loop uses. The history is deliberately out of
    // chronological order, so a pass also proves the sort; game 103 is the
    // practice tool, so a pass also proves it never counts toward today.
    // Expected: record "W W L W L" (the 5-game record still counts every
    // mode), newest 105, today [ranked W, ranked W, normals W, aram L],
    // rank "Emerald II - 45 LP (Solo/Duo), 210W 198L this season", abs 2245.
    public static string TestParse() {
      string fixture = "{\"games\":{\"games\":["
        + FixGame("104", "1700000400000", "true",  "5",  "1", "9", "420", "CLASSIC")
        + "," + FixGame("101", "1700000100000", "false", "2", "7", "3", "450", "ARAM")
        + "," + FixGame("105", "1700000500000", "true",  "12", "3", "8", "420", "CLASSIC")
        + "," + FixGame("102", "1700000200000", "true",  "8",  "4", "6", "430", "CLASSIC")
        + "," + FixGame("103", "1700000300000", "false", "4",  "6", "2", "0",   "PRACTICETOOL")
        + "]}}";
      string record, lastLine, at; long newest;
      bool ok = ParseHistory(fixture, out record, out lastLine, out newest, out at);
      string todayJson; int[] tw, tl;
      bool tok = ParseToday(fixture, 1700000000000, out todayJson, out tw, out tl);

      string rankFixture = "{\"queueMap\":{\"RANKED_SOLO_5x5\":{\"tier\":\"EMERALD\","
        + "\"division\":\"II\",\"leaguePoints\":45,\"wins\":210,\"losses\":198}}}";
      string tier, div, queue; int lp, wins, losses;
      bool rok = ParseRankedStats(rankFixture, out tier, out div, out lp, out wins, out losses, out queue);
      string rline = rok ? ComposeRankLine(tier, div, lp, wins, losses, queue) : "";

      return "{\"ok\":" + (ok ? "true" : "false")
           + ",\"record\":" + TwitchChat.Qs(record)
           + ",\"last\":" + TwitchChat.Qs(lastLine)
           + ",\"newestGameId\":" + newest
           + ",\"todayOk\":" + (tok ? "true" : "false")
           + ",\"today\":" + todayJson
           + ",\"tallies\":\"ranked " + tw[0] + "W " + tl[0] + "L, normals " + tw[1] + "W " + tl[1]
           + "L, aram " + tw[2] + "W " + tl[2] + "L, other " + tw[3] + "W " + tl[3] + "L\""
           + ",\"rankOk\":" + (rok ? "true" : "false")
           + ",\"rankLine\":" + TwitchChat.Qs(rline)
           + ",\"absLp\":" + AbsoluteLp(tier, div, lp)
           + ",\"expected\":\"record W W L W L, newest 105, today ranked W/ranked W/normals W/aram L, abs 2245\"}";
    }

    const string FixTemplate = "{{\"gameId\":{0},\"gameCreation\":{1},"
      + "\"queueId\":{6},\"gameMode\":\"{7}\",\"gameType\":\"MATCHED_GAME\","
      + "\"participants\":[{{\"stats\":{{\"win\":{2},\"kills\":{3},\"deaths\":{4},\"assists\":{5}}}}}]}}";

    static string FixGame(string id, string created, string win, string k, string d, string a,
                          string queueId, string mode) {
      return string.Format(FixTemplate, id, created, win, k, d, a, queueId, mode);
    }
  }
}
