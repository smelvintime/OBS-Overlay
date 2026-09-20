// Offline regression check: build.ps1 -Check. No application startup, saved
// settings, League process, Twitch connection, or third-party test framework.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace NowPlaying {
  static class Checks {
    static int _checks;
    const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
    static void Equal(object actual, object expected, string label) {
      if (Convert.ToString(actual) != Convert.ToString(expected))
        throw new Exception(label + ": expected " + expected + ", got " + actual);
      _checks++;
    }
    static void Set(Type type, string name, object value) { type.GetField(name, Flags).SetValue(null, value); }
    static object Get(Type type, string name) { return type.GetField(name, Flags).GetValue(null); }
    static object Call(Type type, string name, params object[] args) {
      foreach (var method in type.GetMethods(Flags))
        if (method.Name == name && method.GetParameters().Length == args.Length) return method.Invoke(null, args);
      throw new Exception("Missing method " + name);
    }
    static object Field(object json, params string[] path) { return TwitchEvents.Nav(json, path); }
    static object Json(string json) { return TwitchEvents.NavPublic(json); }

    static int Main() {
      try {
        Set(typeof(LeagueStats), "_dayLoaded", true); // fixtures never touch the user's day file
        ParserChecks(); ScheduleChecks(); HistoryChecks(); TransportAndRosterChecks(); HeaderChecks();
        Console.WriteLine(_checks + " checks passed (offline; no app startup or user settings writes).");
        return 0;
      } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void ParserChecks() {
      object league = Json(LeagueStats.TestParse());
      foreach (string key in new[] { "ok", "eogOk", "eogRanked", "eogNoWinKeyWon", "todayOk", "rankOk" })
        Equal(Field(league, key), true, key);
      Equal(Field(league, "record"), "W W", "ranked record");
      Equal(Field(league, "newestGameId"), 105, "newest ranked");
      Equal(Field(league, "eogAramIsRanked"), false, "ARAM end screen");
      Equal(Field(league, "absLp"), 2245, "absolute LP");
      foreach (string key in new[] { "rankTags", "hiddenSeats", "laneLayout", "walkStops", "dayVerdicts" })
        Equal(Field(league, key), Field(league, key + "Expected"), key);
      Equal(Field(league, "todayOnePage"), 20, "single page tally");
      Equal(Field(league, "todayAllPages"), 41, "deduplicated pages");
      Equal(Field(league, "recordWalked"), "W W W W W", "deep record");
      object ghosts = Json(GhostWatch.TestMatch());
      Equal(((object[])Field(ghosts, "hits")).Length, 4, "ghost matches");
      Equal(Field(ghosts, "norm"), Field(ghosts, "normExpected"), "ghost normalization");
      Equal(Field(ghosts, "variants"), Field(ghosts, "variantsExpected"), "ghost variants");
      string text = "quote\" slash\\ newline\n\r\t\u0001 \ud83c\udfb5";
      Equal(Json((string)Call(typeof(Program), "Q", text)), text, "JSON escaping");
      Equal(Call(typeof(Program), "Q", new object[] { null }), "\"\"", "null JSON convention");
    }

    static void ScheduleChecks() {
      DateTime start = DateTime.UtcNow;
      var schedule = new LeagueStats.PollSchedule();
      schedule.Observe("InProgress", start);
      Equal(schedule.Observe("Reconnect", start), false, "reconnect is not game end");
      Equal(schedule.Observe("PreEndOfGame", start), true, "end after reconnect");
      Equal(schedule.Observe("EndOfGame", start), false, "one chase per transition");
      schedule.Observe("Reconnect", start);
      Equal(schedule.Observe("EndOfGame", start), false, "post-game reconnect does not restart chase");
      int reads = 0;
      for (int seconds = 0; seconds <= 90; seconds++) {
        DateTime now = start.AddSeconds(seconds);
        if (schedule.Due(now)) { reads++; schedule.Read(now); }
      }
      Equal(reads, 5, "bounded history chase");
      Equal(schedule.Due(start.AddSeconds(100)), false, "no extra chase after budget");
      schedule = new LeagueStats.PollSchedule();
      schedule.Read(start);
      Equal(schedule.Due(start.AddSeconds(299)), false, "empty/failed history waits five minutes");
      Equal(schedule.Due(start.AddSeconds(300)), true, "normal refresh");
      schedule.Observe("InProgress", start);
      schedule.Observe("WaitingForStats", start);
      schedule.Finish(start.AddSeconds(5));
      Equal(schedule.Due(start.AddSeconds(80)), false, "caught up cancels chase");
    }

    static string Page(int firstId, int count, string queue) {
      long midnight = (long)Call(typeof(LeagueStats), "LocalMidnightEpochMs");
      return (string)Call(typeof(LeagueStats), "FixPageMode", firstId, count, midnight, queue,
                         queue == "450" ? "ARAM" : "CLASSIC");
    }
    static void ResetHistory() {
      foreach (string key in new[] { "_newestGameId", "_eogAnnouncedId", "_pendingTodayId", "_historyNewestAny", "_cachedNewestAny" })
        Set(typeof(LeagueStats), key, 0L);
      Set(typeof(LeagueStats), "_historyPages", new List<string>());
      Set(typeof(LeagueStats), "_historyDay", "");
      Set(typeof(LeagueStats), "_record", "");
      Set(typeof(LeagueStats), "_lastLine", "");
    }
    static void HistoryChecks() {
      ResetHistory();
      DateTime now = DateTime.UtcNow;
      int reads = 0;
      Func<int, string> read = delegate(int page) { reads++; return page == 0 ? Page(240, 20, "450") : Page(220, 5, "420"); };
      Equal(LeagueStats.RefreshHistory(read, now), 240L, "all-mode newest ID");
      Equal(reads, 2, "ARAM page does not block ranked pagination");
      Equal(Get(typeof(LeagueStats), "_record"), "W W W W W", "live orchestration ranked record");
      Equal(Get(typeof(LeagueStats), "_newestGameId"), 220L, "separate ranked ID");
      int[] w, l; LeagueStats.TodayParts(out w, out l);
      Equal(w[0] + l[0] + w[2] + l[2], 25, "all-mode day tally");
      reads = 0;
      LeagueStats.RefreshHistory(read, now.AddSeconds(5));
      Equal(reads, 1, "unchanged history reuses old pages");
      LeagueStats.RefreshHistory(delegate(int page) { return Page(239, 2, "450"); }, now.AddSeconds(10));
      Equal(Get(typeof(LeagueStats), "_historyNewestAny"), 240L, "stale history cannot rewind");

      ResetHistory(); reads = 0;
      LeagueStats.RefreshHistory(delegate(int page) { reads++; return Page(240, 2, "450"); }, now);
      LeagueStats.TodayParts(out w, out l);
      Equal(w[2] + l[2], 2, "non-ranked-only account tally");
      Equal(LeagueStats.Status, "live", "non-ranked history is valid");
      Equal(reads, 1, "short non-ranked page stops");
      ResetHistory();
      LeagueStats.RefreshHistory(delegate(int page) { return "{\"games\":{\"games\":[]}}"; }, now);
      Equal(LeagueStats.Status, "live", "empty account is valid");
      LeagueStats.TodayParts(out w, out l);
      Equal(w[0] + w[2] + l[0] + l[2], 0, "empty tally");

      ResetHistory(); reads = 0;
      LeagueStats.RefreshHistory(delegate(int page) { reads++; return page == 0 ? Page(240, 20, "450") : null; }, now);
      Equal(Get(typeof(LeagueStats), "_historyDay"), "", "failed deep read is not cached");
      reads = 0;
      LeagueStats.RefreshHistory(read, now.AddSeconds(10));
      Equal(reads, 2, "failed deep read retried on scheduled pass");
      Set(typeof(LeagueStats), "_newestGameId", 241L); // result arrived before history
      Set(typeof(LeagueStats), "_record", "fresh end-screen result");
      LeagueStats.RefreshHistory(read, now.AddSeconds(20));
      Equal(Get(typeof(LeagueStats), "_record"), "fresh end-screen result", "history does not undo end-screen result");
      ResetHistory(); reads = 0;
      Func<int, string> capped = delegate(int page) { reads++; return Page(1000 - page * 20, 20, "450"); };
      LeagueStats.RefreshHistory(capped, now);
      Equal(reads, 10, "history walk is capped");
      reads = 0;
      LeagueStats.RefreshHistory(capped, now.AddSeconds(5));
      Equal(reads, 1, "unchanged capped history does not repeat deep walk");
    }

    // Ephemeral certificate and loopback mock. No certificate store changes.
    sealed class Client : IDisposable {
      internal readonly TcpListener Listener = new TcpListener(IPAddress.Loopback, 0);
      internal volatile string Phase = "InProgress";
      internal volatile int Game = 10, Requests, Sessions, Ranks;
      internal string Identity = "self";
      readonly X509Certificate2 _certificate;
      readonly Thread _thread;
      internal readonly List<string> Errors = new List<string>();
      internal int Port { get { return ((IPEndPoint)Listener.LocalEndpoint).Port; } }
      internal Client() {
        using (RSA rsa = RSA.Create(2048)) {
          var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
          using (var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)))
            _certificate = new X509Certificate2(cert.Export(X509ContentType.Pfx), "", X509KeyStorageFlags.DefaultKeySet);
        }
        Listener.Start(); _thread = new Thread(Serve); _thread.IsBackground = true; _thread.Start();
      }
      void Serve() {
        while (true) {
          TcpClient client;
          try { client = Listener.AcceptTcpClient(); } catch (SocketException) { return; }
          using (client) using (var ssl = new SslStream(client.GetStream())) {
            try {
              ssl.ReadTimeout = ssl.WriteTimeout = 5000;
              ssl.AuthenticateAsServer(_certificate, false, SslProtocols.Tls12, false);
              var request = new StringBuilder();
              while (!request.ToString().EndsWith("\r\n\r\n")) { int b = ssl.ReadByte(); if (b < 0) break; request.Append((char)b); }
              string path = request.ToString().Split(' ')[1]; Requests++;
              string body = "{}";
              if (path.EndsWith("gameflow-phase")) body = TwitchChat.Qs(Phase);
              if (path == "/lol-gameflow/v1/session") { Sessions++; body = Roster(); }
              if (path == "/lol-summoner/v1/current-summoner") body = "{\"puuid\":\"" + Identity + "\",\"gameName\":\"Self\"}";
              if (path.StartsWith("/lol-ranked/")) { Ranks++; body = "{\"queues\":[]}"; }
              byte[] bytes = Encoding.UTF8.GetBytes(body);
              byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
              ssl.Write(head, 0, head.Length); ssl.Write(bytes, 0, bytes.Length); ssl.Flush();
            } catch (Exception ex) { lock (Errors) Errors.Add(ex.Message); }
          }
        }
      }
      string Roster() {
        var sides = new string[2];
        for (int side = 0; side < 2; side++) {
          var players = new List<string>();
          for (int i = 0; i < 5; i++) {
            string id = side == 0 && i == 0 ? Identity : "player" + side + i;
            players.Add("{\"puuid\":\"" + id + "\",\"gameName\":\"name" + side + i + "\"}");
          }
          sides[side] = "[" + string.Join(",", players.ToArray()) + "]";
        }
        return "{\"gameData\":{\"gameId\":" + Game + ",\"teamOne\":" + sides[0] + ",\"teamTwo\":" + sides[1] + "}}";
      }
      public void Dispose() { Listener.Stop(); _thread.Join(6000); _certificate.Dispose(); }
    }

    static void TransportAndRosterChecks() {
      using (var client = new Client()) {
        string phase = LeagueStats.LcuGet(client.Port, "test", "/lol-gameflow/v1/gameflow-phase");
        if (phase == null) throw new Exception("Mock handshake: " + Get(typeof(LeagueStats), "_lastHttpError")
          + " / " + string.Join("; ", client.Errors.ToArray()));
        LeagueStats.LcuGet(client.Port, "test", "/lol-gameflow/v1/gameflow-phase");
        Equal(client.Requests, 1, "phase reads coalesced");
        Call(typeof(LobbyRanks), "EnsureGameRoster", client.Port, "test", false);
        Equal(client.Sessions, 1, "first roster read");
        Equal(client.Ranks, 0, "ghost names do not fetch ranks");
        Call(typeof(LobbyRanks), "EnsureGameRoster", client.Port, "test", false);
        Equal(client.Sessions, 1, "complete roster reused");
        var readers = new Thread[2];
        Exception error = null;
        for (int i = 0; i < readers.Length; i++) {
          readers[i] = new Thread(delegate() {
            try { Call(typeof(LobbyRanks), "EnsureGameRoster", client.Port, "test", true); }
            catch (Exception ex) { error = ex; }
          }); readers[i].Start();
        }
        foreach (var thread in readers) if (!thread.Join(10000)) throw new Exception("Roster deadlock");
        if (error != null) throw error;
        Equal(client.Sessions, 1, "rank readers reuse verified roster");
        Equal(client.Ranks, 10, "concurrent rank readers share one build");
        client.Phase = "EndOfGame";
        Set(typeof(LeagueStats), "_phaseAtTicks", 0L);
        LeagueStats.LcuGet(client.Port, "test", "/lol-gameflow/v1/gameflow-phase");
        // A long observation gap forces match verification, then the final snapshot holds.
        Call(typeof(LobbyRanks), "EnsureGameRoster", client.Port, "test", false);
        int finalSessions = client.Sessions;
        Call(typeof(LobbyRanks), "EnsureGameRoster", client.Port, "test", false);
        Equal(client.Sessions, finalSessions, "post-game snapshot reused");
        client.Phase = "InProgress"; client.Game = 11;
        Set(typeof(LeagueStats), "_phaseAtTicks", 0L);
        LeagueStats.LcuGet(client.Port, "test", "/lol-gameflow/v1/gameflow-phase");
        Call(typeof(LobbyRanks), "EnsureGameRoster", client.Port, "test", false);
        Equal(Get(typeof(LobbyRanks), "_gameId"), 11L, "next game invalidates roster");
        Call(typeof(LeagueStats), "RefreshIdentity", client.Port, "test", false);
        Set(typeof(LeagueStats), "_newestGameId", 999L);
        client.Identity = "other";
        Call(typeof(LeagueStats), "RefreshIdentity", client.Port, "test", false);
        Equal(Get(typeof(LeagueStats), "_newestGameId"), 0L, "account change clears ranked cursor");
        Equal(Get(typeof(LeagueStats), "_historyNewestAny"), 0L, "account change clears all-mode cursor");
        Set(typeof(LeagueStats), "_phaseNow", "InProgress");
        Set(typeof(LeagueStats), "_phaseAtTicks", DateTime.UtcNow.Ticks);
        Equal(LeagueStats.BusyWithGame(), true, "fresh match defers automatic updates");
        Set(typeof(LeagueStats), "_phaseAtTicks", DateTime.UtcNow.AddMinutes(-2).Ticks);
        Equal(LeagueStats.BusyWithGame(), false, "stale phase cannot defer updates forever");
        Equal(client.Errors.Count, 0, "mock transport errors");
      }
    }

    static void HeaderChecks() {
      foreach (string page in new[] { "/app", "/help/", "/control", "/layouts", "/customize/", "/alerts", "/stats" })
        Equal(Call(typeof(Program), "IsEmbeddedPage", page), true, "embedded page " + page);
      Equal(Call(typeof(Program), "SameOriginRequest", "GET /features/set HTTP/1.1\r\nSec-Fetch-Site: cross-site\r\n\r\n"), false, "cross-site feature toggle blocked");
      // Read the actual response writer without starting the application server.
      var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
      using (var receiver = new TcpClient()) {
        receiver.Connect((IPEndPoint)listener.LocalEndpoint);
        using (var sender = listener.AcceptTcpClient()) {
          Call(typeof(Program), "SendPrivate", sender.GetStream(), 200, "text/plain", Encoding.UTF8.GetBytes("ok"), "max-age=60");
          sender.Close();
          string response = new System.IO.StreamReader(receiver.GetStream()).ReadToEnd();
          Equal(response.Contains("Cache-Control: max-age=60\r\n"), true, "cache override preserved");
          Equal(response.Contains("X-Content-Type-Options: nosniff\r\n"), true, "nosniff preserved");
          Equal(response.Contains("Access-Control-Allow-Origin"), false, "no wildcard CORS");
        }
      }
      listener.Stop();
    }
  }
}
