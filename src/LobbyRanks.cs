// Who you are playing with, and what rank they are - the data behind !ranks.
//
// This file used to be DraftWatch, a champ-select reader feeding an on-stream
// draft board and a desktop popup over the client. Both are gone: everything
// they drew, the League client already showed the streamer, and a viewer
// watching a draft board learns nothing they could not read off the stream
// itself. What survived is the one part that was never visible anywhere -
// the ranks of the nine other people in the game - because the client does
// not put those on screen at any point.
//
// STRICTLY READ-ONLY. The client's local API can also pick, ban and trade;
// nothing here issues anything but GETs, and it should stay that way.
//
// Everything is best-effort in the TwitchEvents mould: a missing client or a
// moved endpoint leaves !ranks saying so, never a crash.

using System;
using System.Collections.Generic;
using System.Threading;

namespace NowPlaying {

  static class LobbyRanks {

    // ------------------------------------------------------------------ state
    static readonly object _lock = new object();
    static volatile string _phase = "None";

    // Demand: !ranks stamps this when it is asked, and the loop stays awake
    // for 30 seconds afterwards so a second question is instant. Nobody
    // asking means the League client is left completely alone.
    static long _wantedUntilTicks;

    public static void NoteInterest() {
      Interlocked.Exchange(ref _wantedUntilTicks, DateTime.UtcNow.AddSeconds(30).Ticks);
    }

    // "In a game" covers the whole tail of the match, not just the part with
    // a Nexus: chat asks who you played against while the post-game screen is
    // still up, and the client keeps that lobby's data right through it.
    static bool InGame() {
      string p = _phase;
      return p == "InProgress" || p == "WaitingForStats"
          || p == "PreEndOfGame" || p == "EndOfGame" || p == "Reconnect";
    }

    // ------------------------------------------------------------------- loop
    public static void Start() {
      var t = new Thread(Loop);
      t.IsBackground = true;
      t.Start();
    }

    static void Loop() {
      while (true) {
        try {
          if (DateTime.UtcNow.Ticks >= Interlocked.Read(ref _wantedUntilTicks)) {
            Thread.Sleep(2000);
            continue;
          }

          int port; string pw;
          if (!LeagueStats.FindLockfile(out port, out pw)) {
            _phase = "None";
            Forget();
            Thread.Sleep(5000);
            continue;
          }

          string ph = LeagueStats.LcuGet(port, pw, "/lol-gameflow/v1/gameflow-phase");
          if (ph == null) { Thread.Sleep(5000); continue; }
          _phase = ph.Trim().Trim('"');

          if (_phase == "ChampSelect") {
            string session = LeagueStats.LcuGet(port, pw, "/lol-champ-select/v1/session");
            if (session != null)
              SnapshotDraft(port, pw, Nav(TwitchEvents.NavPublic(session), "myTeam") as object[]);
          } else if (InGame()) {
            EnsureGameRoster(port, pw);
          }
          Thread.Sleep(3000);
        } catch {
          Thread.Sleep(5000);
        }
      }
    }

    // ------------------------------------------------------- per-player facts
    // /lol-ranked/v1/ranked-stats/<puuid> is a real per-player lookup, not a
    // dressed-up read of your own row - verified by feeding it a fabricated
    // puuid, which comes back NONE while a live one comes back with a tier.
    // Looked up once per player and remembered until the client goes away.
    static readonly Dictionary<string, string> _rankShort = new Dictionary<string, string>();

    static void Forget() {
      lock (_lock) {
        _rankShort.Clear();
        _draftLine = ""; _gameLine = ""; _gameId = 0; _myPuuid = "";
      }
    }

    static string RankShortOf(string puuid) {
      if (string.IsNullOrEmpty(puuid)) return "";
      lock (_lock) { string s; return _rankShort.TryGetValue(puuid, out s) ? s : ""; }
    }

    // Returns "" only when the client could not be asked at all; an account
    // with no ranked history answers "Unranked", which is an answer.
    static string FetchRank(int port, string pw, string puuid) {
      string have = RankShortOf(puuid);
      if (have.Length > 0) return have;
      string rj = LeagueStats.LcuGet(port, pw, "/lol-ranked/v1/ranked-stats/" + puuid);
      if (rj == null) return "";
      string tier, div, queue; int lp, w, l;
      if (!LeagueStats.ParseRankedStats(rj, out tier, out div, out lp, out w, out l, out queue))
        return "";
      string rank = tier.Length > 0 ? tier + (div.Length > 0 ? " " + div : "") : "Unranked";
      lock (_lock) _rankShort[puuid] = rank;
      return rank;
    }

    // ------------------------------------------------------- formatting
    // Lane order, ranks only. The position in the line IS the lane - top,
    // jungle, mid, ADC, support - so two rows of five line up and a viewer
    // reads the matchup straight down instead of parsing names and tiers.
    static readonly string[] RoleOrder = { "TOP", "JG", "MID", "ADC", "SUP" };

