// Champ select, watched from the League client's local API and served to two
// consumers: the /draft OBS source and the desktop draft window.
//
// STRICTLY READ-ONLY, and this is a rule, not a habit. The champ-select API
// also exposes POSTs that pick, ban, trade and reroll - an overlay that
// touched any of them would be automating gameplay, which is both against
// the spirit of the thing and the kind of behaviour that gets accounts
// actioned. Nothing in this file issues anything but GETs. Keep it that way.
//
// Names and icons come from the client itself (/lol-game-data/assets/...),
// so nothing here talks to the internet: the same no-API-key, no-CDN
// reasoning as the rest of LeagueStats. Everything is best-effort in the
// TwitchEvents mould: a missing client or a moved endpoint leaves the board
// hidden, never a crash.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace NowPlaying {

  static class DraftWatch {

    // ------------------------------------------------------------------ state
    static readonly object _lock = new object();
    static string _stateJson = InactiveJson("None");
    static volatile string _phase = "None";

    // Demand: the OBS page polls /draft-state (30s grace, like the session
    // tracker), and the desktop window is a standing subscriber while its
    // tray switch is on. With neither, the League client is left alone.
    static long _wantedUntilTicks;
    static volatile bool _windowWanted;

    public static void NoteInterest() {
      Interlocked.Exchange(ref _wantedUntilTicks, DateTime.UtcNow.AddSeconds(30).Ticks);
    }
    public static void SetWindowWanted(bool on) { _windowWanted = on; }
    public static bool InChampSelect { get { return _phase == "ChampSelect"; } }

    public static string StateJson(bool demo) {
      if (demo) return DemoJson();
      lock (_lock) return _stateJson;
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
          bool active = _windowWanted
                     || DateTime.UtcNow.Ticks < Interlocked.Read(ref _wantedUntilTicks);
          if (!active) { _phase = "None"; SetInactive("None"); Thread.Sleep(2000); continue; }

          int port; string pw;
          if (!LeagueStats.FindLockfile(out port, out pw)) {
            _phase = "None"; SetInactive("None");
            ClearAssets();
            Thread.Sleep(5000);
            continue;
          }

          string ph = LeagueStats.LcuGet(port, pw, "/lol-gameflow/v1/gameflow-phase");
          if (ph == null) { Thread.Sleep(5000); continue; }
          ph = ph.Trim().Trim('"');
          _phase = ph;

          if (ph != "ChampSelect") {
            SetInactive(ph);
            Thread.Sleep(2000);
            continue;
          }

          // In champ select: hovers move constantly, so this is the one loop
          // in the app that polls every second - and only for as long as the
          // draft lasts.
          EnsureAssets(port, pw);
          string session = LeagueStats.LcuGet(port, pw, "/lol-champ-select/v1/session");
          if (session != null) {
            string built = BuildJson(session);
            if (built != null) lock (_lock) _stateJson = built;
          }
          Thread.Sleep(1000);
        } catch {
          Thread.Sleep(5000);
        }
      }
    }

    static void SetInactive(string phase) {
      lock (_lock) _stateJson = InactiveJson(phase);
    }

    static string InactiveJson(string phase) {
      return "{\"active\":false,\"demo\":false,\"phase\":" + TwitchChat.Qs(phase)
           + ",\"self\":-1,\"timer\":{\"phase\":\"\",\"leftMs\":0,\"totalMs\":0,\"infinite\":false,\"nowMs\":0},"
           + "\"bans\":{\"mine\":[],\"theirs\":[],\"num\":0},\"myTeam\":[],\"theirTeam\":[],"
           + "\"actions\":[],\"extras\":{\"rerolls\":0,\"benchEnabled\":false,\"bench\":[],"
           + "\"custom\":false,\"spectating\":false,\"trades\":0,\"swaps\":0,"
           + "\"skinSelection\":false,\"battleBoost\":false,\"counter\":0}}";
    }

    // ------------------------------------------------------------- name maps
    // The client documents itself and it also DESCRIBES itself: names and
    // square portraits are served from /lol-game-data. Fetched once per
    // client run (the port changing is the tell that the client restarted,
    // and a patch may have shipped in between).
    static int _assetsPort = -1;
    static Dictionary<long, string> _champNames = new Dictionary<long, string>();
    static Dictionary<long, string> _spellNames = new Dictionary<long, string>();
    static Dictionary<long, string> _spellIcons = new Dictionary<long, string>();
    static Dictionary<long, string> _skinNames = new Dictionary<long, string>();
    static readonly Dictionary<long, byte[]> _champPng = new Dictionary<long, byte[]>();
    static readonly Dictionary<long, byte[]> _spellPng = new Dictionary<long, byte[]>();

    static void ClearAssets() {
      lock (_lock) {
        if (_assetsPort < 0) return;
        _assetsPort = -1;
        _champNames = new Dictionary<long, string>();
        _spellNames = new Dictionary<long, string>();
        _spellIcons = new Dictionary<long, string>();
        _skinNames = new Dictionary<long, string>();
        _skinsMined.Clear();
        _champPng.Clear();
        _spellPng.Clear();
        // Who you were drafting with belongs to that client run, not the next.
        _rankLine.Clear();
        _rankShort.Clear();
        _mastery.Clear();
        _looked.Clear();
        _roster = "";
      }
    }

    static void EnsureAssets(int port, string pw) {
      lock (_lock) { if (_assetsPort == port) return; }

      var champs = new Dictionary<long, string>();
      var spells = new Dictionary<long, string>();
      var spellIcons = new Dictionary<long, string>();
      var skins = new Dictionary<long, string>();

      try {
        string cj = LeagueStats.LcuGet(port, pw, "/lol-game-data/assets/v1/champion-summary.json");
        var arr = cj == null ? null : TwitchEvents.NavPublic(cj) as object[];
        if (arr != null) foreach (var o in arr) {
          long id = LNum(o, "id");
          string name = TwitchEvents.SNavPublic(o, "name");
          if (id > 0 && name.Length > 0) champs[id] = name;
        }
      } catch { }

      try {
        string sj = LeagueStats.LcuGet(port, pw, "/lol-game-data/assets/v1/summoner-spells.json");
        var arr = sj == null ? null : TwitchEvents.NavPublic(sj) as object[];
        if (arr != null) foreach (var o in arr) {
          long id = LNum(o, "id");
          if (id <= 0) continue;
          string name = TwitchEvents.SNavPublic(o, "name");
          string icon = TwitchEvents.SNavPublic(o, "iconPath");
          if (name.Length > 0) spells[id] = name;
          if (icon.Length > 0) spellIcons[id] = icon;
        }
      } catch { }

      // Skins deliberately do NOT come from skins.json: that file is by far
      // the biggest thing the asset service serves and it arrives slowly
      // enough to blow both the socket timeout and the serializer's ceiling.
      // Names are mined per champion instead - see MineChampSkins.
      lock (_lock) {
        _assetsPort = port;
        _champNames = champs;
        _spellNames = spells;
        _spellIcons = spellIcons;
        _skinNames = skins;
        _skinsMined.Clear();
        _champPng.Clear();
        _spellPng.Clear();
      }
      AppLog.Write("draft: assets loaded (" + champs.Count + " champions, "
                 + spells.Count + " spells)");
    }

    // ------------------------------------------------------- per-player facts
    // The one part of this board the client does NOT put on screen: who you
    // are actually playing with. /lol-ranked/v1/ranked-stats/<puuid> is a
    // real per-player lookup (verified - an unknown puuid comes back NONE
    // rather than echoing your own), and champion mastery works the same way.
    //
    // Allies only, and not by choice: ranked hides the enemy team behind
    // obfuscated puuids, so there is nothing to look up there. That is Riot's
    // rule and the right one - this shows you your own team, not a dossier on
    // theirs.
    //
    // Looked up once per player per champ select, off the poll thread, and
    // dropped when the client goes away. Five allies is ten calls, once.
    class Mastery { public int Level; public long Points; public string Grade = ""; }

    static readonly Dictionary<string, string> _rankLine = new Dictionary<string, string>();
    // Tier and division without the LP: chat wants "Gold I", the overlay
    // wants "Gold I · 62 LP", and neither should have to edit the other's.
    static readonly Dictionary<string, string> _rankShort = new Dictionary<string, string>();
    static readonly Dictionary<string, Dictionary<long, Mastery>> _mastery =
      new Dictionary<string, Dictionary<long, Mastery>>();
    static readonly HashSet<string> _looked = new HashSet<string>();

    static void LookUpPlayer(string puuid) {
      if (string.IsNullOrEmpty(puuid)) return;
      lock (_lock) { if (_looked.Contains(puuid)) return; _looked.Add(puuid); }
      ThreadPool.QueueUserWorkItem(delegate {
        try {
          int port; string pw;
          if (!LeagueStats.FindLockfile(out port, out pw)) return;

          string rj = LeagueStats.LcuGet(port, pw, "/lol-ranked/v1/ranked-stats/" + puuid);
          if (rj != null) {
            string tier, div, queue; int lp, w, l;
            if (LeagueStats.ParseRankedStats(rj, out tier, out div, out lp, out w, out l, out queue)
                && tier.Length > 0) {
              string shortRank = tier + (div.Length > 0 ? " " + div : "");
              lock (_lock) {
                _rankShort[puuid] = shortRank;
                _rankLine[puuid] = shortRank + " · " + lp + " LP";
              }
            } else {
              lock (_lock) { _rankShort[puuid] = "Unranked"; _rankLine[puuid] = "Unranked"; }
            }
          }

          string mj = LeagueStats.LcuGet(port, pw,
            "/lol-champion-mastery/v1/" + puuid + "/champion-mastery");
          var arr = mj == null ? null : TwitchEvents.NavPublic(mj) as object[];
          if (arr != null) {
            var map = new Dictionary<long, Mastery>();
            foreach (var o in arr) {
              long cid = LNum(o, "championId");
              if (cid <= 0) continue;
              map[cid] = new Mastery {
                Level = (int)LNum(o, "championLevel"),
                Points = LNum(o, "championPoints"),
                Grade = TwitchEvents.SNavPublic(o, "highestGrade")
              };
            }
            lock (_lock) _mastery[puuid] = map;
          }
        } catch { }
      });
    }

    static string RankOf(string puuid) {
      if (string.IsNullOrEmpty(puuid)) return "";
      lock (_lock) { string s; return _rankLine.TryGetValue(puuid, out s) ? s : ""; }
    }
    static string RankShortOf(string puuid) {
      if (string.IsNullOrEmpty(puuid)) return "";
      lock (_lock) { string s; return _rankShort.TryGetValue(puuid, out s) ? s : ""; }
    }

    static Mastery MasteryOf(string puuid, long champId) {
      if (string.IsNullOrEmpty(puuid) || champId <= 0) return null;
      lock (_lock) {
        Dictionary<long, Mastery> map;
        if (!_mastery.TryGetValue(puuid, out map)) return null;
        Mastery m;
        return map.TryGetValue(champId, out m) ? m : null;
      }
    }

    // ------------------------------------------------------------- !ranks
    // The roster line, rebuilt every champ-select poll and then KEPT once
    // the draft ends. Chat asks "!ranks" mid-game far more than mid-draft,
    // and by then the champ-select session is gone from the client - so the
    // last one seen is the answer until the next draft replaces it.
    static string _roster = "";

    static void SnapshotRoster(object[] team) {
      if (team == null || team.Length == 0) return;
      var bits = new List<string>();
      foreach (var p in team) {
        string puuid = TwitchEvents.SNavPublic(p, "puuid");
        string rank = RankShortOf(puuid);
        if (rank.Length == 0) return;      // lookups still in flight; keep the old line
        string who = TwitchEvents.SNavPublic(p, "gameName");
        if (who.Length == 0) who = ChampName(LNum(p, "championId"));
        if (who.Length == 0) continue;
        bits.Add(who + " " + rank);
      }
      if (bits.Count == 0) return;
      lock (_lock) _roster = string.Join(" · ", bits.ToArray());
    }

    // What !ranks answers. Allies only - ranked hides the enemy team behind
    // obfuscated puuids, so there is genuinely nothing to look up there, and
    // saying "my team" is more honest than implying the list is everyone.
    public static string RanksLine() {
      string snap;
      lock (_lock) snap = _roster;
      if (snap.Length > 0) return "My team: " + snap;

      // Nothing cached: either the draft watcher was never asked to run (no
      // overlay open, window switched off) or the client just started. One
      // synchronous look is cheap and makes the command work on its own
      // terms rather than depending on an overlay being open somewhere.
      NoteInterest();
      try {
        int port; string pw;
        if (!LeagueStats.FindLockfile(out port, out pw))
          return "The League client isn't running on the stream PC.";
        string ph = LeagueStats.LcuGet(port, pw, "/lol-gameflow/v1/gameflow-phase");
        if (ph == null) return "Can't reach the League client right now.";
        if (ph.Trim().Trim('"') != "ChampSelect")
          return "No champ select right now - ranks show up once a draft starts.";
        EnsureAssets(port, pw);
        string session = LeagueStats.LcuGet(port, pw, "/lol-champ-select/v1/session");
        var team = session == null ? null : Nav(TwitchEvents.NavPublic(session), "myTeam") as object[];
        if (team == null) return "No champ select right now.";
        // Fetch inline rather than firing the async lookups and returning
        // nothing: a chat command that answers "ask me again" is a worse
        // answer than one that takes a second.
        var bits = new List<string>();
        foreach (var p in team) {
          string puuid = TwitchEvents.SNavPublic(p, "puuid");
          if (puuid.Length == 0) continue;
          string rank = RankShortOf(puuid);
          if (rank.Length == 0) {
            string rj = LeagueStats.LcuGet(port, pw, "/lol-ranked/v1/ranked-stats/" + puuid);
            string tier, div, queue; int lp, w, l;
            if (rj != null
                && LeagueStats.ParseRankedStats(rj, out tier, out div, out lp, out w, out l, out queue)) {
              rank = tier.Length > 0 ? tier + (div.Length > 0 ? " " + div : "") : "Unranked";
              lock (_lock) { _rankShort[puuid] = rank; _rankLine[puuid] = rank + " · " + lp + " LP"; }
            }
          }
          if (rank.Length == 0) continue;
          string who = TwitchEvents.SNavPublic(p, "gameName");
          if (who.Length == 0) who = ChampName(LNum(p, "championId"));
          if (who.Length > 0) bits.Add(who + " " + rank);
        }
        if (bits.Count == 0) return "No ranks to read yet - the draft just started.";
        string line = string.Join(" · ", bits.ToArray());
        lock (_lock) _roster = line;
        return "My team: " + line;
      } catch {
        return "Can't read the lobby right now.";
      }
    }

    // A champion's own data file is small and carries every one of its skin
    // names - the whole-catalogue file does too, but at a size the transport
    // chokes on. Mined in the background the first time a skin id we cannot
    // name shows up; by the next one-second poll the name is there.
    static readonly HashSet<long> _skinsMined = new HashSet<long>();

    static void MineChampSkins(long champId) {
      if (champId <= 0) return;
      lock (_lock) { if (_skinsMined.Contains(champId)) return; _skinsMined.Add(champId); }
      ThreadPool.QueueUserWorkItem(delegate {
        try {
          int port; string pw;
          if (!LeagueStats.FindLockfile(out port, out pw)) return;
          string cj = LeagueStats.LcuGet(port, pw,
            "/lol-game-data/assets/v1/champions/" + champId + ".json");
          if (cj == null) return;
          var skins = TwitchEvents.NavPublic(cj, "skins") as object[];
          if (skins == null) return;
          lock (_lock) {
            foreach (var sk in skins) {
              long id = LNum(sk, "id");
              string name = TwitchEvents.SNavPublic(sk, "name");
              if (id > 0 && name.Length > 0) _skinNames[id] = name;
            }
          }
        } catch { }
      });
    }

    static string ChampName(long id) {
      lock (_lock) { string n; return _champNames.TryGetValue(id, out n) ? n : ""; }
    }
    static string SpellName(long id) {
      lock (_lock) { string n; return _spellNames.TryGetValue(id, out n) ? n : ""; }
    }
    static string SkinName(long id) {
      lock (_lock) { string n; return _skinNames.TryGetValue(id, out n) ? n : ""; }
    }

    // ------------------------------------------------------------------ icons
    // PNG bytes for the routes, cached because OBS, the dashboard preview and
    // the desktop window all want the same few dozen squares. Served from
    // whatever client is running; when none is, unknown icons simply 404 and
    // the pages draw their empty slots.
    public static byte[] ChampIconPng(long id) {
      if (id <= 0) return null;
      lock (_lock) { byte[] b; if (_champPng.TryGetValue(id, out b)) return b; }
      int port; string pw;
      if (!LeagueStats.FindLockfile(out port, out pw)) return null;
      byte[] png = LeagueStats.LcuGetRaw(port, pw,
        "/lol-game-data/assets/v1/champion-icons/" + id + ".png");
      if (png != null && png.Length > 0) lock (_lock) _champPng[id] = png;
      return png;
    }

    public static byte[] SpellIconPng(long id) {
      if (id <= 0) return null;
      lock (_lock) { byte[] b; if (_spellPng.TryGetValue(id, out b)) return b; }
      int port; string pw;
      if (!LeagueStats.FindLockfile(out port, out pw)) return null;
      // Spell icons live at per-spell paths published in summoner-spells.json,
      // so the maps have to exist even when no draft has run yet - the demo
      // preview asks for Flash long before the first champ select does.
      EnsureAssets(port, pw);
      string path;
      lock (_lock) { if (!_spellIcons.TryGetValue(id, out path)) path = null; }
      if (path == null) return null;
      byte[] png = LeagueStats.LcuGetRaw(port, pw, path);
      if (png != null && png.Length > 0) lock (_lock) _spellPng[id] = png;
      return png;
    }

    // ------------------------------------------------------------------ build
    // The session payload in, the contract out. Every name resolved here so
    // the pages never need a lookup table, and every field defaulted so the
    // contract never has holes.
    static string BuildJson(string sessionJson) {
      try {
        object s = TwitchEvents.NavPublic(sessionJson);
        if (s == null) return null;

        long self = LNum(s, "localPlayerCellId");
        var sb = new StringBuilder(4096);
        sb.Append("{\"active\":true,\"demo\":false,\"phase\":\"ChampSelect\"");
        sb.Append(",\"self\":").Append(self);

        object timer = Nav(s, "timer");
        sb.Append(",\"timer\":{\"phase\":").Append(TwitchChat.Qs(TwitchEvents.SNavPublic(timer, "phase")))
          .Append(",\"leftMs\":").Append(LNum(timer, "adjustedTimeLeftInPhase"))
          .Append(",\"totalMs\":").Append(LNum(timer, "totalTimeInPhase"))
          .Append(",\"infinite\":").Append(SBool(timer, "isInfinite") ? "true" : "false")
          .Append(",\"nowMs\":").Append(LNum(timer, "internalNowInEpochMs"))
          .Append('}');

        object bans = Nav(s, "bans");
        sb.Append(",\"bans\":{\"mine\":");
        AppendIdNameArray(sb, Nav(bans, "myTeamBans") as object[]);
        sb.Append(",\"theirs\":");
        AppendIdNameArray(sb, Nav(bans, "theirTeamBans") as object[]);
        sb.Append(",\"num\":").Append(LNum(bans, "numBans")).Append('}');

        var myTeam = Nav(s, "myTeam") as object[];
        SnapshotRoster(myTeam);   // keeps !ranks answerable after the draft ends
        sb.Append(",\"myTeam\":");
        AppendTeam(sb, myTeam, self);
        sb.Append(",\"theirTeam\":");
        AppendTeam(sb, Nav(s, "theirTeam") as object[], self);

        // actions is an array of arrays: each inner group is a simultaneous
        // slice of the draft. Flattened with the group kept as a number so
        // the timeline can draw its gaps.
        sb.Append(",\"actions\":[");
        var groups = Nav(s, "actions") as object[];
        bool first = true;
        if (groups != null) for (int g = 0; g < groups.Length; g++) {
          var acts = groups[g] as object[];
          if (acts == null) continue;
          foreach (var a in acts) {
            string type = TwitchEvents.SNavPublic(a, "type");
            if (type == "ten_bans_reveal") continue;   // a ceremony, not a move
            long champ = LNum(a, "championId");
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"group\":").Append(g)
              .Append(",\"type\":").Append(TwitchChat.Qs(type))
              .Append(",\"cell\":").Append(LNum(a, "actorCellId"))
              .Append(",\"ally\":").Append(SBool(a, "isAllyAction") ? "true" : "false")
              .Append(",\"champ\":").Append(IdName(champ, ChampName(champ)))
              .Append(",\"done\":").Append(SBool(a, "completed") ? "true" : "false")
              .Append(",\"active\":").Append(SBool(a, "isInProgress") ? "true" : "false")
              .Append('}');
          }
        }
        sb.Append(']');

        var trades = Nav(s, "trades") as object[];
        var pswaps = Nav(s, "pickOrderSwaps") as object[];
        var posswaps = Nav(s, "positionSwaps") as object[];
        sb.Append(",\"extras\":{\"rerolls\":").Append(LNum(s, "rerollsRemaining"))
          .Append(",\"benchEnabled\":").Append(SBool(s, "benchEnabled") ? "true" : "false")
          .Append(",\"bench\":[");
        var bench = Nav(s, "benchChampions") as object[];
        if (bench != null) for (int i = 0; i < bench.Length; i++) {
          long bid = LNum(bench[i], "championId");
          if (i > 0) sb.Append(',');
          sb.Append(IdName(bid, ChampName(bid)));
        }
        sb.Append("],\"custom\":").Append(SBool(s, "isCustomGame") ? "true" : "false")
          .Append(",\"spectating\":").Append(SBool(s, "isSpectating") ? "true" : "false")
          .Append(",\"trades\":").Append(trades == null ? 0 : trades.Length)
          .Append(",\"swaps\":").Append((pswaps == null ? 0 : pswaps.Length)
                                      + (posswaps == null ? 0 : posswaps.Length))
          .Append(",\"skinSelection\":").Append(SBool(s, "allowSkinSelection") ? "true" : "false")
          .Append(",\"battleBoost\":").Append(SBool(s, "allowBattleBoost") ? "true" : "false")
          .Append(",\"counter\":").Append(LNum(s, "counter"))
          .Append("}}");
        return sb.ToString();
      } catch { return null; }
    }

    static void AppendTeam(StringBuilder sb, object[] team, long self) {
      sb.Append('[');
      if (team != null) for (int i = 0; i < team.Length; i++) {
        var p = team[i];
        if (i > 0) sb.Append(',');
        long cell = LNum(p, "cellId");
        long champ = LNum(p, "championId");
        long intent = LNum(p, "championPickIntent");
        long skinId = LNum(p, "selectedSkinId");
        // The base skin is champId*1000 - it is not news that Zed is wearing
        // Zed, so only a real skin gets a name. An unknown one kicks off the
        // per-champion mining and fills in on a later poll.
        string skin = "";
        if (skinId > 0 && champ > 0 && skinId != champ * 1000) {
          skin = SkinName(skinId);
          if (skin.Length == 0) MineChampSkins(champ);
        }
        sb.Append("{\"cell\":").Append(cell)
          .Append(",\"self\":").Append(cell == self ? "true" : "false")
          .Append(",\"champ\":").Append(IdName(champ, ChampName(champ)))
          .Append(",\"intent\":").Append(IdName(intent, ChampName(intent)))
          .Append(",\"locked\":").Append(champ > 0 ? "true" : "false")
          .Append(",\"pos\":").Append(TwitchChat.Qs(TwitchEvents.SNavPublic(p, "assignedPosition")))
          .Append(",\"spells\":[")
            .Append(IdName(LNum(p, "spell1Id"), SpellName(LNum(p, "spell1Id")))).Append(',')
            .Append(IdName(LNum(p, "spell2Id"), SpellName(LNum(p, "spell2Id"))))
          .Append("],\"player\":").Append(TwitchChat.Qs(TwitchEvents.SNavPublic(p, "gameName")))
          .Append(",\"tag\":").Append(TwitchChat.Qs(TwitchEvents.SNavPublic(p, "tagLine")))
          .Append(",\"skin\":").Append(TwitchChat.Qs(skin));

        // Rank and mastery, the two things the client itself never shows in
        // champ select. Keyed off the puuid, so the enemy team - obfuscated
        // in ranked - simply comes back blank rather than needing a rule.
        string puuid = TwitchEvents.SNavPublic(p, "puuid");
        if (puuid.Length > 0) LookUpPlayer(puuid);
        long shown = champ > 0 ? champ : intent;
        var mast = MasteryOf(puuid, shown);
        sb.Append(",\"rank\":").Append(TwitchChat.Qs(RankOf(puuid)))
          .Append(",\"mastery\":");
        if (mast == null) sb.Append("{\"level\":0,\"points\":0,\"grade\":\"\"}");
        else sb.Append("{\"level\":").Append(mast.Level)
               .Append(",\"points\":").Append(mast.Points)
               .Append(",\"grade\":").Append(TwitchChat.Qs(mast.Grade)).Append('}');
        sb.Append('}');
      }
      sb.Append(']');
    }

    static void AppendIdNameArray(StringBuilder sb, object[] ids) {
      sb.Append('[');
      if (ids != null) for (int i = 0; i < ids.Length; i++) {
        long id = 0;
        try { id = Convert.ToInt64(ids[i]); } catch { }
        if (i > 0) sb.Append(',');
        sb.Append(IdName(id, ChampName(id)));
      }
      sb.Append(']');
    }

    static string IdName(long id, string name) {
      if (id <= 0) return "{\"id\":0,\"name\":\"\"}";
      return "{\"id\":" + id + ",\"name\":" + TwitchChat.Qs(name) + "}";
    }

    // Object-walking versions of the JSON helpers: NavPublic parses a string;
    // these walk what it returned.
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
    static bool SBool(object o, string key) {
      return TwitchEvents.SNavPublic(o, key).Equals("True", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------- demo
    // One canned mid-draft, served to the OBS page's ?demo=1 and the desktop
    // window's preview alike - a single source of truth for what "a draft"
    // looks like, so the two can never rehearse different plays. Icons still
    // come from the live client when it is running; without it the pages
    // draw their empty slots, which is honest.
    //
    // Shaped as RANKED SOLO actually behaves, which is stingier than the
    // schema allows and the only shape worth rehearsing: allies carry names,
    // roles, spells, hovers, ranks and mastery; the enemy team carries locked
    // champions and nothing else, because ranked hides their names behind
    // obfuscated puuids and never broadcasts their hovers, spells or roles.
    // An earlier demo showed enemies hovering with full detail - it dressed
    // beautifully and would have been a lie on stream.
    static string _demo;
    static string DemoJson() {
      if (_demo != null) return _demo;
      _demo = "{\"active\":true,\"demo\":true,\"phase\":\"ChampSelect\",\"self\":2,"
        + "\"timer\":{\"phase\":\"BAN_PICK\",\"leftMs\":23000,\"totalMs\":30000,\"infinite\":false,\"nowMs\":0},"
        + "\"bans\":{\"mine\":[{\"id\":86,\"name\":\"Garen\"},{\"id\":11,\"name\":\"Master Yi\"},{\"id\":157,\"name\":\"Yasuo\"}],"
        + "\"theirs\":[{\"id\":238,\"name\":\"Zed\"},{\"id\":55,\"name\":\"Katarina\"},{\"id\":0,\"name\":\"\"}],\"num\":5},"
        + "\"myTeam\":["
        + "{\"cell\":0,\"self\":false,\"champ\":{\"id\":54,\"name\":\"Malphite\"},\"intent\":{\"id\":0,\"name\":\"\"},\"locked\":true,\"pos\":\"top\",\"spells\":[{\"id\":4,\"name\":\"Flash\"},{\"id\":12,\"name\":\"Teleport\"}],\"player\":\"RockSolid\",\"tag\":\"NA1\",\"skin\":\"\",\"rank\":\"Gold I · 62 LP\",\"mastery\":{\"level\":7,\"points\":142300,\"grade\":\"S\"}},"
        + "{\"cell\":1,\"self\":false,\"champ\":{\"id\":64,\"name\":\"Lee Sin\"},\"intent\":{\"id\":0,\"name\":\"\"},\"locked\":true,\"pos\":\"jungle\",\"spells\":[{\"id\":11,\"name\":\"Smite\"},{\"id\":4,\"name\":\"Flash\"}],\"player\":\"KickFlip\",\"tag\":\"NA1\",\"skin\":\"God Fist Lee Sin\",\"rank\":\"Platinum IV · 8 LP\",\"mastery\":{\"level\":6,\"points\":48120,\"grade\":\"A+\"}},"
        + "{\"cell\":2,\"self\":true,\"champ\":{\"id\":0,\"name\":\"\"},\"intent\":{\"id\":91,\"name\":\"Talon\"},\"locked\":false,\"pos\":\"middle\",\"spells\":[{\"id\":4,\"name\":\"Flash\"},{\"id\":14,\"name\":\"Ignite\"}],\"player\":\"92explorer\",\"tag\":\"posty\",\"skin\":\"\",\"rank\":\"Gold III · 25 LP\",\"mastery\":{\"level\":5,\"points\":21400,\"grade\":\"A\"}},"
        + "{\"cell\":3,\"self\":false,\"champ\":{\"id\":222,\"name\":\"Jinx\"},\"intent\":{\"id\":0,\"name\":\"\"},\"locked\":true,\"pos\":\"bottom\",\"spells\":[{\"id\":4,\"name\":\"Flash\"},{\"id\":7,\"name\":\"Heal\"}],\"player\":\"PewPew\",\"tag\":\"NA1\",\"skin\":\"\",\"rank\":\"Gold II · 44 LP\",\"mastery\":{\"level\":4,\"points\":9800,\"grade\":\"B+\"}},"
        + "{\"cell\":4,\"self\":false,\"champ\":{\"id\":0,\"name\":\"\"},\"intent\":{\"id\":412,\"name\":\"Thresh\"},\"locked\":false,\"pos\":\"utility\",\"spells\":[{\"id\":4,\"name\":\"Flash\"},{\"id\":3,\"name\":\"Exhaust\"}],\"player\":\"HookCity\",\"tag\":\"NA1\",\"skin\":\"\",\"rank\":\"Unranked\",\"mastery\":{\"level\":3,\"points\":4100,\"grade\":\"B\"}}"
        + "],\"theirTeam\":["
        + "{\"cell\":5,\"self\":false,\"champ\":{\"id\":266,\"name\":\"Aatrox\"},\"intent\":{\"id\":0,\"name\":\"\"},\"locked\":true,\"pos\":\"\",\"spells\":[{\"id\":0,\"name\":\"\"},{\"id\":0,\"name\":\"\"}],\"player\":\"\",\"tag\":\"\",\"skin\":\"\",\"rank\":\"\",\"mastery\":{\"level\":0,\"points\":0,\"grade\":\"\"}},"
        + "{\"cell\":6,\"self\":false,\"champ\":{\"id\":120,\"name\":\"Hecarim\"},\"intent\":{\"id\":0,\"name\":\"\"},\"locked\":true,\"pos\":\"\",\"spells\":[{\"id\":0,\"name\":\"\"},{\"id\":0,\"name\":\"\"}],\"player\":\"\",\"tag\":\"\",\"skin\":\"\",\"rank\":\"\",\"mastery\":{\"level\":0,\"points\":0,\"grade\":\"\"}},"
        + "{\"cell\":7,\"self\":false,\"champ\":{\"id\":103,\"name\":\"Ahri\"},\"intent\":{\"id\":0,\"name\":\"\"},\"locked\":true,\"pos\":\"\",\"spells\":[{\"id\":0,\"name\":\"\"},{\"id\":0,\"name\":\"\"}],\"player\":\"\",\"tag\":\"\",\"skin\":\"\",\"rank\":\"\",\"mastery\":{\"level\":0,\"points\":0,\"grade\":\"\"}},"
        + "{\"cell\":8,\"self\":false,\"champ\":{\"id\":0,\"name\":\"\"},\"intent\":{\"id\":0,\"name\":\"\"},\"locked\":false,\"pos\":\"\",\"spells\":[{\"id\":0,\"name\":\"\"},{\"id\":0,\"name\":\"\"}],\"player\":\"\",\"tag\":\"\",\"skin\":\"\",\"rank\":\"\",\"mastery\":{\"level\":0,\"points\":0,\"grade\":\"\"}},"
        + "{\"cell\":9,\"self\":false,\"champ\":{\"id\":0,\"name\":\"\"},\"intent\":{\"id\":0,\"name\":\"\"},\"locked\":false,\"pos\":\"\",\"spells\":[{\"id\":0,\"name\":\"\"},{\"id\":0,\"name\":\"\"}],\"player\":\"\",\"tag\":\"\",\"skin\":\"\",\"rank\":\"\",\"mastery\":{\"level\":0,\"points\":0,\"grade\":\"\"}}"
        + "],\"actions\":["
        + "{\"group\":0,\"type\":\"ban\",\"cell\":0,\"ally\":true,\"champ\":{\"id\":86,\"name\":\"Garen\"},\"done\":true,\"active\":false},"
        + "{\"group\":0,\"type\":\"ban\",\"cell\":5,\"ally\":false,\"champ\":{\"id\":238,\"name\":\"Zed\"},\"done\":true,\"active\":false},"
        + "{\"group\":0,\"type\":\"ban\",\"cell\":1,\"ally\":true,\"champ\":{\"id\":11,\"name\":\"Master Yi\"},\"done\":true,\"active\":false},"
        + "{\"group\":0,\"type\":\"ban\",\"cell\":6,\"ally\":false,\"champ\":{\"id\":55,\"name\":\"Katarina\"},\"done\":true,\"active\":false},"
        + "{\"group\":1,\"type\":\"pick\",\"cell\":5,\"ally\":false,\"champ\":{\"id\":266,\"name\":\"Aatrox\"},\"done\":true,\"active\":false},"
        + "{\"group\":1,\"type\":\"pick\",\"cell\":0,\"ally\":true,\"champ\":{\"id\":54,\"name\":\"Malphite\"},\"done\":true,\"active\":false},"
        + "{\"group\":1,\"type\":\"pick\",\"cell\":1,\"ally\":true,\"champ\":{\"id\":64,\"name\":\"Lee Sin\"},\"done\":true,\"active\":false},"
        + "{\"group\":2,\"type\":\"pick\",\"cell\":6,\"ally\":false,\"champ\":{\"id\":120,\"name\":\"Hecarim\"},\"done\":true,\"active\":false},"
        + "{\"group\":2,\"type\":\"pick\",\"cell\":2,\"ally\":true,\"champ\":{\"id\":0,\"name\":\"\"},\"done\":false,\"active\":true},"
        + "{\"group\":2,\"type\":\"pick\",\"cell\":3,\"ally\":true,\"champ\":{\"id\":222,\"name\":\"Jinx\"},\"done\":true,\"active\":false},"
        + "{\"group\":3,\"type\":\"pick\",\"cell\":7,\"ally\":false,\"champ\":{\"id\":103,\"name\":\"Ahri\"},\"done\":true,\"active\":false},"
        + "{\"group\":3,\"type\":\"pick\",\"cell\":4,\"ally\":true,\"champ\":{\"id\":0,\"name\":\"\"},\"done\":false,\"active\":false}"
        + "],\"extras\":{\"rerolls\":0,\"benchEnabled\":false,\"bench\":[],\"custom\":false,"
        + "\"spectating\":false,\"trades\":1,\"swaps\":0,\"skinSelection\":true,"
        + "\"battleBoost\":false,\"counter\":9}}";
      return _demo;
    }
  }
}
