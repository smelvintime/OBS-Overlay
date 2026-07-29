// A rolling text log for the one class of bug that otherwise leaves no trace:
// the process dying between one user action and the next, with nothing in the
// Windows event log because the crash was a plain unhandled managed exception
// rather than a fault Windows itself notices. Every branch here has to be safe
// to call from inside a crash handler, which is why every write is wrapped -
// logging must never become a second reason the process goes down.

using System;
using System.IO;

namespace NowPlaying {

  static class AppLog {

    static readonly object _lock = new object();
    const long MaxBytes = 512 * 1024;
    const int KeepLines = 1500;   // trimmed target once MaxBytes is exceeded

    static string Path_() {
      string dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NowPlayingOverlay");
      Directory.CreateDirectory(dir);
      return System.IO.Path.Combine(dir, "app.log");
    }

    public static void Write(string line) {
      try {
        lock (_lock) {
          string p = Path_();
          var fi = new FileInfo(p);
          if (fi.Exists && fi.Length > MaxBytes) Trim(p);
          File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + "\r\n");
        }
      } catch { }
    }

    static void Trim(string p) {
      try {
        var lines = File.ReadAllLines(p);
        if (lines.Length <= KeepLines) return;
        var keep = new string[KeepLines];
        Array.Copy(lines, lines.Length - KeepLines, keep, 0, KeepLines);
        File.WriteAllLines(p, keep);
      } catch { }
    }
  }
}
