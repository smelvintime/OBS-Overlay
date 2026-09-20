// Ghost watch: is anyone from the enemy team watching the stream?
//
// "Ghosting" (stream sniping) is an opponent opening your stream mid-game to
// see your side of the map. Streamers catch them by hand today: alt-tab to
// the viewer list and squint at ten Riot names. This automates exactly that
// check and nothing more - the enemy team's names, read from the League
// client the way everything League already is, against the channel's own
// chatter list from Twitch, with one chat line when they overlap.
//
// The call-out is public and names a real person, so the matcher is
// deliberately narrow: a name has to match exactly (case, spaces and
// underscores aside), short names never count, and the only fuzz allowed is
// the "TTV"/"twitch" tag people staple onto their own names - because that
// tag's entire meaning is "the rest of this is my Twitch name". Missing a
// sniper is cheaper than accusing an innocent viewer in front of the whole
// chat, and every rule here leans that way.
//
// Names are checked one way only: enemies. The streamer's own team is never
// looked at - a duo partner lurking in chat is Tuesday, not a ghost. And the
// check only exists in-game, because ranked hides the enemy for the whole
// draft; there is genuinely nothing to compare until the match starts.
//
// Split of labour, same as the game-result announcements: this file is the
// eyes (its own loop, its own state, its own status for the dashboard);
// TwitchChat is the mouth, and only speaks when the bot itself is on.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace NowPlaying {

  static class GhostWatch {

    // ------------------------------------------------------------------ state
    internal static bool Enabled { get { return _enabled; } }
    static volatile bool _enabled;                 // the dashboard's Ghost watch switch
    // off | no-twitch | no-scope | idle | watching | error. no-scope gets its
    // own word because it has its own fix (reconnect Twitch) and would
    // otherwise be indistinguishable from "nothing found".
    static volatile string _status = "off";
    static volatile string _detail = "";

    static readonly object _lock = new object();
    static long _gameId;                           // the game the findings belong to
    static int _enemyCount;
    // What was spotted, as ready-made display strings, sticky for the game:
    // a sniper who closes the tab after being called out did not un-snipe.
    static readonly List<string> _found = new List<string>();
    // Announce once per (enemy, viewer) pair per game - the whole game, not
    // per scan, or one ghost would be re-announced every twenty seconds.
    static readonly HashSet<string> _announced = new HashSet<string>();

    // The viewer list, and when it was fetched. Only the loop thread touches
    // the list itself; the counters are shared with the status readers.
    static List<string[]> _chatters;               // [login, display-name]
    static DateTime _chattersAt = DateTime.MinValue;
    static int _chatterTotal = -1;                 // -1 = never fetched
    static volatile string _scanAt = "";           // when names and viewers were last compared

    public static void SetEnabled(bool on) {
      if (_enabled == on) return;
      _enabled = on;
      if (!on) { _status = "off"; _detail = ""; }
      AppLog.Write("ghosts: watch " + (on ? "on" : "off"));
    }

    // ------------------------------------------------------------------- loop
    public static void Start() {
      var t = new Thread(Loop);
      t.IsBackground = true;
      t.Start();
    }

    // How stale the viewer list may be before a scan refetches it. Chatters
    // move slowly, and a sniper who opens the stream mid-game is caught a
    // minute later at worst - while the alternative, fetching per scan, asks
    // Twitch three times as often for the same answer.
    const int ChatterMaxAgeSec = 60;

    static void Loop() {
      while (true) {
        int sleepMs = 12000;
        try {
          if (!_enabled) { Thread.Sleep(3000); continue; }
          if (!Program.LeagueFeatureOn) {
            _status = "paused"; _detail = "League integration is paused on the Features page";
            Thread.Sleep(3000); continue;
          }

          if (!TwitchEvents.ApiReady) {
            // The viewer list rides the broadcaster connection, not the
            // bot's - chat:read cannot see who is silently watching.
            _status = "no-twitch";
            _detail = "the main Twitch connection isn't up - the viewer list is read through it";
            Thread.Sleep(15000);
            continue;
          }

          long gid; string[] names; string why;
          if (!LobbyRanks.EnemyNamesNow(out gid, out names, out why)) {
            if (why == "not-ready") {
              // In a game; the roster is still filling in (or the client
              // never said which side is ours - see LobbyRanks). Try again
              // soon: this is the window a snapshot lands in.
              _status = "watching";
              _detail = "In a game - still reading the enemy roster";
              sleepMs = 5000;
            } else {
              _status = "idle";
              _detail = why == "no-client" ? "The League client isn't running on this PC"
                      : why == "no-phase" ? "Can't reach the League client right now"
                      : "Waiting for a game - the check runs while a match is on";
            }
            Thread.Sleep(sleepMs);
            continue;
          }

          lock (_lock) {
            if (gid != _gameId) {
              // A fresh game gets a clean slate. The old findings die here,
              // on the next game starting, not on the old one ending - so
              // between games !ghosts can still answer for the match that
              // just finished, the same way !ranks does.
              _gameId = gid;
              _found.Clear();
              _announced.Clear();
            }
            _enemyCount = names.Length;
          }

          if ((DateTime.UtcNow - _chattersAt).TotalSeconds > ChatterMaxAgeSec) {
            string problem;
            if (!FetchChatters(out problem)) {
              if (problem == "scope") {
                // A settled answer until Twitch is reconnected, which
                // restarts the app - so scanning is pointless and the wait
                // can be long.
                _status = "no-scope";
                _detail = "Twitch refused the viewer list - this connection was made before "
                        + "ghost watch existed, so it never asked for that permission. "
                        + "Reconnect on the Setup page once and it sticks.";
                Thread.Sleep(60000);
                continue;
              }
              // Network trouble is not a verdict. An earlier list, if one
              // exists, is still worth scanning against - a minute of
              // staleness beats a blind pass - and with none there is
              // nothing to compare yet.
              if (_chatters == null) {
                _status = "watching";
                _detail = "Couldn't reach Twitch for the viewer list - retrying";
                Thread.Sleep(10000);
                continue;
              }
            }
          }

          var hits = FindGhosts(names, _chatters, TwitchChat.ChannelName, TwitchChat.BotName);
          var fresh = new List<string>();
          lock (_lock) {
            foreach (var h in hits) {
              if (!_announced.Add(Norm(h.Enemy) + "|" + h.Viewer)) continue;
              string shown = h.Enemy + " (in chat as " + h.Viewer + ")";
              _found.Add(shown);
              fresh.Add(shown);
            }
          }
          if (fresh.Count > 0) {
            // At most five entries ever - the enemy team is five people - so
            // the line cannot outgrow a chat message.
            string line = "Ghosters here: " + string.Join(", ", fresh.ToArray());
            AppLog.Write("ghosts: " + line + " (game " + gid + ")");
            TwitchChat.OnGhostsFound(line);
          }

          _scanAt = DateTime.Now.ToString("HH:mm:ss");
          _status = "watching";
          _detail = "";
          sleepMs = 20000;
        } catch (Exception ex) {
          _status = "error";
          _detail = ex.Message;
        }
        Thread.Sleep(sleepMs);
      }
    }

    // ---------------------------------------------------------- viewer list
    // Helix's chatter list, via the same authenticated helper every other
    // Twitch read uses. Needs moderator:read:chatters, which the setup wizard
    // asks for now but older connections never did - and Twitch reports that
    // as 401 (or 403 for a non-moderator), AFTER the automatic token refresh
    // has already been tried. So a 401 landing here means the token works for
    // everything else and simply does not carry the scope: "scope", fixed by
    // reconnecting, as distinct from "net", fixed by waiting.
    const int ChatterPagesMax = 10;      // 10k viewers; a bigger room than this
                                         // makes one-name matching noise anyway

    static bool FetchChatters(out string problem) {
      problem = "";
      var list = new List<string[]>();
      int total = -1;
      string cursor = "";
      for (int page = 0; page < ChatterPagesMax; page++) {
        int http;
        string body = TwitchEvents.Helix(
          "https://api.twitch.tv/helix/chat/chatters?broadcaster_id=" + TwitchEvents.BroadcasterId
          + "&moderator_id=" + TwitchEvents.BroadcasterId + "&first=1000"
          + (cursor.Length > 0 ? "&after=" + Uri.EscapeDataString(cursor) : ""), out http);
        if (body == null) {
          problem = (http == 401 || http == 403) ? "scope" : "net";
          return false;
        }
        var data = TwitchEvents.NavPublic(body, "data") as object[];
        if (data == null) { problem = "net"; return false; }
        foreach (var e in data) {
          string login = TwitchEvents.SNavPublic(e, "user_login").Trim().ToLowerInvariant();
          if (login.Length == 0) continue;
          list.Add(new[] { login, TwitchEvents.SNavPublic(e, "user_name").Trim() });
        }
        object root = TwitchEvents.NavPublic(body);
        if (total < 0) {
          int t;
          if (int.TryParse(TwitchEvents.SNavPublic(root, "total"), out t)) total = t;
        }
        cursor = TwitchEvents.SNavPublic(root, "pagination", "cursor");
        if (cursor.Length == 0) break;
      }
      _chatters = list;
      _chattersAt = DateTime.UtcNow;
      lock (_lock) _chatterTotal = total >= 0 ? total : list.Count;
      return true;
    }

    // -------------------------------------------------------------- matching
    // Everything from here to the test section is pure - names in, verdicts
    // out - so /ghosts/test can prove the accusation rules without a game or
    // a viewer list. That matters more here than anywhere else in the app:
    // a wrong !record is a wrong number, a wrong ghost call is a wrong PERSON.

    // A name flattened to the parts both sides can carry: letters and digits,
    // lowercased. Riot names allow spaces where Twitch loves underscores, so
    // "Dark Slayer" and "dark_slayer" both come out "darkslayer". A Riot tag
    // ("#EUW") is routing, not name, and is cut first - it can never appear
    // in a Twitch login anyway.
    internal static string Norm(string name) {
      if (name == null) return "";
      int hash = name.IndexOf('#');
      if (hash >= 0) name = name.Substring(0, hash);
      var sb = new StringBuilder(name.Length);
      foreach (char c in name.Trim().ToLowerInvariant())
        if (char.IsLetterOrDigit(c)) sb.Append(c);
      return sb.ToString();
    }

    // Shorter than this and an exact match is a coincidence, not a catch -
    // "Bob" meeting a viewer called bob proves nothing worth telling a live
    // audience. Applied to every variant, so a strip can never manufacture a
    // match the full name would have been too short to earn.
    const int MinNameLen = 4;

    // The variants one name answers to: itself, and itself without a leading
    // or trailing stream tag. Longest tag first, so "twitchtv" is read as the
    // one word it is before "twitch" gets a bite at it.
    static readonly string[] StreamTags = { "twitchtv", "twitch", "ttv" };

    internal static List<string> Variants(string raw) {
      var list = new List<string>();
      string n = Norm(raw);
      if (n.Length >= MinNameLen) list.Add(n);
      foreach (var tag in StreamTags) {
        if (n.Length - tag.Length < MinNameLen) continue;
        if (n.StartsWith(tag, StringComparison.Ordinal)) list.Add(n.Substring(tag.Length));
        if (n.EndsWith(tag, StringComparison.Ordinal)) list.Add(n.Substring(0, n.Length - tag.Length));
      }
      return list;
    }

    internal class Hit { public string Enemy; public string Viewer; }

    // The verdicts: which enemies are in chat, and as whom. Login and display
    // name both count for a viewer - they are the same word to a human, and
    // for a non-ASCII display name the login could never match at all. The
    // broadcaster and the bot are skipped outright: both are in their own
    // chatter list every minute of every stream, and neither is news.
    internal static List<Hit> FindGhosts(IEnumerable<string> enemies, IEnumerable<string[]> chatters,
                                         string channelLogin, string botLogin) {
      var hits = new List<Hit>();
      if (enemies == null || chatters == null) return hits;

      var byVariant = new Dictionary<string, string>();      // variant -> login
      foreach (var c in chatters) {
        if (c == null || c.Length == 0) continue;
        string login = c[0];
        if (login == channelLogin || login == botLogin) continue;
        foreach (var v in Variants(login))
          if (!byVariant.ContainsKey(v)) byVariant[v] = login;
        if (c.Length > 1)
          foreach (var v in Variants(c[1]))
            if (!byVariant.ContainsKey(v)) byVariant[v] = login;
      }

      var taken = new HashSet<string>();                     // one hit per pair
      foreach (var e in enemies) {
        string clean = CleanRiotName(e);
        if (clean.Length == 0) continue;
        foreach (var v in Variants(e)) {
          string login;
          if (!byVariant.TryGetValue(v, out login)) continue;
          if (taken.Add(Norm(e) + "|" + login))
            hits.Add(new Hit { Enemy = clean, Viewer = login });
          break;
        }
      }
      return hits;
    }

    // The name as chat should read it: the Riot ID without its #tag. The tag
    // is dropped from what is SAID as well as what is compared - "Faker#KR1"
    // in a call-out reads like a database, and the tag identifies a region,
    // not a person.
    static string CleanRiotName(string riot) {
      if (riot == null) return "";
      int hash = riot.IndexOf('#');
      return (hash >= 0 ? riot.Substring(0, hash) : riot).Trim();
    }

    // ------------------------------------------------------------------ chat
    // What !ghosts answers. From the loop's own state rather than a fresh
    // synchronous sweep: the loop scans every twenty seconds while a game is
    // on, the command cools down for thirty, and an answer up to one scan old
    // is a fair trade for never stacking a Helix fetch onto the chat thread.
    public static string CommandLine() {
      if (!Program.LeagueFeatureOn) return "League integration is paused on the Features page.";
      if (!_enabled)
        return "Ghost watch is switched off.";
      string s = _status;
      if (s == "no-twitch" || s == "error")
        return "Can't check that right now.";
      if (s == "no-scope")
        return "Can't read the viewer list right now - Twitch needs reconnecting on the stream PC.";

      string found; long gid; int enemies, viewers;
      lock (_lock) {
        found = _found.Count > 0 ? string.Join(", ", _found.ToArray()) : "";
        gid = _gameId; enemies = _enemyCount; viewers = _chatterTotal;
      }

      if (s == "watching") {
        if (found.Length > 0) return "Ghosters here: " + found;
        if (enemies == 0) return "Still reading the lobby - ask again in a moment.";
        return "No ghosters spotted - " + enemies + " enemy name" + (enemies == 1 ? "" : "s")
             + " checked against " + (viewers >= 0 ? viewers.ToString() : "the") + " people in chat.";
      }
      // Between games the last match's findings outlive the post-game screen,
      // honestly labelled as past - the same rule !ranks follows.
      if (found.Length > 0 && gid != 0) return "Last game - ghosters spotted: " + found;
      return "Not in a game right now - ghost watch runs while a match is on.";
    }

    // ------------------------------------------------------------------ status
    public static string StatusJson() {
      string[] found; int enemies, viewers;
      lock (_lock) {
        found = _found.ToArray();
        enemies = _enemyCount;
        viewers = _chatterTotal;
      }
      var sb = new StringBuilder();
      sb.Append('{');
      sb.Append("\"enabled\":").Append(_enabled ? "true" : "false").Append(',');
      sb.Append("\"status\":").Append(TwitchChat.Qs(_status)).Append(',');
      sb.Append("\"detail\":").Append(TwitchChat.Qs(_detail)).Append(',');
      sb.Append("\"enemies\":").Append(enemies).Append(',');
      sb.Append("\"chatters\":").Append(viewers).Append(',');
      sb.Append("\"scanAt\":").Append(TwitchChat.Qs(_scanAt)).Append(',');
      sb.Append("\"found\":[");
      for (int i = 0; i < found.Length; i++) {
        if (i > 0) sb.Append(',');
        sb.Append(TwitchChat.Qs(found[i]));
      }
      sb.Append("]}");
      return sb.ToString();
    }

    // ------------------------------------------------------------------- test
    // Canned names through the real matcher. Every rule that keeps an
    // innocent viewer out of a call-out has a fixture proving it holds:
    // the length floor, no substring matching, tag stripping only ever
    // NARROWING a name, and the broadcaster's own presence never counting.
    public static string TestMatch() {
      var enemies = new[] {
        "Dark Slayer",        // spaces vs underscores: the ordinary catch
        "TTV BigGhost",       // the sniper wearing their Twitch name openly
        "Faker#KR1",          // Riot tag cut before comparing
        "NightCrawler",       // the tag on the VIEWER'S side instead
        "Bob",                // too short: an exact match still says nothing
        "xX Shadow Xx",       // decorated name vs plain viewer: NOT a match
        "Certain",            // prefix of a viewer's name: NOT a match
        "My Channel"          // reads like the broadcaster: never counted
      };
      var chatters = new List<string[]> {
        new[] { "dark_slayer", "Dark_Slayer" },
        new[] { "bigghost", "BigGhost" },
        new[] { "faker", "Faker" },
        new[] { "ttv_nightcrawler", "TTV_NightCrawler" },
        new[] { "bob", "Bob" },
        new[] { "shadow", "Shadow" },
        new[] { "certainly", "Certainly" },
        new[] { "mychannel", "MyChannel" },
        new[] { "the_bot", "The_Bot" }
      };
      var hits = FindGhosts(enemies, chatters, "mychannel", "the_bot");
      var sb = new StringBuilder();
      sb.Append("{\"hits\":[");
      for (int i = 0; i < hits.Count; i++) {
        if (i > 0) sb.Append(',');
        sb.Append("{\"enemy\":").Append(TwitchChat.Qs(hits[i].Enemy))
          .Append(",\"viewer\":").Append(TwitchChat.Qs(hits[i].Viewer)).Append('}');
      }
      sb.Append("],\"hitsExpected\":\"Dark Slayer/dark_slayer, TTV BigGhost/bigghost, "
              + "Faker/faker, NightCrawler/ttv_nightcrawler - and nothing else\"");
      // The normaliser on its own, for the shapes worth pinning: tag cut,
      // decoration stripped, case folded.
      sb.Append(",\"norm\":")
        .Append(TwitchChat.Qs(Norm("Dark Slayer") + " " + Norm("Faker#KR1") + " "
                            + Norm("xX_Shadow_Xx") + " " + Norm("TTV BigGhost")));
      sb.Append(",\"normExpected\":\"darkslayer faker xxshadowxx ttvbigghost\"");
      // Variants: the tag strips, and the floor stopping a strip that would
      // leave too little name behind ("ttvab" must not offer "ab").
      sb.Append(",\"variants\":")
        .Append(TwitchChat.Qs(string.Join(",", Variants("TTV BigGhost").ToArray()) + " | "
                            + string.Join(",", Variants("ttv_ab").ToArray())));
      sb.Append(",\"variantsExpected\":\"ttvbigghost,bigghost | ttvab\"");
      sb.Append('}');
      return sb.ToString();
    }
  }
}
