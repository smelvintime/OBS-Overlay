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
    static readonly object _lock = new object(), _refreshLock = new object();
    static volatile string _phase = "None";

    // Demand: !ranks stamps this when it is asked, and the loop stays awake
    // for 30 seconds afterwards so a second question is instant. Nobody
    // asking means the League client is left completely alone.
    static long _wantedUntilTicks;
    internal static bool Wanted { get { return DateTime.UtcNow.Ticks < Interlocked.Read(ref _wantedUntilTicks); } }

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
          if (!Program.LeagueFeatureOn || !Wanted) {
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
        _nameOf.Clear();
        _draftLine = ""; _gameLine = ""; _gameId = 0; _myPuuid = "";
        _gameFull = false; _gameTries = 0; _rankAttempted = _namesFull = false;
        _enemyNames = null;
        _allies = _enemies = null;
      }
    }

    static string TagOf(string puuid) {
      if (string.IsNullOrEmpty(puuid)) return "";
      lock (_lock) { string s; return _rankTag.TryGetValue(puuid, out s) ? s : ""; }
    }

    // ---------------------------------------------------------- player names
    // Who a seat actually is, for GhostWatch. The gameflow teams carry the
    // name in one field or another depending on client era - gameName (the
    // Riot ID) now, summonerName before that - and a player who only ever
    // arrived via playerChampionSelections carries no name at all, just a
    // puuid, so those are asked about directly. One lookup per player per
    // game, remembered like the rank tags; a failed lookup is not cached, so
    // the roster retry that is already happening fills it in.
    static readonly Dictionary<string, string> _nameOf = new Dictionary<string, string>();

    static string NameFor(int port, string pw, object p) {
      string n = TwitchEvents.SNavPublic(p, "gameName").Trim();
      if (n.Length == 0) n = TwitchEvents.SNavPublic(p, "summonerName").Trim();
      if (n.Length > 0) return n;
      string puuid = TwitchEvents.SNavPublic(p, "puuid");
      if (IsAnonymous(puuid)) return "";
      lock (_lock) { string have; if (_nameOf.TryGetValue(puuid, out have)) return have; }
      string sj = LeagueStats.LcuGet(port, pw, "/lol-summoner/v2/summoners/puuid/" + puuid);
      if (sj == null) return "";
      object root = TwitchEvents.NavPublic(sj);
      n = TwitchEvents.SNavPublic(root, "gameName").Trim();
      if (n.Length == 0) n = TwitchEvents.SNavPublic(root, "displayName").Trim();
      if (n.Length > 0) lock (_lock) _nameOf[puuid] = n;
      return n;
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

    // Five lanes, always, in the order the line promises: top, jungle, mid,
    // ADC, support. The position in the line IS the lane - that is the entire
    // reason there are no names - so a team that prints four ranks does not
    // just lose a player, it makes the other four unreadable. "Them: M565
    // M634" cannot be answered: is that top and jungle, or ADC and support?
    // Nobody can tell, and two ranks nobody can place are worth less than
    // nothing because they look like information.
    //
    // So the shape is fixed and the gaps are marked. A lane with nothing to
    // say gets "_", which is what it already meant for a player the client
    // would not name - from a viewer's seat "hidden" and "not in the payload"
    // are the same fact: no rank for that lane.
    //
    // Seats carrying a position are placed by it. Seats without one fill the
    // lanes left over, in the order the client listed them - a guess, and the
    // same guess the old one-hole deduction made, but now it can never push
    // a known lane out of place because known lanes are seated first.
    static string Format(List<Seat> seats) {
      var slots = new Seat[RoleOrder.Length];
      var loose = new List<Seat>();

      foreach (var s in seats) {
        int ix = Array.IndexOf(RoleOrder, s.Role);
        // Two players claiming one lane happens (a swap the client reports
        // mid-change). First one keeps it; the other becomes loose rather
        // than overwriting a lane that was actually stated.
        if (ix >= 0 && slots[ix] == null) slots[ix] = s;
        else loose.Add(s);
      }
      int next = 0;
      foreach (var s in loose) {
        while (next < slots.Length && slots[next] != null) next++;
        if (next >= slots.Length) break;      // more players than lanes; nothing to do
        slots[next] = s;
      }

      var bits = new List<string>();
      for (int i = 0; i < slots.Length; i++)
        bits.Add(slots[i] == null ? HiddenTag : slots[i].Tag);
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
      lock (_refreshLock) {
        object root = TwitchEvents.NavPublic(session);
        SnapshotDraft(port, pw, TwitchEvents.Nav(root, "myTeam") as object[], TwitchEvents.Nav(root, "theirTeam") as object[]);
      }
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
    static bool _gameFull;        // both sides identified; nothing left to improve
    static int _gameTries;
    static string _myPuuid = "";
    // The enemy team by NAME, for GhostWatch. Null until a roster this game
    // was captured from a payload that also said which side the streamer is
    // on - see the sideSure note below.
    static string[] _enemyNames;
    static long _rosterGeneration = -1;
    static DateTime _rosterAt = DateTime.MinValue;
    static bool _rankAttempted, _namesFull;
    static List<object> _allies, _enemies;

    // How many times one game is worth re-reading before settling for what the
    // client is willing to say. At the loop's three-second cadence that is
    // about a minute of the match, which is far longer than the payload takes
    // to fill in and short enough that a mode which never fields ten players
    // stops asking.
    const int RosterTries = 20;

    static void EnsureGameRoster(int port, string pw) { EnsureGameRoster(port, pw, true); }

    static void EnsureGameRoster(int port, string pw, bool ranks) {
      lock (_refreshLock) {
        long generation = LeagueStats.PhaseGeneration;
        bool valid = _rosterGeneration == generation && (LeagueStats.PostGame(_phase)
          || (DateTime.UtcNow - _rosterAt).TotalSeconds < 60);
        // Phase changes and observation gaps invalidate the snapshot. A minute
        // ceiling also catches a whole match missed between observations.
        if (valid && (_gameFull && _namesFull || _gameTries >= RosterTries)) {
          if (ranks && !_rankAttempted && _allies != null && _enemies != null)
            BuildRankLines(port, pw);
          return;
        }
        if (_rosterGeneration != generation) {
          Forget(); _rosterGeneration = generation;
        }
        ReadGameRoster(port, pw, ranks);
      }
    }

    static void BuildRankLines(int port, string pw) {
      string line = "Us: " + SideLine(port, pw, _allies) + "  |  Them: " + SideLine(port, pw, _enemies);
      lock (_lock) { _gameLine = line; _rankAttempted = true; }
    }

    static void ReadGameRoster(int port, string pw, bool ranks) {
      // Built once per game, but only once it is worth keeping. The old rule
      // was "once it produced any line at all", and the payload does not
      // arrive complete: read three seconds into a match, teamOne and teamTwo
      // are still filling in. That first thin answer was cached for the rest
      // of the game, which is how a lobby came back as four players against
      // two - not a parse failure, a snapshot taken too early and then
      // defended against every later read that would have corrected it.
      string sj = LeagueStats.LcuGet(port, pw, "/lol-gameflow/v1/session");
      if (sj == null) return;
      object data = TwitchEvents.Nav(TwitchEvents.NavPublic(sj), "gameData");
      long gid = LNum(data, "gameId");
      if (gid == 0) return;
      lock (_lock) {
        // A new game's names must not be read against the OLD game's roster
        // in the window before its first snapshot lands, so the list dies
        // with the game it described rather than with the arrival of the
        // next one.
        _rosterAt = DateTime.UtcNow;
        if (gid != _gameId) {
          _gameTries = 0; _gameFull = false; _enemyNames = null;
          _gameLine = ""; _rankAttempted = _namesFull = false;
          _rankTag.Clear(); _nameOf.Clear(); _myPuuid = "";
        }
        if (gid == _gameId && (_gameFull && _namesFull || _gameTries >= RosterTries)) {
          if (ranks && !_rankAttempted) BuildRankLines(port, pw);
          return;
        }
        _gameTries++;
      }

      if (_myPuuid.Length == 0) {
        string me = LeagueStats.LcuGet(port, pw, "/lol-summoner/v1/current-summoner");
        if (me != null) _myPuuid = TwitchEvents.SNavPublic(TwitchEvents.NavPublic(me), "puuid");
      }

      var one = TwitchEvents.Nav(data, "teamOne") as object[];
      var two = TwitchEvents.Nav(data, "teamTwo") as object[];
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
      var allyIds = new HashSet<string>();
      var allies = new List<object>();
      foreach (var p in mine) {
        string id = TwitchEvents.SNavPublic(p, "puuid");
        if (id.Length > 0 && allyIds.Add(id)) allies.Add(p);
      }
      var enemies = new List<object>();
      var enemySeen = new HashSet<string>();
      foreach (var p in theirs) {
        string id = TwitchEvents.SNavPublic(p, "puuid");
        if (id.Length > 0 && enemySeen.Add(id)) enemies.Add(p);
      }

      // That list covers BOTH teams, so "not an enemy" only means "an ally"
      // when the enemy side is known complete. Filing an enemy under Us would
      // be a confidently wrong answer, and a short list beats a wrong one.
      //
      // The same reasoning runs the other way and was simply never written:
      // once the ally side is known complete, "not an ally" means "an enemy".
      // Without it a short teamTwo stayed short no matter what the rest of the
      // payload knew - which is how "Them" came back with two players in it.
      var picks = TwitchEvents.Nav(data, "playerChampionSelections") as object[];
      if (picks != null && enemyIds.Count >= 5) {
        foreach (var p in picks) {
          if (allies.Count >= 5) break;
          string id = TwitchEvents.SNavPublic(p, "puuid");
          if (id.Length > 0 && !enemyIds.Contains(id) && allyIds.Add(id)) allies.Add(p);
        }
      }
      if (picks != null && allyIds.Count >= 5) {
        foreach (var p in picks) {
          if (enemies.Count >= 5) break;
          string id = TwitchEvents.SNavPublic(p, "puuid");
          if (id.Length > 0 && !allyIds.Contains(id) && enemySeen.Add(id)) enemies.Add(p);
        }
      }
      // GhostWatch wants the enemy NAMES, but only when "enemy" is a fact
      // rather than a guess. mineIsOne above defaults to teamOne whenever the
      // client would not say who the streamer is - fine for a line that
      // merely swaps Us and Them, disqualifying for a feature that publicly
      // calls people stream snipers: guessed wrong, it would read the
      // streamer's OWN team against chat, and a duo partner lurking in chat
      // is Tuesday, not a ghost. So names are captured only when the
      // streamer's puuid was found on one of the two teams; until then the
      // ghost roster stays empty and GhostWatch keeps saying it is reading.
      string[] ghostNames = null;
      if (_myPuuid.Length > 0 && (HasPuuid(one, _myPuuid) || HasPuuid(two, _myPuuid))) {
        var gn = new List<string>();
        foreach (var p in enemies) {
          string n = NameFor(port, pw, p);
          if (n.Length > 0) gn.Add(n);
        }
        ghostNames = gn.ToArray();
      }

      lock (_lock) {
        _gameId = gid;
        // Complete means both sides named. Anything less stays open to being
        // read again on the next pass, because the next pass is very likely to
        // know more than this one did.
        _gameFull = allies.Count >= 5 && enemies.Count >= 5;
        _allies = allies; _enemies = enemies;
        _rankAttempted = false;
        _namesFull = ghostNames != null && ghostNames.Length == enemies.Count && enemies.Count > 0;
        if (ghostNames != null) _enemyNames = ghostNames;
        // The draft that fed this game is consumed by it: the in-game line
        // supersedes it, and left alive it could surface later as if it
        // described a champ select that is long over.
        _draftLine = "";
      }
      if (ranks) BuildRankLines(port, pw);
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
      if (!Program.LeagueFeatureOn) return "League integration is paused on the Features page.";
      NoteInterest();
      try {
        int port; string pw;
        if (!LeagueStats.FindLockfile(out port, out pw))
          return "The League client isn't running on the stream PC.";
        string ph = LeagueStats.LcuGet(port, pw, "/lol-gameflow/v1/gameflow-phase");
        if (ph == null) return "Can't reach the League client right now.";
        _phase = ph.Trim().Trim('"');

        if (InGame()) {
          // Early-outs on a complete roster, so repeats in the same match
          // cost one gameflow read, not ten rank lookups - but an incomplete
          // one is retried, because asking again is how it gets completed.
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

    // ------------------------------------------------------------ ghost roster
    // The enemy team's names for GhostWatch, behind the same fresh-phase
    // discipline as RanksLine: every ask re-reads the gameflow phase, so a
    // finished game can never serve its roster as the current one. It does
    // NOT stamp NoteInterest - GhostWatch calls on its own clock and each
    // call already does its own reads, so waking the poll loop as well would
    // just double the traffic to the client.
    //
    // why comes back as no-client, no-phase, not-in-game or not-ready. Names
    // only while the game is actually on; InGame covers the post-game tail on
    // purpose, because a ghost caught at the death screen is still caught.
    internal static bool EnemyNamesNow(out long gameId, out string[] names, out string why) {
      gameId = 0; names = null; why = "";
      try {
        int port; string pw;
        if (!LeagueStats.FindLockfile(out port, out pw)) { why = "no-client"; return false; }
        string ph = LeagueStats.LcuGet(port, pw, "/lol-gameflow/v1/gameflow-phase");
        if (ph == null) { why = "no-phase"; return false; }
        _phase = ph.Trim().Trim('"');
        if (!InGame()) { why = "not-in-game"; return false; }
        EnsureGameRoster(port, pw, false);
        lock (_lock) {
          if (_enemyNames == null || _enemyNames.Length == 0) { why = "not-ready"; return false; }
          gameId = _gameId;
          names = _enemyNames;
        }
        return true;
      } catch { why = "no-client"; return false; }
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
    // Five lanes whatever arrives. The cases that matter are the ones a real
    // lobby produced: a short team, and a team the client gave no positions
    // for at all.
    internal static string LayoutFixtures() {
      var full = new List<Seat> {
        new Seat { Role = "SUP", Tag = "E1" }, new Seat { Role = "TOP", Tag = "G2" },
        new Seat { Role = "ADC", Tag = "M675" }, new Seat { Role = "JG", Tag = "D4" },
        new Seat { Role = "MID", Tag = "P1" }
      };
      // The reported line: two players, no way to tell which lanes they held.
      var short2 = new List<Seat> {
        new Seat { Role = "JG", Tag = "M565" }, new Seat { Role = "ADC", Tag = "M634" }
      };
      // No positions at all - they fill lanes in the order the client listed.
      var noPos = new List<Seat> {
        new Seat { Tag = "S3" }, new Seat { Tag = "B1" }, new Seat { Tag = "UR" }
      };
      // Two players claiming one lane: the first keeps it, the second is
      // seated in a leftover rather than overwriting a stated position.
      var clash = new List<Seat> {
        new Seat { Role = "MID", Tag = "C1204" }, new Seat { Role = "MID", Tag = "GM858" }
      };
      return Format(full) + " | " + Format(short2) + " | "
           + Format(noPos) + " | " + Format(clash);
    }

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
    static long LNum(object o, string key) {
      var v = TwitchEvents.Nav(o, key);
      if (v == null) return 0;
      try { return Convert.ToInt64(v); } catch { return 0; }
    }
  }
}
