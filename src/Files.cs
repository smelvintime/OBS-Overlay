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
      string tmp = path + "." + System.Guid.NewGuid().ToString("N") + ".tmp";
      try {
        File.WriteAllText(tmp, text);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
      } catch {
        // Never leave scratch behind for a write that did not land - the
        // folder is one the user is told to open from the tray menu.
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        throw;
      }
    }
  }
}
