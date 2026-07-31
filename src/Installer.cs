// First-run "where should this live?" step.
//
// The exe is self-contained - every page is an embedded resource - so
// installing it is genuinely just "put this one file somewhere sensible and
// make it findable". There is no MSI, no admin prompt and nothing written
// outside the user's own profile unless they choose a path themselves.
//
// It exists because downloading a program and running it from the Downloads
// folder is the normal thing for a person to do, and then the folder gets
// cleaned out, or the file is one of forty things called something similar,
// and the overlay OBS points at is gone. Asking once, on the very first run,
// turns that into a deliberate choice.
//
// Deliberately not an installer in the traditional sense: no uninstaller entry,
// no registry beyond the startup key the tray already owns, no services. The
// whole thing is a file copy plus two shortcuts, and uninstalling is deleting
// the folder.

using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace NowPlaying {

  static class Installer {

    // One line: the path the user picked. Its presence is also the "we have
    // asked, never ask again" flag - being nagged by a program you have already
    // told what to do is worse than the problem this solves.
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

    // Working from a clone rather than a download: the exe sits in dist\ with
    // the sources beside or above it. Offering to "install" there would be
    // nonsense, and would fire on every developer's first run.
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

    static string DefaultTarget() {
      // Per-user Programs: the modern home for an app that needs no admin
      // rights. Program Files would demand elevation for a file copy.
      return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "NowPlayingOverlay");
    }

    /// <summary>
    /// Offers to install on the very first run. Always returns false - this
    /// process carries on running whatever the answer - but the return value is
    /// kept so the caller can stop if that ever changes.
    /// </summary>
    public static bool MaybeOffer() {
      try {
        if (File.Exists(MarkerPath())) {
          // Already answered once. If the file has since been moved, follow it
          // rather than asking again - they clearly know where they want it.
          try { File.WriteAllText(MarkerPath(), Assembly.GetExecutingAssembly().Location); } catch { }
          return false;
        }
        if (IsDevTree()) return false;

        string target;
        bool desktop, startMenu, autostart;
        if (!Ask(out target, out desktop, out startMenu, out autostart)) {
          // "Not now" is a real answer: remember it so the question does not
          // come back every launch, and carry on from where the file already is.
          try { File.WriteAllText(MarkerPath(), Assembly.GetExecutingAssembly().Location); } catch { }
          return false;
        }

        string destExe = DoInstall(target, desktop, startMenu, autostart);
        if (destExe == null) return false;       // already in the chosen place

        // Deliberately does NOT launch the copy and exit. "Write an executable
        // somewhere else, run it, and terminate yourself" is the dropper
        // sequence every heuristic engine is trained on, and an unsigned binary
        // doing it on first run is a large part of why this app gets flagged.
        // Carrying on in this process reaches the same place: the copy is what
        // the shortcuts and the startup entry point at, so from the next launch
        // onward the installed one is the one that runs.
        MessageBox.Show(
          "Installed to:\r\n" + target + "\r\n\r\n"
          + "This window's copy keeps running for now. From next time - the "
          + "shortcut, or signing in to Windows - it starts from its new home, "
          + "and you can delete the file you downloaded.",
          "Now Playing Overlay", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
      } catch (Exception ex) {
        AppLog.Write("install offer failed (continuing where we are): " + ex.Message);
        MessageBox.Show(
          "Could not install to that folder:\r\n\r\n" + ex.Message
          + "\r\n\r\nThe overlay will keep running from where it is.",
          "Now Playing Overlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        try { File.WriteAllText(MarkerPath(), Assembly.GetExecutingAssembly().Location); } catch { }
        return false;
      }
    }

    /// <summary>
    /// The install itself, with no UI of its own so it can be exercised without
    /// a dialog in the way. Returns the installed exe path, or null when the
    /// file is already living at the chosen destination.
    /// </summary>
    static string DoInstall(string target, bool desktop, bool startMenu, bool autostart) {
      string src = Assembly.GetExecutingAssembly().Location;
      string destExe = Path.Combine(target, Path.GetFileName(src));

      if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(destExe),
                        StringComparison.OrdinalIgnoreCase)) {
        File.WriteAllText(MarkerPath(), src);
        Shortcuts(destExe, desktop, startMenu);
        if (autostart) Program.SetStartupPublic(true);
        return null;
      }

      Directory.CreateDirectory(target);
      File.Copy(src, destExe, true);

      // Carry the Twitch credentials across, so someone who set the app up
      // before installing it does not have to do it twice. Never overwrite a
      // config already at the destination.
      try {
        string cfg = Path.Combine(Path.GetDirectoryName(src), "twitch-config.json");
        string destCfg = Path.Combine(target, "twitch-config.json");
        if (File.Exists(cfg) && !File.Exists(destCfg)) File.Copy(cfg, destCfg);
      } catch { }

      Shortcuts(destExe, desktop, startMenu);
      AppLog.Write("installed to " + destExe);
      File.WriteAllText(MarkerPath(), destExe);

      // The startup key records an absolute path, so it has to be written
      // pointing at the installed copy, not the one running this.
      if (autostart) {
        try {
          Microsoft.Win32.Registry.SetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run",
            "NowPlayingOverlay", "\"" + destExe + "\"");
        } catch { }
      }
      return destExe;
    }

    // A .lnk needs the shell's own object to create; WScript.Shell is present on
    // every Windows and is reachable late-bound, so this needs no COM reference
    // and cannot break the build on a machine that lacks it.
    static void Shortcuts(string exePath, bool desktop, bool startMenu) {
      if (!desktop && !startMenu) return;
      try {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return;
        dynamic shell = Activator.CreateInstance(t);
        if (desktop) {
          Make(shell, exePath, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Now Playing Overlay.lnk"));
        }
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

    // ------------------------------------------------------------------ dialog
    // Hand-built rather than a bare FolderBrowserDialog: the folder is only half
    // the question, and a picker with no explanation appearing out of nowhere on
    // first launch reads as the program having gone wrong.
    static bool Ask(out string target, out bool desktop, out bool startMenu, out bool autostart) {
      target = DefaultTarget();
      desktop = true; startMenu = true; autostart = false;

      var f = new Form {
        Text = "Install Now Playing Overlay",
        FormBorderStyle = FormBorderStyle.FixedDialog,
        StartPosition = FormStartPosition.CenterScreen,
        MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = true,
        ClientSize = new Size(500, 258),
        Font = new Font("Segoe UI", 9F)
      };

      var head = new Label {
        Text = "Where should the overlay live?",
        Font = new Font("Segoe UI", 12F, FontStyle.Bold),
        Location = new Point(16, 14), Size = new Size(468, 24)
      };
      var blurb = new Label {
        Text = "This is one self-contained file - the overlay pages are inside it, so "
             + "nothing else needs to be copied. Putting it somewhere permanent means "
             + "clearing out your Downloads folder later cannot break OBS.",
        Location = new Point(16, 40), Size = new Size(468, 46),
        ForeColor = Color.FromArgb(90, 90, 90)
      };

      var box = new TextBox { Text = target, Location = new Point(16, 94), Size = new Size(378, 23) };
      var browse = new Button { Text = "Browse...", Location = new Point(402, 93), Size = new Size(82, 25) };
      browse.Click += (s, e) => {
        using (var d = new FolderBrowserDialog()) {
          d.Description = "Choose a folder for Now Playing Overlay";
          d.SelectedPath = Directory.Exists(box.Text) ? box.Text
                         : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
          if (d.ShowDialog(f) == DialogResult.OK)
            box.Text = Path.Combine(d.SelectedPath, "NowPlayingOverlay");
        }
      };

      var cbDesktop = new CheckBox { Text = "Put a shortcut on my desktop", Checked = true,
                                     Location = new Point(16, 128), Size = new Size(468, 22) };
      var cbStart = new CheckBox { Text = "Add it to the Start menu", Checked = true,
                                   Location = new Point(16, 152), Size = new Size(468, 22) };
      var cbAuto = new CheckBox { Text = "Start automatically when I sign in to Windows",
                                  Checked = false, Location = new Point(16, 176), Size = new Size(468, 22) };

      var ok = new Button { Text = "Install", Location = new Point(298, 214), Size = new Size(96, 28),
                            DialogResult = DialogResult.OK };
      var skip = new Button { Text = "Not now", Location = new Point(402, 214), Size = new Size(82, 28),
                              DialogResult = DialogResult.Cancel };
      // "Not now" leaves it where it is - a real choice, not a way to trap
      // someone in a dialog they did not ask for.
      f.AcceptButton = ok; f.CancelButton = skip;

      f.Controls.AddRange(new Control[] { head, blurb, box, browse, cbDesktop, cbStart, cbAuto, ok, skip });

      bool go = f.ShowDialog() == DialogResult.OK;
      target = (box.Text ?? "").Trim();
      desktop = cbDesktop.Checked; startMenu = cbStart.Checked; autostart = cbAuto.Checked;
      f.Dispose();
      if (!go || target.Length == 0) return false;
      return true;
    }
  }
}
