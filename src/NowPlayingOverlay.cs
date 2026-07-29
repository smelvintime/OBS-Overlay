// Now Playing overlay - standalone Windows executable.
//
// Serves a local web page showing whatever music is playing on this PC, for use
// as an OBS Browser Source. Reads two sources and picks whichever is actually
// playing:
//
//   1. Windows media session (SMTC) - Spotify, Apple Music, browsers, most apps
//   2. iTunes COM automation        - iTunes does not publish to SMTC at all
//
// Requires nothing installed: it targets the .NET Framework that ships with
// Windows, and the overlay pages are embedded in the binary.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace NowPlaying {

  // Immutable view of "what is playing", swapped in wholesale by the poller so
  // request handlers never see a half-updated state.
  sealed class Snapshot {
    public bool Playing;
    public string Title = "";
    public string Artist = "";
    public string Album = "";
    public string App = "";
    public string Source = "none";
    public string Id = "none";
    public bool HasArt;
    public byte[] Art;
    public string ArtMime = "image/jpeg";
  }

  static class Program {

    // ------------------------------------------------------------------ state
    static volatile Snapshot _current = new Snapshot();
    static int _port = 8787;

    // The media APIs (WinRT session manager, iTunes COM) are single shared
    // objects. The poller touches them every second and /sources can touch them
    // from a request thread, so all provider access is serialised through this.
    static readonly object _mediaLock = new object();

    // .NET Framework's AsTask() lives in System.Runtime.WindowsRuntime, which
    // needs the union Windows.winmd from the Windows SDK. Driving the completion
    // handler directly works off the per-namespace winmd files alone, so this
    // builds on a machine with no SDK installed.
    // Must run on an MTA thread: on an STA thread the completion callback would
    // marshal back to the very thread we are blocking, and deadlock.
    static TResult Await<TResult>(IAsyncOperation<TResult> op) {
      var done = new ManualResetEventSlim(false);
      op.Completed = (o, s) => done.Set();
      done.Wait();
      return op.GetResults();
    }

    // ------------------------------------------------------------------ entry
    static int Main(string[] args) {
      for (int i = 0; i < args.Length; i++) {
        var a = args[i].TrimStart('-', '/').ToLowerInvariant();
        if ((a == "port" || a == "p") && i + 1 < args.Length) {
          int p; if (int.TryParse(args[i + 1], out p)) { _port = p; i++; }
        } else if (a == "help" || a == "h" || a == "?") {
          Console.WriteLine("Usage: NowPlayingOverlay.exe [-port 8787]");
          return 0;
        } else {
          int p; if (int.TryParse(a, out p)) _port = p;
        }
      }

      TcpListener listener;
      try {
        listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
      } catch (SocketException ex) {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine();
        Console.WriteLine("  Could not open port " + _port + ".");
        Console.WriteLine("  " + ex.Message);
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("  Another copy may already be running. Try a different port:");
        Console.WriteLine("      NowPlayingOverlay.exe -port " + (_port + 1));
        Console.WriteLine("  (then use that same port in the OBS Browser Source URL)");
        Console.WriteLine();
        Console.WriteLine("  Press any key to close.");
        try { Console.ReadKey(true); } catch { }
        return 1;
      }

      var poller = new Thread(PollLoop);
      poller.IsBackground = true;
      poller.Start();          // left MTA on purpose - see Await()

      Banner();

      while (true) {
        TcpClient client;
        try { client = listener.AcceptTcpClient(); } catch { break; }
        ThreadPool.QueueUserWorkItem(_ => Handle(client));
      }
      return 0;
    }

    static void Banner() {
      Console.Title = "Now Playing Overlay";
      Console.WriteLine();
      Console.ForegroundColor = ConsoleColor.Green;
      Console.WriteLine("  Now Playing overlay is running.");
      Console.ResetColor();
      Console.WriteLine();
      Console.ForegroundColor = ConsoleColor.Cyan;
      Console.WriteLine("    Add this as an OBS Browser Source:");
      Console.WriteLine("        http://127.0.0.1:" + _port + "/");
      Console.WriteLine();
      Console.WriteLine("    Compare the layouts here:");
      Console.WriteLine("        http://127.0.0.1:" + _port + "/layouts");
      Console.ResetColor();
      Console.WriteLine();
      Console.ForegroundColor = ConsoleColor.DarkGray;
      Console.WriteLine("    Sources: Windows media session + iTunes");
      Console.WriteLine("    Keep this window open while streaming. Ctrl+C to stop.");
      Console.ResetColor();
      Console.WriteLine();
    }

    // ----------------------------------------------------------------- poller
    // All WinRT/COM work happens here on one thread, so request handlers stay
    // fast and there is no cross-thread contention over the media APIs.
    static void PollLoop() {
      while (true) {
        try {
          Snapshot snap;
          lock (_mediaLock) {
            snap = Build();
            var prev = _current;
            // Only refetch art when the track actually changes - it is the
            // expensive part and it does not change within a track.
            if (snap.Id == prev.Id && prev.Art != null) {
              snap.Art = prev.Art;
              snap.ArtMime = prev.ArtMime;
            } else if (snap.HasArt) {
              byte[] art = (snap.Source == "itunes") ? ITunesArt() : SmtcArt();
              if (art != null && art.Length > 0) {
                snap.Art = art;
                snap.ArtMime = SniffMime(art);
              } else {
                snap.HasArt = false;
              }
            }
          }
          _current = snap;
        } catch {
          // never let a transient media-API failure kill the poller
        }
        Thread.Sleep(1000);
      }
    }

    static Snapshot Build() {
      var list = new List<Snapshot>();
      var smtc = SmtcRead();
      if (smtc != null) list.Add(smtc);
      var itunes = ITunesRead();
      if (itunes != null) list.Add(itunes);
      if (list.Count == 0) return new Snapshot();

      // Something actively playing always beats something idle, so a paused
      // iTunes never steals the overlay from a playing Spotify, or the reverse.
      Snapshot best = null; int bestScore = -1;
      foreach (var c in list) {
        int score = 0;
        if (c.Playing) score += 4;
        if (c.Source == "itunes" || IsMusicApp(c.App)) score += 1;
        if (score > bestScore) { bestScore = score; best = c; }
      }
      return best;
    }

    static readonly string[] MusicHints =
      { "spotify", "apple", "itunes", "music", "tidal", "deezer" };

    static bool IsMusicApp(string aumid) {
      if (string.IsNullOrEmpty(aumid)) return false;
      var l = aumid.ToLowerInvariant();
      foreach (var h in MusicHints) if (l.Contains(h)) return true;
      return false;
    }

    static string MakeId(string t, string a, string al) {
      if (string.IsNullOrEmpty(t)) return "none";
      // FNV-1a: deterministic across runs, unlike string.GetHashCode()
      unchecked {
        uint hash = 2166136261;
        foreach (char c in (t + "|" + a + "|" + al)) { hash ^= c; hash *= 16777619; }
        return hash.ToString();
      }
    }

    // ------------------------------------------------- source 1: media session
    static GlobalSystemMediaTransportControlsSessionManager _mgr;

    static GlobalSystemMediaTransportControlsSessionManager Manager() {
      if (_mgr == null)
        _mgr = Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
      return _mgr;
    }

    static GlobalSystemMediaTransportControlsSession BestSession() {
      var mgr = Manager();
      if (mgr == null) return null;
      var sessions = mgr.GetSessions();
      if (sessions == null || sessions.Count == 0) return null;
      GlobalSystemMediaTransportControlsSession best = null; int bestScore = -1;
      foreach (var s in sessions) {
        int score = 0;
        try {
          if (s.GetPlaybackInfo().PlaybackStatus ==
              GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) score += 2;
        } catch { }
        if (IsMusicApp(s.SourceAppUserModelId)) score += 1;
        if (score > bestScore) { bestScore = score; best = s; }
      }
      return best ?? mgr.GetCurrentSession();
    }

    static Snapshot SmtcRead() {
      try {
        var s = BestSession();
        if (s == null) return null;
        var p = Await(s.TryGetMediaPropertiesAsync());
        if (p == null || string.IsNullOrEmpty(p.Title)) return null;
        bool playing = s.GetPlaybackInfo().PlaybackStatus ==
                       GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        return new Snapshot {
          Playing = playing,
          Title = p.Title ?? "",
          Artist = p.Artist ?? "",
          Album = p.AlbumTitle ?? "",
          App = s.SourceAppUserModelId ?? "",
          Source = "smtc",
          HasArt = p.Thumbnail != null,
          Id = MakeId(p.Title, p.Artist, p.AlbumTitle)
        };
      } catch {
        _mgr = null;      // rebuild the manager next time round
        return null;
      }
    }

    static byte[] SmtcArt() {
      try {
        var s = BestSession();
        if (s == null) return null;
        var p = Await(s.TryGetMediaPropertiesAsync());
        if (p == null || p.Thumbnail == null) return null;
        var stream = Await(p.Thumbnail.OpenReadAsync());
        uint size = (uint)stream.Size;
        if (size == 0) return null;
        var reader = new DataReader(stream);
        Await(reader.LoadAsync(size));
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
      } catch { return null; }
    }

    // -------------------------------------------------------- source 2: iTunes
    // Creating the iTunes COM object LAUNCHES iTunes, so every entry point gates
    // on the process already running. The overlay must never pop iTunes open.
    static object _itunes;

    static void DropITunes() {
      if (_itunes != null) {
        try { Marshal.ReleaseComObject(_itunes); } catch { }
        _itunes = null;
      }
    }

    static bool ITunesRunning() {
      try { return System.Diagnostics.Process.GetProcessesByName("iTunes").Length > 0; }
      catch { return false; }
    }

    static dynamic ITunesApp() {
      if (!ITunesRunning()) { DropITunes(); return null; }
      if (_itunes != null) {
        try { var probe = ((dynamic)_itunes).PlayerState; return (dynamic)_itunes; }
        catch { DropITunes(); }        // stale RPC handle, rebuild below
      }
      try {
        Type t = Type.GetTypeFromProgID("iTunes.Application");
        if (t == null) return null;
        _itunes = Activator.CreateInstance(t);
        return (dynamic)_itunes;
      } catch { return null; }
    }

    static Snapshot ITunesRead() {
      try {
        dynamic app = ITunesApp();
        if (app == null) return null;
        dynamic track = app.CurrentTrack;
        if (track == null) return null;      // stopped, or nothing loaded
        int state = (int)app.PlayerState;    // 1 = playing; 0 = paused/stopped
        string name = (string)track.Name ?? "";
        string artist = "";
        string album = "";
        try { artist = (string)track.Artist ?? ""; } catch { }
        try { album = (string)track.Album ?? ""; } catch { }
        bool hasArt = false;
        try { hasArt = ((int)track.Artwork.Count) > 0; } catch { }
        if (string.IsNullOrEmpty(name)) return null;
        return new Snapshot {
          Playing = (state == 1),
          Title = name, Artist = artist, Album = album,
          App = "iTunes", Source = "itunes",
          HasArt = hasArt, Id = MakeId(name, artist, album)
        };
      } catch { DropITunes(); return null; }
    }

    static byte[] ITunesArt() {
      string tmp = null;
      try {
        dynamic app = ITunesApp();
        if (app == null) return null;
        dynamic track = app.CurrentTrack;
        if (track == null) return null;
        dynamic art = track.Artwork;
        if (art == null || (int)art.Count < 1) return null;
        dynamic item = art.Item(1);
        int fmt = 1;
        try { fmt = (int)item.Format; } catch { }   // 1 JPEG, 2 PNG, 3 BMP
        string ext = fmt == 2 ? ".png" : fmt == 3 ? ".bmp" : ".jpg";
        tmp = Path.Combine(Path.GetTempPath(), "np-art-" + Guid.NewGuid().ToString("N") + ext);
        item.SaveArtworkToFile(tmp);
        if (!File.Exists(tmp)) return null;
        return File.ReadAllBytes(tmp);
      } catch {
        return null;
      } finally {
        try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
      }
    }

    static string SniffMime(byte[] b) {
      if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
      if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50) return "image/png";
      if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return "image/bmp";
      return "application/octet-stream";
    }

    // ------------------------------------------------------------------- HTTP
    static void Handle(TcpClient client) {
      try {
        using (client)
        using (var ns = client.GetStream()) {
          ns.ReadTimeout = 4000;
          var buf = new byte[4096];
          int read = ns.Read(buf, 0, buf.Length);
          if (read <= 0) return;
          string req = Encoding.ASCII.GetString(buf, 0, read);

          string path = "/";
          int sp1 = req.IndexOf(' ');
          if (sp1 > 0) {
            int sp2 = req.IndexOf(' ', sp1 + 1);
            if (sp2 > sp1) path = req.Substring(sp1 + 1, sp2 - sp1 - 1);
          }
          string route = path.Split('?')[0];
          var snap = _current;

          if (route == "/np") {
            Send(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Json(snap)));
          } else if (route == "/art") {
            if (snap.Art != null && snap.Art.Length > 0) Send(ns, 200, snap.ArtMime, snap.Art);
            else Send(ns, 204, "text/plain", new byte[0]);
          } else if (route == "/sources") {
            // Diagnostic: what each provider reports, and which one won.
            var sb = new StringBuilder();
            Snapshot smtcNow, itunesNow; bool itRunning;
            lock (_mediaLock) {
              itRunning = ITunesRunning();
              smtcNow = SmtcRead();
              itunesNow = ITunesRead();
            }
            sb.Append("{\"chosen\":").Append(Q(snap.Source)).Append(',');
            sb.Append("\"itunesRunning\":").Append(itRunning ? "true" : "false").Append(',');
            sb.Append("\"providers\":[");
            sb.Append("{\"name\":\"smtc\",\"found\":").Append(smtcNow != null ? "true" : "false");
            if (smtcNow != null) sb.Append(",\"track\":").Append(Json(smtcNow));
            sb.Append("},{\"name\":\"itunes\",\"found\":").Append(itunesNow != null ? "true" : "false");
            if (itunesNow != null) sb.Append(",\"track\":").Append(Json(itunesNow));
            sb.Append("}]}");
            Send(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(sb.ToString()));
          } else if (route == "/layouts" || route == "/layouts/") {
            SendResource(ns, "layouts.html");
          } else if (route == "/") {
            SendResource(ns, "overlay.html");
          } else {
            Send(ns, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
          }
        }
      } catch {
        // a dropped browser connection must not take the server down
      }
    }

    static void SendResource(NetworkStream ns, string name) {
      var asm = Assembly.GetExecutingAssembly();
      using (var s = asm.GetManifestResourceStream(name)) {
        if (s == null) {
          Send(ns, 404, "text/plain", Encoding.UTF8.GetBytes(name + " missing from executable"));
          return;
        }
        using (var ms = new MemoryStream()) {
          s.CopyTo(ms);
          Send(ns, 200, "text/html; charset=utf-8", ms.ToArray());
        }
      }
    }

    static void Send(NetworkStream ns, int code, string contentType, byte[] body) {
      string status = code == 200 ? "OK" : code == 204 ? "No Content"
                    : code == 404 ? "Not Found" : "Error";
      var sb = new StringBuilder();
      sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(status).Append("\r\n");
      sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
      sb.Append("Content-Length: ").Append(body == null ? 0 : body.Length).Append("\r\n");
      sb.Append("Access-Control-Allow-Origin: *\r\n");
      sb.Append("Cache-Control: no-cache, no-store, must-revalidate\r\n");
      sb.Append("Connection: close\r\n\r\n");
      var head = Encoding.ASCII.GetBytes(sb.ToString());
      ns.Write(head, 0, head.Length);
      if (body != null && body.Length > 0) ns.Write(body, 0, body.Length);
      ns.Flush();
    }

    static string Json(Snapshot s) {
      var sb = new StringBuilder();
      sb.Append('{');
      sb.Append("\"playing\":").Append(s.Playing ? "true" : "false").Append(',');
      sb.Append("\"title\":").Append(Q(s.Title)).Append(',');
      sb.Append("\"artist\":").Append(Q(s.Artist)).Append(',');
      sb.Append("\"album\":").Append(Q(s.Album)).Append(',');
      sb.Append("\"app\":").Append(Q(s.App)).Append(',');
      sb.Append("\"source\":").Append(Q(s.Source)).Append(',');
      sb.Append("\"id\":").Append(Q(s.Id)).Append(',');
      // The poller clears HasArt if the art fetch fails, so on a published
      // snapshot this already implies the bytes are present.
      sb.Append("\"hasArt\":").Append(s.HasArt ? "true" : "false");
      sb.Append('}');
      return sb.ToString();
    }

    // Song titles legitimately contain quotes, backslashes and emoji, so escape
    // properly rather than trusting the input.
    static string Q(string s) {
      if (s == null) return "\"\"";
      var sb = new StringBuilder("\"");
      foreach (char c in s) {
        switch (c) {
          case '"': sb.Append("\\\""); break;
          case '\\': sb.Append("\\\\"); break;
          case '\n': sb.Append("\\n"); break;
          case '\r': sb.Append("\\r"); break;
          case '\t': sb.Append("\\t"); break;
          default:
            if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
            break;
        }
      }
      return sb.Append('"').ToString();
    }
  }
}
