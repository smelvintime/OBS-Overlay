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
    internal static bool FindLockfile(out int port, out string password) {
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

    // String for JSON, bytes for anything else - one transport. LobbyRanks
    // shares these; the LCU rules (per-connection trust, byte-domain
    // de-chunking) are subtle enough that a second copy would drift.
    internal static string LcuGet(int port, string password, string path) {
      byte[] body = LcuGetRaw(port, password, path);
      return body == null ? null : Encoding.UTF8.GetString(body);
    }

    internal static byte[] LcuGetRaw(int port, string password, string path) {
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

            // Accept must be */*: the JSON endpoints don't care, but the
            // WAD-packaged assets (spell icons, profile icons) stall for
            // seconds when asked for application/json before answering.
            string reqText = "GET " + path + " HTTP/1.1\r\n"
              + "Host: 127.0.0.1:" + port + "\r\n"
              + "Authorization: Basic " + Convert.ToBase64String(
                  Encoding.ASCII.GetBytes("riot:" + password)) + "\r\n"
              + "Accept: */*\r\n"
              + "User-Agent: NowPlayingOverlay\r\n"
              + "Connection: close\r\n\r\n";
            var reqBytes = Encoding.ASCII.GetBytes(reqText);
            ssl.Write(reqBytes, 0, reqBytes.Length);
            ssl.Flush();

            // Read until the response is COMPLETE, not until the server hangs
            // up: the asset service ignores Connection: close and holds the
            // socket open after sending everything, which used to cost a full
            // read-timeout per icon. Content-Length or the chunked terminator
            // says when done; close remains the fallback for anything else.
            var ms = new MemoryStream();
            var buf = new byte[16384];
            int hdrAt = -1; long want = -1; bool isChunked = false;
            while (true) {
              int n;
              try { n = ssl.Read(buf, 0, buf.Length); } catch (IOException) { break; }
              if (n <= 0) break;
              ms.Write(buf, 0, n);
              byte[] soFar = ms.ToArray();
              if (hdrAt < 0) {
                hdrAt = IndexOfHeaderEnd(soFar);
                if (hdrAt >= 0) {
                  string h = Encoding.ASCII.GetString(soFar, 0, hdrAt);
                  isChunked = h.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;
                  long cl = ContentLengthOf(h);
                  if (cl >= 0) want = hdrAt + 4 + cl;
                }
              }
              if (hdrAt >= 0) {
                if (want >= 0 && ms.Length >= want) break;
                if (isChunked && EndsWithFinalChunk(soFar)) break;
              }
            }

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
            return body;
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

    static long ContentLengthOf(string head) {
      foreach (var line in head.Split('\n')) {
        int c = line.IndexOf(':');
        if (c <= 0) continue;
        if (!line.Substring(0, c).Trim().Equals("content-length", StringComparison.OrdinalIgnoreCase)) continue;
        long v;
        if (long.TryParse(line.Substring(c + 1).Trim(), out v)) return v;
      }
      return -1;
    }

    // The last thing a chunked body sends is a zero-size chunk: CRLF "0" CRLF
    // CRLF. Trailers would sit between the last two CRLFs, but the LCU sends
    // none, and if it ever did the close-fallback still finishes the read.
    static bool EndsWithFinalChunk(byte[] b) {
      int n = b.Length;
      return n >= 7
          && b[n - 7] == 13 && b[n - 6] == 10 && b[n - 5] == (byte)'0'
          && b[n - 4] == 13 && b[n - 3] == 10 && b[n - 2] == 13 && b[n - 1] == 10;
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
          // Ranked only. The record is the grind: an ARAM warm-up or a normal
          // with friends polluting "past 5" is how a 3-0 ranked evening reads
          // as 3-2 in chat. Everything else already has its own home - the
          // session tracker counts whatever modes its switches say.
          if (ModeBucket(list[i]) != "ranked") continue;
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

    // The end-of-game screen's own data, which is what makes the announcement
    // fast. Match history is the authority on the season, but Riot's copy of
    // it lags the final whistle by up to a minute, and for that whole minute
    // the client itself has known the result perfectly well - it is drawing
    // it on the post-game screen. This payload is that screen's source, and
    // it holds every piece the line needs.
    //
    // Shapes differ from the history payload and are not guessed: captured
    // live from a finished ranked game. WIN is 1/0 here where history uses a
    // JSON boolean; the KDA keys are CHAMPIONS_KILLED / NUM_DEATHS /
    // ASSISTS; and there is no queueId at all, only the queue's name.
    internal static bool ParseEog(string json, out long gameId, out bool ranked,
                                  out bool win, out string lastLine) {
      gameId = 0; ranked = false; win = false; lastLine = "";
      try {
        object root = TwitchEvents.NavPublic(json);
        gameId = LNav(root, "gameId");
        if (gameId == 0) return false;
        // RANKED_SOLO_5x5 and RANKED_FLEX_SR are queues 420 and 440 - exactly
        // the two ModeBucket counts as ranked, named instead of numbered.
        string q = TwitchEvents.SNavPublic(root, "queueType").Trim().ToUpperInvariant();
        ranked = q.StartsWith("RANKED_SOLO", StringComparison.Ordinal)
              || q.StartsWith("RANKED_FLEX", StringComparison.Ordinal);

        object stats = Nav(root, "localPlayer", "stats");
        if (stats == null) return false;
        if (Nav(stats, "WIN") != null) {
          win = LNav(stats, "WIN") == 1;
        } else {
          // No WIN key: read it off the teams instead. Announcing a defeat as
          // a victory is the single most embarrassing thing this line can do,
          // and unlike the record it is never corrected afterwards - history
          // does not re-announce - so it is worth a second way to know.
          long mine = LNav(Nav(root, "localPlayer"), "teamId");
          var teams = Nav(root, "teams") as object[];
          bool found = false;
          if (teams != null) {
            foreach (var t in teams) {
              if (LNav(t, "teamId") != mine) continue;
              win = TwitchEvents.SNavPublic(t, "isWinningTeam")
                      .Equals("True", StringComparison.OrdinalIgnoreCase);
              found = true;
              break;
            }
          }
          if (!found) return false;   // neither source knows: say nothing
        }

        long k = LNav(stats, "CHAMPIONS_KILLED");
        long d = LNav(stats, "NUM_DEATHS");
        long a = LNav(stats, "ASSISTS");
        lastLine = (win ? "Victory" : "Defeat") + " (" + k + "/" + d + "/" + a + ")";
        return true;
      } catch { gameId = 0; return false; }
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

    // -------------------------------------------------------- the fast lane
    // Which game the end-of-game screen already announced, so the history
    // poll can tell "history has caught up" from "history is still behind".
    static long _eogAnnouncedId;

    static bool TryEogAnnounce(int port, string pw) {
      string eog = LcuGet(port, pw, "/lol-end-of-game/v1/eog-stats-block");
      if (eog == null) return false;      // 404 between games: the normal answer
      long gid; bool ranked, win; string last;
      if (!ParseEog(eog, out gid, out ranked, out win, out last)) return false;
      // A non-ranked game is not announced (the line is ranked-only) and must
      // not touch _newestGameId either: that field tracks the newest RANKED
      // game, because history never reports any other kind as newest. Aiming
      // it at an ARAM would make the next history poll see an unfamiliar id
      // and re-announce the ranked game before it.
      if (!ranked) return false;
      lock (_resultLock) {
        // Equal means already announced; smaller means the screen is still
        // showing the PREVIOUS game because this one's has not replaced it
        // yet. Both are "say nothing and look again in two seconds", and
        // both would otherwise put a game chat has already heard about - or
        // one older than that - back on screen as news.
        if (gid <= _newestGameId) return false;
        _newestGameId = gid;
        _eogAnnouncedId = gid;
        _lastLine = last;
        // The record reads newest-first, so this result goes on the front of
        // whatever the last history read established. History replaces the
        // whole string within the minute - starting with the same letter
        // this line just claimed, because both read the same game.
        var letters = new List<string>();
        letters.Add(win ? "W" : "L");
        if (_record.Length > 0) letters.AddRange(_record.Split(' '));
        while (letters.Count > 5) letters.RemoveAt(letters.Count - 1);
        _record = string.Join(" ", letters.ToArray());
      }
      Interlocked.Increment(ref _resultSeq);
      AppLog.Write("league: announced from the end-of-game screen (game " + gid + ")");
      TwitchChat.OnGameEnded(ChatLine());
      return true;
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
      int eogTries = 0;          // >0 = still hoping the end-of-game screen appears
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
            phase = ""; freshPolls = 0; eogTries = 0;
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
            // History lags the end screen by anywhere up to a minute, and the
            // announcement is only as fast as the poll that finds it. 45
            // attempts at the 2s chase cadence below is the same ~90s of
            // patience as before, checked five times as often, so the line
            // lands within a couple of seconds of the result existing
            // instead of within ten.
            freshPolls = 45;
            // ...but the client knows the result NOW, so try that first. The
            // block does not appear at the exact instant the phase turns
            // (it arrives with the post-game screen a moment later), hence a
            // window of tries rather than one shot: ~30s at the chase
            // cadence, after which the screen was plainly skipped and
            // history is the only road left.
            eogTries = 15;
          }
          if (eogTries > 0) {
            eogTries--;
            // The chase is deliberately NOT shortened on success. The line is
            // out, but the day's tallies and the true five-game record still
            // only exist in history - and "caughtUp" below ends the chase the
            // moment they land, which is sooner than any timer I could pick.
            if (TryEogAnnounce(port, pw)) eogTries = 0;
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
                bool caughtUp, behind;
                lock (_resultLock) {
                  // Is this history payload OLDER than what is already known?
                  // It can be, now that the end-of-game screen gets there
                  // first: for up to a minute afterwards every history read
                  // still describes the game before last. Game ids climb, so
                  // "smaller than what we hold" is exactly "out of date".
                  //
                  // This is not hypothetical - it shipped broken for one
                  // evening. The stale payload was written straight over the
                  // fresh result AND passed the old "different id" test for
                  // new-ness, so two seconds after the correct announcement a
                  // second one went out naming the game before it, and the
                  // newest-game pointer walked backwards.
                  behind = newest != 0 && newest < _newestGameId;
                  // Strictly newer, not merely different: "different" reads a
                  // backwards step as news.
                  isNew = !behind && _newestGameId != 0 && newest > _newestGameId;
                  // The game the end-of-game screen announced has now landed
                  // in history: the tallies and the true record are in hand,
                  // so there is nothing left to chase.
                  caughtUp = newest != 0 && newest == _eogAnnouncedId;
                  // Nothing from a stale payload is taken - not the record,
                  // not the last line, not the tallies. They are one snapshot
                  // of one moment, and that moment has passed.
                  if (!behind) {
                    _record = record; _lastLine = lastLine;
                    if (newest != 0) _newestGameId = newest;
                    _newestAt = at;
                    _todayJson = todayJson;
                    _todayWinsB = tw; _todayLossesB = tl;
                  }
                }
                _status = "live"; _detail = "";
                if (caughtUp) freshPolls = 0;
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
        // Three speeds, because the cost of a poll and the value of a poll
        // are not constant. Chasing a just-finished result is worth 2s;
        // watching a game that is going to end soon is worth 4s so the
        // transition is not sat on; idling between games is worth 10s.
        Thread.Sleep(freshPolls > 0 ? 2000 : (phase == "InProgress" ? 4000 : 10000));
      }
    }

    // ------------------------------------------------------------------ output
    // The one sentence both the announcer and !record say. "ranked" is in the
    // words on purpose: the line filters to ranked games, and a count that
    // quietly ignores the ARAM someone just watched needs to say what it is.
    public static string ChatLine() {
      lock (_resultLock) {
        if (_record.Length == 0) return "";
        return (_lastLine.Length > 0 ? "Last game: " + _lastLine + " - " : "")
             + "past " + _record.Split(' ').Length + " ranked: " + _record;
      }
    }

    // What !record answers when there is nothing to say yet.
    public static string CommandLine() {
      string line = ChatLine();
      if (line.Length > 0) return line;
      if (!_enabled) return "Game stats are switched off.";
      return _status == "no-client"
        ? "No ranked games tracked yet - the League client isn't running."
        : "No ranked games tracked yet this session.";
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
    // practice tool and 101/102 are aram/normals, so a pass also proves the
    // record keeps to ranked while today still counts every real mode.
    // Expected: record "W W" (ranked only: 105 and 104), newest 105, today
    // [ranked W, ranked W, normals W, aram L], rank "Emerald II - 45 LP
    // (Solo/Duo), 210W 198L this season", abs 2245.
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

      // The end-of-game block, in the shape captured from a real finished
      // ranked game. Three cases worth holding still: the ordinary ranked
      // win, an ARAM (which must report NOT ranked, so the fast lane declines
      // it and leaves the ranked bookkeeping alone), and a payload with no
      // WIN stat, which must still get the result right off the teams.
      long eg1; bool eRanked1, eWin1; string eLast1;
      bool eok = ParseEog(EogFix("5614070805", "RANKED_SOLO_5x5", "\"WIN\":1,", "11", "1", "5", "100", "100"),
                          out eg1, out eRanked1, out eWin1, out eLast1);
      long eg2; bool eRanked2, eWin2; string eLast2;
      ParseEog(EogFix("5614070806", "ARAM_UNRANKED_5x5", "\"WIN\":0,", "3", "9", "12", "200", "100"),
               out eg2, out eRanked2, out eWin2, out eLast2);
      long eg3; bool eRanked3, eWin3; string eLast3;
      ParseEog(EogFix("5614070807", "RANKED_FLEX_SR", "", "7", "2", "4", "200", "200"),
               out eg3, out eRanked3, out eWin3, out eLast3);

      return "{\"ok\":" + (ok ? "true" : "false")
           + ",\"record\":" + TwitchChat.Qs(record)
           + ",\"last\":" + TwitchChat.Qs(lastLine)
           + ",\"newestGameId\":" + newest
           + ",\"eogOk\":" + (eok ? "true" : "false")
           + ",\"eogId\":" + eg1
           + ",\"eogLast\":" + TwitchChat.Qs(eLast1)
           + ",\"eogRanked\":" + (eRanked1 ? "true" : "false")
           + ",\"eogAramIsRanked\":" + (eRanked2 ? "true" : "false")
           + ",\"eogNoWinKeyWon\":" + (eWin3 ? "true" : "false")
           + ",\"todayOk\":" + (tok ? "true" : "false")
           + ",\"today\":" + todayJson
           + ",\"tallies\":\"ranked " + tw[0] + "W " + tl[0] + "L, normals " + tw[1] + "W " + tl[1]
           + "L, aram " + tw[2] + "W " + tl[2] + "L, other " + tw[3] + "W " + tl[3] + "L\""
           + ",\"rankOk\":" + (rok ? "true" : "false")
           + ",\"rankLine\":" + TwitchChat.Qs(rline)
           + ",\"absLp\":" + AbsoluteLp(tier, div, lp)
           + ",\"expected\":\"record W W (ranked only), newest 105, today ranked W/ranked W/normals W/aram L, abs 2245"
           + "; eog Victory (11/1/5) ranked, aram not ranked, no-WIN-key won\"}";
    }

    // The end-of-game payload, reduced to the fields ParseEog reads.
    // winningTeam is given separately from the WIN stat on purpose: that is
    // what lets a fixture leave WIN out and check the teams fallback.
    static string EogFix(string id, string queue, string winStat, string k, string d, string a,
                         string myTeam, string winningTeam) {
      return "{\"gameId\":" + id + ",\"queueType\":\"" + queue + "\","
           + "\"localPlayer\":{\"teamId\":" + myTeam + ",\"stats\":{" + winStat
           + "\"CHAMPIONS_KILLED\":" + k + ",\"NUM_DEATHS\":" + d + ",\"ASSISTS\":" + a + "}},"
           + "\"teams\":[{\"teamId\":100,\"isWinningTeam\":" + (winningTeam == "100" ? "true" : "false") + "},"
           + "{\"teamId\":200,\"isWinningTeam\":" + (winningTeam == "200" ? "true" : "false") + "}]}";
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
