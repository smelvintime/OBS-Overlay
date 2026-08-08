// The one way this app writes a file it intends to read back.
//
// Truncate-then-write (File.WriteAllText) dies at the worst possible moment:
// a crash or power cut between the truncate and the write leaves an empty or
// half-written file. For most of what the app saves that is an annoyance; for
// twitch-config.json - rewritten automatically at every token refresh, so
// every few hours for as long as the app runs - it means the client's Twitch
// sign-in is gone and the setup wizard has to be run again, weeks after
// anyone touched anything. Writing a sibling temp file and swapping it in
// makes the switch a single atomic rename: the file on disk is always either
// the old complete content or the new complete content, never neither.

using System.IO;

namespace NowPlaying {

  static class Files {

    // Same volume by construction (the temp sits beside the target), which is
    // what File.Replace needs to stay atomic.
    //
    // The temp name carries a GUID rather than being a flat "<path>.tmp".
    // Eighteen call sites reach this from HTTP request threads and background
    // timers, several of them writing the same file, and a shared scratch name
    // makes two concurrent writers fight over one handle: the first one's
    // File.Replace needs delete access to a file the second has just opened
    // with FileMode.Create, so it throws IOException - which every caller
    // swallows. The write is reported as done and the setting reverts at the
    // next restart. Distinct temps mean the worst case is a harmless
    // last-writer-wins instead of a silently lost save.
    internal static void WriteAtomic(string path, string text) {
      Swap(path, delegate(string tmp) { File.WriteAllText(tmp, text); });
    }

    // Same guarantee for bytes. Uploaded alert media comes through here: a clip
    // half-written by a cancelled upload would sit in the folder looking like a
    // real file, get picked in the customizer, and fail to render on stream.
    internal static void WriteAtomic(string path, byte[] bytes) {
      Swap(path, delegate(string tmp) { File.WriteAllBytes(tmp, bytes); });
    }

    static void Swap(string path, System.Action<string> write) {
      string tmp = path + "." + System.Guid.NewGuid().ToString("N") + ".tmp";
      SweepStale(path);
      try {
        write(tmp);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
      } catch {
        // Never leave scratch behind for a write that did not land - the
        // folder is one the user is told to open from the tray menu.
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        throw;
      }
    }

    // The cost of the GUID above: where a single fixed ".tmp" name left one
    // stale file that the next write simply reused, a unique one leaves a new
    // one behind every time the process dies mid-write - and build.ps1 force-
    // kills the running overlay on every rebuild.
    //
    // Scoped to the one file being written, NOT to the directory. Sweeping
    // "*.tmp" across a directory would be the thorough version, but the
    // directory is not always ours: twitch-config.json is looked up from the
    // exe folder and as much as four levels above it (FindConfig in
    // TwitchEvents.cs), which can be somebody's Downloads folder or a drive
    // root. Deleting another program's scratch files is not a tidy-up.
    //
    // So the honest description is: each file reclaims its own orphans the
    // next time it is written. A file that is rarely saved keeps its stale
    // temp until it next is. They are inert either way - nothing enumerates
    // by ".tmp", and the themes listing filters on ".json".
    //
    // An hour old, so this can never race a write that is genuinely in flight
    // on another thread. Best-effort throughout: a failure to tidy up must
    // never be the reason a save does not happen.
    static void SweepStale(string path) {
      try {
        string dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return;
        var cutoff = System.DateTime.UtcNow.AddHours(-1);
        foreach (var f in Directory.GetFiles(dir, Path.GetFileName(path) + ".*.tmp")) {
          // Re-checked in managed code: a Win32 wildcard also matches against
          // a file's short 8.3 name, so "*.tmp" can return names that do not
          // actually end in .tmp.
          if (!string.Equals(Path.GetExtension(f), ".tmp",
                             System.StringComparison.OrdinalIgnoreCase)) continue;
          try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); } catch { }
        }
      } catch { }
    }
  }
}
