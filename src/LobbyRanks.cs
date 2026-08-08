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
            SnapshotDraftFrom(port, pw,
              LeagueStats.LcuGet(port, pw, "/lol-champ-select/v1/session"));
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
    // The finished tag, not the raw tier: a rank is looked up once and the
    // string that goes in the line is what gets remembered.
    static readonly Dictionary<string, string> _rankTag = new Dictionary<string, string>();

    static void Forget() {
      lock (_lock) {
        _rankTag.Clear();
        _draftLine = ""; _gameLine = ""; _gameId = 0; _myPuuid = "";
      }
    }

    static string TagOf(string puuid) {
      if (string.IsNullOrEmpty(puuid)) return "";
      lock (_lock) { string s; return _rankTag.TryGetValue(puuid, out s) ? s : ""; }
    }

    // Returns "" only when the client could not be asked at all; an account
    // with no ranked history answers "UR", which is an answer.
    static string FetchTag(int port, string pw, string puuid) {
      string have = TagOf(puuid);
      if (have.Length > 0) return have;
      string rj = LeagueStats.LcuGet(port, pw, "/lol-ranked/v1/ranked-stats/" + puuid);
      if (rj == null) return "";
      string tier, div, queue; int lp, w, l;
      if (!LeagueStats.ParseRankedStats(rj, out tier, out div, out lp, out w, out l, out queue))
        return "";
      string tag = RankTag(tier, div, lp);
      lock (_lock) _rankTag[puuid] = tag;
      return tag;
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

    // Emerald III -> "E3". The tier letter always leads, so Iron I is I1 and
    // never collides with a division numeral.
    //
    // Master and above carry LP instead: "M342", "GM721", "C1204". Apex is one
    // shared pool with no divisions, so the letter on its own says almost
    // nothing - M0 and M900 are "just promoted" and "one bad night from
    // Challenger", and in a high-elo lobby every seat would otherwise read the
    // same "M". LP is the actual ladder position up there, which is exactly
    // what the rest of the line is for.
    //
    // The division field is filled in regardless - a numeral when ranked, the
    // string "NA" when not - and what the client puts there for apex could not
    // be checked from a Gold account. It does not matter: apex is decided on
    // the tier before the division is read, so the tag is "M342" whether the
    // client says I, NA or nothing. LP replaces that numeral rather than
    // joining it, which is also why nothing here can be mistaken for one.
    //
    // Below apex LP stays off on purpose. The division already places you
    // inside the tier, and ten LP values would push a line built to be taken
    // in at a glance past the point where that works.
    static bool IsApex(string tier) {
      return tier.Equals("Master", StringComparison.OrdinalIgnoreCase)
          || tier.Equals("Grandmaster", StringComparison.OrdinalIgnoreCase)
          || tier.Equals("Challenger", StringComparison.OrdinalIgnoreCase);
    }

    internal static string RankTag(string tier, string div, int lp) {
      tier = (tier ?? "").Trim();
      if (tier.Length == 0 || tier.Equals("Unranked", StringComparison.OrdinalIgnoreCase))
        return "UR";
      string letter =
          tier.Equals("Grandmaster", StringComparison.OrdinalIgnoreCase) ? "GM"
        : tier.Equals("Challenger", StringComparison.OrdinalIgnoreCase) ? "C"
        : tier.Substring(0, 1).ToUpperInvariant();
      if (IsApex(tier)) return letter + (lp > 0 ? lp : 0);
      string num = div == "I" ? "1" : div == "II" ? "2" : div == "III" ? "3" : div == "IV" ? "4" : "";
      return letter + num;
    }

    class Seat { public string Role = ""; public string Tag = "?"; }

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
      foreach (var s in seats) bits.Add(s.Tag);
      return string.Join(" ", bits.ToArray());
    }

    // The two payloads name the same idea differently: the in-game team
    // arrays say selectedPosition, champ select says assignedPosition.
    static string PositionOf(object p) {
      string pos = TwitchEvents.SNavPublic(p, "selectedPosition");
      return pos.Length > 0 ? pos : TwitchEvents.SNavPublic(p, "assignedPosition");
    }

    // Hidden is not the same as unknown, and the line should not pretend it
    // is. "?" means the rank was asked for and the answer did not come back.
    // "_" means the client never said who the player was, so there was nobody
    // to ask about - which in ranked champ select is every enemy, deliberately,
    // right up until the match starts.
    const string HiddenTag = "_";

    // The client blanks a withheld identity rather than dropping the seat: an
    // empty puuid, or the all-zero one. Both mean the same thing here.
    static bool IsAnonymous(string puuid) {
      if (string.IsNullOrEmpty(puuid)) return true;
      foreach (char c in puuid)
        if (c != '0' && c != '-') return false;
      return true;
    }

    static string SideLine(int port, string pw, IEnumerable<object> team) {
      var seats = new List<Seat>();
      foreach (var p in team) {
        string puuid = TwitchEvents.SNavPublic(p, "puuid");
        // An anonymised seat is still a seat. Skipping it printed a five-man
        // team as four - the exact failure the "?" fallback below exists to
        // prevent - and it did it silently, so a hidden team produced no line
        // at all rather than a line saying it was hidden.
        if (IsAnonymous(puuid)) {
          seats.Add(new Seat { Role = RoleTag(PositionOf(p)), Tag = HiddenTag });
          continue;
        }
        string tag = FetchTag(port, pw, puuid);
        // A failed lookup must not delete the player either: an unknown rank
        // is an incomplete answer, a missing player is a wrong one.
        seats.Add(new Seat { Role = RoleTag(PositionOf(p)), Tag = tag.Length == 0 ? "?" : tag });
      }
      return Format(seats);
    }

    // ------------------------------------------------------------ champ select
    // Ranked hides the enemy team behind obfuscated puuids during the draft,
    // so their ranks cannot be looked up. They are printed as hidden rather
    // than left out: "five enemies, ranks not readable yet" is an answer, and
    // it is the one that stops chat wondering why only half a lobby showed up.
    // The labels match the in-game line, so the same row of seats is in the
    // same place all the way through - you watch the underscores become ranks
    // when the match starts.
    //
    // Carries its own labels, like _gameLine, because which ones apply is
    // decided here: a queue that does not hide the enemy gets both sides
    // named, and a payload with no enemy array at all gets "My team".
    static string _draftLine = "";

    static void SnapshotDraft(int port, string pw, object[] mine, object[] theirs) {
      if (mine == null || mine.Length == 0) return;
      string us = SideLine(port, pw, mine);
      if (us.Length == 0) return;
      string them = (theirs == null || theirs.Length == 0) ? "" : SideLine(port, pw, theirs);
      lock (_lock) {
        _draftLine = them.Length > 0 ? "Us: " + us + "  |  Them: " + them
                                     : "My team: " + us;
      }
    }

    // Both sides of the champ-select payload, in one place - the poll loop and
    // the command read it identically.
    static void SnapshotDraftFrom(int port, string pw, string session) {
      if (session == null) return;
      object root = TwitchEvents.NavPublic(session);
      SnapshotDraft(port, pw, Nav(root, "myTeam") as object[], Nav(root, "theirTeam") as object[]);
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
          SnapshotDraftFrom(port, pw,
            LeagueStats.LcuGet(port, pw, "/lol-champ-select/v1/session"));
          string draft;
          lock (_lock) draft = _draftLine;
          return draft.Length > 0 ? draft
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

    // ------------------------------------------------------------------ test
    // Every tag shape through the real formatter. The apex ones are the point:
    // the streamer would have to reach Master to see them any other way, and
    // "it looked right in a Gold lobby" proves nothing about the branch that
    // only runs for someone else's rank. M0 is in there because a freshly
    // promoted Master is the one apex value that could be mistaken for a
    // division numeral, and UR because "" means unranked, not unreadable.
    internal static string TagFixtures() {
      return string.Join(" ", new[] {
        RankTag("Emerald", "III", 42),
        RankTag("Gold", "I", 88),
        RankTag("Iron", "I", 7),
        RankTag("Master", "I", 342),
        RankTag("Grandmaster", "I", 721),
        RankTag("Challenger", "I", 1204),
        RankTag("Master", "I", 0),
        RankTag("", "", 0)
      });
    }

    // Which identities count as withheld. The all-zero puuid is the one that
    // matters and the one that cannot be checked by playing: it only appears
    // on the enemy side of a ranked draft.
    internal static string HiddenFixtures() {
      return string.Join(" ", new[] {
        IsAnonymous(null) ? HiddenTag : "x",
        IsAnonymous("") ? HiddenTag : "x",
        IsAnonymous("00000000-0000-0000-0000-000000000000") ? HiddenTag : "x",
        IsAnonymous("7f3c1a52-9b0e-4d21-8a44-1c9de0b57a63") ? HiddenTag : "x"
      });
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