    static string RoleTag(string pos) {
      switch ((pos ?? "").Trim().ToUpperInvariant()) {
        case "TOP": return "TOP";
        case "JUNGLE": return "JG";
        case "MIDDLE": case "MID": return "MID";
        case "BOTTOM": case "BOT": return "ADC";
        case "UTILITY": case "SUPPORT": return "SUP";
        default: return "";
      }
    }

    // "Emerald III" -> "E3". The tier letter always leads, so Iron I is I1
    // and never collides with a division numeral.
    static string RankTag(string rank) {
      if (rank.Length == 0 || rank == "?") return "?";
      if (rank.StartsWith("Unranked", StringComparison.OrdinalIgnoreCase)) return "UR";
      string[] parts = rank.Split(' ');
      string tier = parts[0];
      string letter =
          tier.Equals("Grandmaster", StringComparison.OrdinalIgnoreCase) ? "GM"
        : tier.Equals("Challenger", StringComparison.OrdinalIgnoreCase) ? "C"
        : tier.Substring(0, 1).ToUpperInvariant();
      string div = parts.Length > 1 ? parts[1] : "";
      string num = div == "I" ? "1" : div == "II" ? "2" : div == "III" ? "3" : div == "IV" ? "4" : "";
      return letter + num;
    }

    class Seat { public string Role = ""; public string Rank = "?"; }

    // A player the client gave no position for takes whichever lane is left
    // over. In a five-man team with exactly one hole that is deduction, not a
    // guess - and it matters, because the teammate recovered from the
    // incomplete team list below arrives carrying no role at all.
    static string Format(List<Seat> seats) {
      var missing = new List<string>(RoleOrder);
      foreach (var s in seats) missing.Remove(s.Role);
      foreach (var s in seats) {
        if (s.Role.Length != 0) continue;
        if (missing.Count == 1) { s.Role = missing[0]; missing.Clear(); }
      }
      seats.Sort(delegate(Seat a, Seat b) {
        int ia = Array.IndexOf(RoleOrder, a.Role), ib = Array.IndexOf(RoleOrder, b.Role);
        if (ia < 0) ia = 99;
        if (ib < 0) ib = 99;
        return ia.CompareTo(ib);
      });
      var bits = new List<string>();
      foreach (var s in seats) bits.Add(RankTag(s.Rank));
      return string.Join(" ", bits.ToArray());
    }

    // The two payloads name the same idea differently: the in-game team
    // arrays say selectedPosition, champ select says assignedPosition.
    static string PositionOf(object p) {
      string pos = TwitchEvents.SNavPublic(p, "selectedPosition");
      return pos.Length > 0 ? pos : TwitchEvents.SNavPublic(p, "assignedPosition");
    }

    static string SideLine(int port, string pw, IEnumerable<object> team) {
      var seats = new List<Seat>();
      foreach (var p in team) {
        string puuid = TwitchEvents.SNavPublic(p, "puuid");
        if (puuid.Length == 0) continue;
        string rank = FetchRank(port, pw, puuid);
        // A failed lookup must not delete the player: a five-man team that
        // prints as four is a wrong answer, where an unknown rank is merely
        // an incomplete one.
        seats.Add(new Seat { Role = RoleTag(PositionOf(p)), Rank = rank.Length == 0 ? "?" : rank });
      }
      return Format(seats);
    }

    // ------------------------------------------------------------ champ select
    // Allies only, and not by choice: ranked hides the enemy team behind
    // obfuscated puuids during the draft, so there is nothing to look up.
    static string _draftLine = "";

    static void SnapshotDraft(int port, string pw, object[] team) {
      if (team == null || team.Length == 0) return;
      string line = SideLine(port, pw, team);
      if (line.Length > 0) lock (_lock) _draftLine = line;
    }

    // ---------------------------------------------------------------- in game
    // Once the match starts the blackout lifts: the gameflow session carries
    // teamOne and teamTwo with real puuids for all ten players, so both sides
    // can be ranked. Verified live in a ranked solo game.
    //
    // Built once per gameId - nobody's rank moves mid-match, and ten lookups
    // is not something to repeat every few seconds.
    static string _gameLine = "";
    static long _gameId;
    static string _myPuuid = "";

