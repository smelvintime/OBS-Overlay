// First-run "this shouldn't live in Downloads" nudge.
//
// The problem is real: people download a program, run it from Downloads, and
// later clear that folder out - taking the thing OBS points at with it. The
// obvious fix is for the app to copy itself somewhere permanent, and that is
// what this file used to do.
//
// It cannot. An unsigned executable that writes another executable to disk and
// registers it for startup is, to a machine-learning malware classifier, a
// dropper - there is no way to look different while doing the same thing.
// Defender flagged our release as Trojan:Win32/Wacatac.C!ml and deleted it on
// sight. Removing the self-copy code from the binary entirely is what cleared
// it; nothing subtler worked, because the verdict comes from the code being
// present, not from it running.
//
// So the app no longer moves itself. It offers to set the destination up and
// opens both folders so the move is one drag in Explorer - Windows' own file
// operation, performed by the user, which no classifier objects to. Once the
// app is somewhere permanent it offers shortcuts and startup instead, both of
// which are ordinary .lnk and registry writes that were never the problem.
//
// If this project ever gets a code-signing certificate, the automatic version
// is worth restoring; see git history for it.

using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace NowPlaying {

  static class Installer {

    // Records that the question has been asked, so nobody is nagged twice.
    static string MarkerPath() {
      string dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NowPlayingOverlay");
      Directory.CreateDirectory(dir);
      return Path.Combine(dir, "installed.txt");
    }

    static string ExeDir() {
      return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    }

    // Working from a clone: the exe sits in dist\ with the sources around it.
    // Suggesting a move there would be nonsense and would fire for every
    // developer on their first build.
    static bool IsDevTree() {
      try {
        string dir = ExeDir();
        for (int i = 0; i < 3 && dir != null; i++) {
          if (File.Exists(Path.Combine(dir, "build.ps1"))
              && Directory.Exists(Path.Combine(dir, "src"))) return true;
          var parent = Directory.GetParent(dir);
          dir = parent == null ? null : parent.FullName;
        }
      } catch { }
      return false;
    }

    // Only nag about the folders people actually clear out. Anywhere else is a
    // deliberate choice and none of our business.
    static bool InTransientFolder() {
      try {
        string here = Path.GetFullPath(ExeDir()).TrimEnd('\\');
        string[] transient = {
          Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
          Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
          Path.GetTempPath()
        };
        foreach (var t in transient) {
          if (string.IsNullOrEmpty(t)) continue;
          string root = Path.GetFullPath(t).TrimEnd('\\');
          // The folder itself or anything inside it. Unzipping into
          // Downloads\overlay\ is the same problem as sitting in Downloads,
          // and an exact match would miss every one of those.
          if (here.Equals(root, StringComparison.OrdinalIgnoreCase)
              || here.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            return true;
        }
      } catch { }
      return false;
    }

    static string DefaultTarget() {
      // Per-user Programs: where an app that needs no admin rights belongs.
      return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "NowPlayingOverlay");
    }

    /// <summary>
    /// Runs at most once, on first launch. Always returns false - this process
    /// keeps running whatever the answer - but the signature is kept so the
    /// caller can stop if that ever changes again.
    /// </summary>
    public static bool MaybeOffer() {
      try {
        if (File.Exists(MarkerPath())) return false;
        if (IsDevTree()) return false;

        // Somewhere permanent already: skip the move talk and just offer the
        // conveniences, which is all most people wanted anyway.
        if (!InTransientFolder()) { OfferShortcuts(); return false; }

        AskToMove();
        try { File.WriteAllText(MarkerPath(), Assembly.GetExecutingAssembly().Location); } catch { }
        return false;
      } catch (Exception ex) {
        AppLog.Write("first-run prompt failed (continuing): " + ex.Message);
        try { File.WriteAllText(MarkerPath(), "failed"); } catch { }
        return false;
      }
    }

    // ---------------------------------------------------------------- the move
    static void AskToMove() {
      string target = DefaultTarget();
      string src = Assembly.GetExecutingAssembly().Location;

      var f = new Form {
        Text = "Move Now Playing Overlay somewhere permanent",
        FormBorderStyle = FormBorderStyle.FixedDialog,
        StartPosition = FormStartPosition.CenterScreen,
        MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = true,
        ClientSize = new Size(520, 250),
        Font = new Font("Segoe UI", 9F)
      };

      var head = new Label {
        Text = "You're running this from " + FriendlyFolderName() + ".",
        Font = new Font("Segoe UI", 12F, FontStyle.Bold),
        Location = new Point(16, 14), Size = new Size(488, 24)
      };
      var blurb = new Label {
        Text = "That folder gets cleared out, and if this file disappears your OBS overlays "
             + "go blank. It's one self-contained file, so moving it is all it takes - nothing "
             + "else needs to come along.\r\n\r\n"
             + "The button below creates the folder and opens both windows, so you can drag "
             + "the file across. Then run it from its new home.",
        Location = new Point(16, 42), Size = new Size(488, 92),
        ForeColor = Color.FromArgb(80, 80, 80)
      };
      var pathLbl = new Label {
        Text = "Suggested: " + target,
        Location = new Point(16, 140), Size = new Size(488, 20),
        ForeColor = Color.FromArgb(50, 50, 50)
      };

      var pick = new Button { Text = "Choose a different folder...", Location = new Point(16, 166),
                              Size = new Size(178, 26) };
      pick.Click += (s, e) => {
        using (var d = new FolderBrowserDialog()) {
          d.Description = "Where should Now Playing Overlay live?";
          d.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
          if (d.ShowDialog(f) == DialogResult.OK) {
            target = Path.Combine(d.SelectedPath, "NowPlayingOverlay");
            pathLbl.Text = "Suggested: " + target;
          }
        }
      };

      var go = new Button { Text = "Open both folders", Location = new Point(286, 206),
                            Size = new Size(128, 28), DialogResult = DialogResult.OK };
      var later = new Button { Text = "Not now", Location = new Point(422, 206),
                               Size = new Size(82, 28), DialogResult = DialogResult.Cancel };
      f.AcceptButton = go; f.CancelButton = later;
      f.Controls.AddRange(new Control[] { head, blurb, pathLbl, pick, go, later });

      bool move = f.ShowDialog() == DialogResult.OK;
      f.Dispose();
      if (!move) return;

      try {
        Directory.CreateDirectory(target);
        // The destination, then the source with the file already selected, so
        // the drag is between two windows that are both already open. Windows
        // performs the copy; this app never writes an executable anywhere.
        System.Diagnostics.Process.Start("explorer.exe", "\"" + target + "\"");
        System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + src + "\"");
        AppLog.Write("first run: opened " + target + " for a manual move");
      } catch (Exception ex) {
        MessageBox.Show("Could not open that folder:\r\n\r\n" + ex.Message,
                        "Now Playing Overlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    static string FriendlyFolderName() {
      try {
        string here = Path.GetFullPath(ExeDir()).TrimEnd('\\');
        if (here.Equals(Path.GetFullPath(Path.Combine(
              Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")).TrimEnd('\\'),
              StringComparison.OrdinalIgnoreCase)) return "your Downloads folder";
        if (here.Equals(Path.GetFullPath(
              Environment.GetFolderPath(Environment.SpecialFolder.Desktop)).TrimEnd('\\'),
              StringComparison.OrdinalIgnoreCase)) return "your Desktop";
        return "a temporary folder";
      } catch { return "a temporary folder"; }
    }

    // ----------------------------------------------------------- conveniences
    // Offered once the app is somewhere it will stay. A .lnk and an HKCU Run
    // value are what every installer writes; neither is what got us flagged.
    static void OfferShortcuts() {
      string exe = Assembly.GetExecutingAssembly().Location;

      var f = new Form {
        Text = "Now Playing Overlay",
        FormBorderStyle = FormBorderStyle.FixedDialog,
        StartPosition = FormStartPosition.CenterScreen,
        MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = true,
        ClientSize = new Size(470, 196),
        Font = new Font("Segoe UI", 9F)
      };
      var head = new Label {
        Text = "Shortcuts?",
        Font = new Font("Segoe UI", 12F, FontStyle.Bold),
        Location = new Point(16, 14), Size = new Size(438, 24)
      };
      var blurb = new Label {
        Text = "It runs in the system tray with no window, so a shortcut is the easiest way "
             + "to start it again later.",
        Location = new Point(16, 40), Size = new Size(438, 36),
        ForeColor = Color.FromArgb(80, 80, 80)
      };
      var cbDesktop = new CheckBox { Text = "Put a shortcut on my desktop", Checked = true,
                                     Location = new Point(16, 82), Size = new Size(438, 22) };
      var cbStart = new CheckBox { Text = "Add it to the Start menu", Checked = true,
                                   Location = new Point(16, 106), Size = new Size(438, 22) };
      var cbAuto = new CheckBox { Text = "Start automatically when I sign in to Windows",
                                  Checked = false, Location = new Point(16, 130), Size = new Size(438, 22) };
      var ok = new Button { Text = "Done", Location = new Point(280, 156), Size = new Size(80, 28),
                            DialogResult = DialogResult.OK };
      var skip = new Button { Text = "No thanks", Location = new Point(368, 156), Size = new Size(86, 28),
                              DialogResult = DialogResult.Cancel };
      f.AcceptButton = ok; f.CancelButton = skip;
      f.Controls.AddRange(new Control[] { head, blurb, cbDesktop, cbStart, cbAuto, ok, skip });

      bool apply = f.ShowDialog() == DialogResult.OK;
      bool d1 = cbDesktop.Checked, s1 = cbStart.Checked, a1 = cbAuto.Checked;
      f.Dispose();
      try { File.WriteAllText(MarkerPath(), exe); } catch { }
      if (!apply) return;

      Shortcuts(exe, d1, s1);
      if (a1) Program.SetStartupPublic(true);
    }

    // WScript.Shell is on every Windows and reachable late-bound, so this needs
    // no COM reference and cannot break the build.
    static void Shortcuts(string exePath, bool desktop, bool startMenu) {
      if (!desktop && !startMenu) return;
      try {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return;
        dynamic shell = Activator.CreateInstance(t);
        if (desktop)
          Make(shell, exePath, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Now Playing Overlay.lnk"));
        if (startMenu) {
          string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Now Playing Overlay");
          Directory.CreateDirectory(dir);
          Make(shell, exePath, Path.Combine(dir, "Now Playing Overlay.lnk"));
        }
      } catch (Exception ex) {
        AppLog.Write("could not create shortcuts (not fatal): " + ex.Message);
      }
    }

    static void Make(dynamic shell, string exePath, string lnkPath) {
      try {
        dynamic sc = shell.CreateShortcut(lnkPath);
        sc.TargetPath = exePath;
        sc.WorkingDirectory = Path.GetDirectoryName(exePath);
        sc.Description = "Now Playing overlay for OBS";
        sc.Save();
      } catch { }
    }
  }
}