    static void EnsureGameRoster(int port, string pw) {
      string sj = LeagueStats.LcuGet(port, pw, "/lol-gameflow/v1/session");
      if (sj == null) return;
      object data = Nav(TwitchEvents.NavPublic(sj), "gameData");
      long gid = LNum(data, "gameId");
      if (gid == 0) return;
      lock (_lock) { if (gid == _gameId && _gameLine.Length > 0) return; }

      if (_myPuuid.Length == 0) {
        string me = LeagueStats.LcuGet(port, pw, "/lol-summoner/v1/current-summoner");
        if (me != null) _myPuuid = TwitchEvents.SNavPublic(TwitchEvents.NavPublic(me), "puuid");
      }

      var one = Nav(data, "teamOne") as object[];
      var two = Nav(data, "teamTwo") as object[];
      if (one == null || two == null) return;
      bool mineIsOne = _myPuuid.Length == 0 || !HasPuuid(two, _myPuuid);
      object[] mine = mineIsOne ? one : two;
      object[] theirs = mineIsOne ? two : one;

      // teamOne/teamTwo are not always complete - observed live in a real
      // ranked game where teamOne listed four of five while
      // playerChampionSelections listed all ten. So the ally side is the
      // union of the two, keyed by puuid.
      var enemyIds = new HashSet<string>();
      foreach (var p in theirs) {
        string id = TwitchEvents.SNavPublic(p, "puuid");
        if (id.Length > 0) enemyIds.Add(id);
      }
      var allies = new List<object>();
      var seen = new HashSet<string>();
      foreach (var p in mine) {
        string id = TwitchEvents.SNavPublic(p, "puuid");
        if (id.Length > 0 && seen.Add(id)) allies.Add(p);
      }
      // That list covers BOTH teams, so "not an enemy" only means "an ally"
      // when the enemy side is known complete. Filing an enemy under Us would
      // be a confidently wrong answer, and a short list beats a wrong one.
      var picks = Nav(data, "playerChampionSelections") as object[];
      if (picks != null && enemyIds.Count >= 5) {
        foreach (var p in picks) {
          if (allies.Count >= 5) break;
          string id = TwitchEvents.SNavPublic(p, "puuid");
          if (id.Length > 0 && !enemyIds.Contains(id) && seen.Add(id)) allies.Add(p);
        }
      }

      string us = SideLine(port, pw, allies);
      string them = SideLine(port, pw, theirs);
      if (us.Length == 0 && them.Length == 0) return;
      lock (_lock) {
        _gameId = gid;
        _gameLine = "Us: " + us + "  |  Them: " + them;
        // The draft that fed this game is consumed by it: the in-game line
        // supersedes it, and left alive it could surface later as if it
        // described a champ select that is long over.
        _draftLine = "";
      }
    }

    static bool HasPuuid(object[] team, string puuid) {
      foreach (var p in team)
        if (TwitchEvents.SNavPublic(p, "puuid") == puuid) return true;
      return false;
    }

    // ------------------------------------------------------------------ !ranks
    // Every ask starts from a fresh phase read. This used to trust the cached
    // phase and lines first, which could serve the PREVIOUS game's ranks as
    // the current game's: the loop only polls for 30s after an ask, so a
    // question asked once in game one and next in game two found _phase still
    // frozen at InProgress and _gameLine still holding game one. One gameflow
    // GET per ask is cheap (the command has a 30s cooldown), and the caches
    // below still make repeats fast - they just can no longer make them wrong.
    public static string RanksLine() {
      NoteInterest();
      try {
        int port; string pw;
        if (!LeagueStats.FindLockfile(out port, out pw))
          return "The League client isn't running on the stream PC.";
        string ph = LeagueStats.LcuGet(port, pw, "/lol-gameflow/v1/gameflow-phase");
        if (ph == null) return "Can't reach the League client right now.";
        _phase = ph.Trim().Trim('"');

        if (InGame()) {
          // Early-outs on a matching gameId, so repeats in the same match
          // cost one gameflow read, not ten rank lookups.
          EnsureGameRoster(port, pw);
          string built;
          lock (_lock) built = _gameLine;
          return built.Length > 0 ? built : "Reading the lobby - ask again in a moment.";
        }

        if (_phase == "ChampSelect") {
          string session = LeagueStats.LcuGet(port, pw, "/lol-champ-select/v1/session");
          var team = session == null ? null : Nav(TwitchEvents.NavPublic(session), "myTeam") as object[];
          if (team != null) SnapshotDraft(port, pw, team);
          string draft;
          lock (_lock) draft = _draftLine;
          return draft.Length > 0 ? "My team: " + draft
               : "No ranks to read yet - the draft just started.";
        }

        // Between games a leftover draft line is stale twice over - the game
        // it fed has finished - so it dies here rather than leaking into the
        // next champ select's "just started" answer.
        string game;
        lock (_lock) { _draftLine = ""; game = _gameLine; }
        // The finished lobby is still worth an answer ("who did you just play
        // against?" outlives the post-game screen), honestly labelled as past.
        if (game.Length > 0) return "Last game - " + game;
        return "Not in a game right now - ranks show up in champ select.";
      } catch {
        return "Can't read the lobby right now.";
      }
    }

    // ------------------------------------------------------------------ json
    // Object-walking helpers: NavPublic parses a string, these walk what it
    // returned.
    static object Nav(object o, params string[] path) {
      foreach (var key in path) {
        var d = o as Dictionary<string, object>;
        if (d == null) return null;
        if (!d.TryGetValue(key, out o)) return null;
      }
      return o;
    }
    static long LNum(object o, string key) {
      var v = Nav(o, key);
      if (v == null) return 0;
      try { return Convert.ToInt64(v); } catch { return 0; }
    }
  }
}
