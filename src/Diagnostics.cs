// The page you open when it works here and not there.
//
// Rendered on the server rather than fetched by script on purpose. A diagnostics
// page that needs JavaScript can fail for the same reason the thing being
// diagnosed failed, and then reports nothing at the moment it is most needed.
// Everything below arrives as finished HTML; the only script is a progressive
// extra that adds browser capability results, and its absence costs nothing.
//
// It is also written to be readable by someone who did not build it - each
// finding says what it means and what to do, because the person reading it is
// usually on the far end of a phone call.

using System;
using System.Collections.Generic;
using System.Text;

namespace NowPlaying {

  static class Diagnostics {

    static string Esc(string s) {
      if (string.IsNullOrEmpty(s)) return "";
      return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    static void Row(StringBuilder sb, string k, string v) {
      sb.Append("<tr><th>").Append(Esc(k)).Append("</th><td>").Append(Esc(v)).Append("</td></tr>");
    }

    static void Note(StringBuilder sb, string kind, string html) {
      sb.Append("<p class=\"note ").Append(kind).Append("\">").Append(html).Append("</p>");
    }

    public static string Html(int port, string mode, string pinApp,
                              bool elevated, string itunesDetail, bool itunesRunning,
                              Snapshot now, string configPath) {
      var sb = new StringBuilder();
      sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
      sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
      sb.Append("<title>Overlay diagnostics</title><style>");
      sb.Append("body{margin:0;background:#080B14;color:#EAF2FF;font-family:'Segoe UI',system-ui,sans-serif}");
      sb.Append(".w{max-width:900px;margin:0 auto;padding:26px 22px 60px}");
      sb.Append("h1{font-size:22px;margin:0 0 4px}h2{font-size:14px;text-transform:uppercase;");
      sb.Append("letter-spacing:.1em;color:#8A9BC0;margin:26px 0 8px}");
      sb.Append("table{width:100%;border-collapse:collapse;background:#10141F;border:1px solid #1E2740;border-radius:10px}");
      sb.Append("th,td{text-align:left;padding:7px 12px;border-bottom:1px solid #171F31;font-size:13.5px;vertical-align:top}");
      sb.Append("th{color:#8A9BC0;font-weight:600;width:210px}tr:last-child th,tr:last-child td{border-bottom:0}");
      sb.Append(".note{border-radius:9px;padding:10px 13px;font-size:13.5px;line-height:1.55;border:1px solid;margin:9px 0}");
      sb.Append(".ok{background:#0d1f16;border-color:#1f4a33;color:#a9e6c4}");
      sb.Append(".warn{background:#2a1f10;border-color:#5a4420;color:#f2d6a2}");
      sb.Append(".bad{background:#2a1414;border-color:#5c2626;color:#f3bcbc}");
      sb.Append("code{font-family:Consolas,monospace;font-size:12.5px;color:#BFE6FA}");
      sb.Append("pre{background:#0A0F1A;border:1px solid #1E2740;border-radius:10px;padding:13px;");
      sb.Append("font-size:12px;white-space:pre-wrap;word-break:break-word;color:#BFE6FA}");
      sb.Append("a{color:#9BD4F5}.muted{color:#8A9BC0;font-size:13px;line-height:1.55}");
      sb.Append("</style></head><body><div class=\"w\">");

      sb.Append("<h1>Overlay diagnostics</h1>");
      sb.Append("<p class=\"muted\">Everything here is read live from this machine. ");
      sb.Append("Copy the block at the bottom if someone is helping you remotely.</p>");

      // ---- build ------------------------------------------------------------
      sb.Append("<h2>This build</h2><table>");
      Row(sb, "Version", BuildInfo.Version);
      Row(sb, "Built", BuildInfo.BuiltUtc);
      Row(sb, "Running as", IntPtr.Size == 8 ? "64-bit" : "32-bit");
      Row(sb, "Elevated (admin)", elevated ? "yes" : "no");
      Row(sb, "Windows", Environment.OSVersion.VersionString);
      Row(sb, "Serving on", "http://127.0.0.1:" + port + "/");
      sb.Append("</table>");
      Note(sb, "warn", "If the version above is older than the machine you built on, that alone "
        + "explains missing pages such as <code>/app</code> or the Chat bot tab. Copy the newer "
        + "<code>dist\\NowPlayingOverlay.exe</code> across before chasing anything else.");

      // ---- what is playing ---------------------------------------------------
      sb.Append("<h2>What the overlay is showing</h2><table>");
      Row(sb, "Chosen source", now == null ? "none" : now.Source);
      Row(sb, "Title", now == null ? "" : now.Title);
      Row(sb, "Artist", now == null ? "" : now.Artist);
      Row(sb, "App", now == null ? "" : now.App);
      Row(sb, "Playing", now != null && now.Playing ? "yes" : "no");
      sb.Append("</table>");

      // ---- the pin trap ------------------------------------------------------
      var sessions = AllSessionsSafe();
      int excluded = 0;
      foreach (var s in sessions) if (s.ExcludedByPin) excluded++;

      sb.Append("<h2>Source selection</h2><table>");
      Row(sb, "Mode", mode);
      Row(sb, "Pinned to", pinApp.Length > 0 ? pinApp : "(nothing)");
      sb.Append("</table>");

      if (mode == "only" && excluded > 0) {
        Note(sb, "bad", "<strong>This is almost certainly your problem.</strong> The source is "
          + "locked to <code>" + Esc(pinApp) + "</code> in <em>only</em> mode, which is hiding "
          + excluded + " other player" + (excluded == 1 ? "" : "s") + " listed below. "
          + "Open <a href=\"/app#control\">Choose source</a> and press "
          + "<em>Back to automatic</em>.");
      } else if (mode != "auto") {
        Note(sb, "warn", "A source is pinned. That is fine, but it does change what can appear. "
          + "<a href=\"/app#control\">Choose source</a> to clear it.");
      }

      // ---- sessions ----------------------------------------------------------
      sb.Append("<h2>Media sessions Windows can see (").Append(sessions.Count).Append(")</h2>");
      if (sessions.Count == 0) {
        Note(sb, "warn", "Windows is reporting no media sessions at all. Start playback in a "
          + "player and reload. Note that classic iTunes never appears in this list - it does "
          + "not publish to the Windows media session, which is why it needs the separate "
          + "connection below.");
      } else {
        sb.Append("<table><tr><th>App id</th><td><strong>Now playing</strong></td></tr>");
        foreach (var s in sessions) {
          sb.Append("<tr><th>").Append(Esc(s.Aumid)).Append("</th><td>");
          sb.Append(Esc(s.Title.Length > 0 ? s.Title + (s.Artist.Length > 0 ? "  -  " + s.Artist : "") : "(nothing)"));
          sb.Append("<br><span class=\"muted\">").Append(Esc(s.Status));
          if (s.Musicish) sb.Append(" &middot; treated as a music app");
          if (s.ExcludedByPin) sb.Append(" &middot; <strong>excluded by the pin above</strong>");
          sb.Append("</span></td></tr>");
        }
        sb.Append("</table>");
      }

      // ---- itunes ------------------------------------------------------------
      sb.Append("<h2>iTunes</h2><table>");
      Row(sb, "iTunes.exe running", itunesRunning ? "yes" : "no");
      Row(sb, "Connection detail", itunesDetail.Length > 0 ? itunesDetail : "(not attempted yet)");
      sb.Append("</table>");

      if (!itunesRunning) {
        Note(sb, "warn", "Classic iTunes is reached over COM automation and only while "
          + "<code>iTunes.exe</code> is actually open - the overlay never launches it. "
          + "If you are using Apple's newer <strong>Apple Music</strong> app from the Microsoft "
          + "Store instead, it has no COM automation at all; it should appear in the media "
          + "session list above and be picked up from there.");
      } else if (itunesDetail.IndexOf("connected", StringComparison.OrdinalIgnoreCase) < 0) {
        Note(sb, "bad", "iTunes is running but the overlay could not attach to it. "
          + (elevated
             ? "This copy is running <strong>as administrator</strong> and iTunes probably is not. "
               + "COM refuses to connect across that boundary - run the overlay normally, without admin."
             : "If iTunes was started <strong>as administrator</strong>, COM will refuse to connect "
               + "across that boundary. Start iTunes normally, or run both the same way.")
          + " The exact error is in the detail row above.");
      } else {
        Note(sb, "ok", "iTunes is connected.");
      }

      // ---- twitch / bot ------------------------------------------------------
      sb.Append("<h2>Twitch</h2><table>");
      Row(sb, "Config file", configPath ?? "(none found)");
      Row(sb, "Alerts status", TwitchEvents.Status + (TwitchEvents.Detail.Length > 0 ? "  -  " + TwitchEvents.Detail : ""));
      Row(sb, "Chat bot status", TwitchChat.Status + (TwitchChat.Detail.Length > 0 ? "  -  " + TwitchChat.Detail : ""));
      sb.Append("</table>");
      if (configPath == null) {
        Note(sb, "warn", "No <code>twitch-config.json</code> was found, so the alerts, the "
          + "follower boxes and the chat bot are all switched off. That is expected on a machine "
          + "you have not set those up on - the song overlay is unaffected.");
      }

      // ---- live connections --------------------------------------------------
      // Worth its own section because connection pressure is the one fault here
      // that looks like a design bug from every other angle: pages that never
      // finish, previews that stay blank, album art that silently does not
      // arrive. None of it reports an error anywhere, so without a number to
      // look at there is nothing to find.
      int spec = Program.SpectrumClients, tw = Program.TwitchClients;
      int workerFree, ioFree, workerMax, ioMax, workerMin, ioMin;
      System.Threading.ThreadPool.GetAvailableThreads(out workerFree, out ioFree);
      System.Threading.ThreadPool.GetMaxThreads(out workerMax, out ioMax);
      System.Threading.ThreadPool.GetMinThreads(out workerMin, out ioMin);

      sb.Append("<h2>Live connections</h2><table>");
      Row(sb, "Equaliser streams", spec + " of " + Program.MaxSpectrumClients);
      Row(sb, "Alert streams", tw + " of " + Program.MaxTwitchClients);
      // Against the ceiling this reads as 2 of 32767, which tells you nothing.
      // The floor is the number that matters: below it a request gets a thread at
      // once, above it the pool adds threads only about twice a second.
      Row(sb, "Worker threads in use", (workerMax - workerFree)
        + "  (instant up to " + workerMin + ")");
      sb.Append("</table>");

      Note(sb, "warn", "Each of these holds one connection open for as long as it lasts, and a "
        + "browser allows only <strong>six per address</strong>. Anything past that waits - which "
        + "is why a starved page shows a blank preview or a missing album cover rather than an "
        + "error. If these numbers look high, close spare dashboard and overlay windows: each one "
        + "you leave open is counted here. OBS browser sources count too.");

      if (spec >= Program.MaxSpectrumClients || tw >= Program.MaxTwitchClients) {
        Note(sb, "bad", "<strong>A stream limit is full.</strong> New pages will be refused their "
          + "live data until something disconnects. Close any dashboard or overlay windows you are "
          + "not using and reload this page - the count should drop within a few seconds.");
      }

      // ---- browser -----------------------------------------------------------
      sb.Append("<h2>This browser</h2>");
      sb.Append("<table id=\"bt\"><tr><th>Checking...</th><td>");
      sb.Append("<span class=\"muted\">If this row never changes, JavaScript is not running, "
        + "which would break the dashboard on its own.</span></td></tr></table>");

      // ---- copyable ----------------------------------------------------------
      sb.Append("<h2>Copy this when asking for help</h2><pre id=\"rep\">");
      sb.Append(Esc(PlainText(port, mode, pinApp, elevated, itunesDetail, itunesRunning, now, configPath, sessions)));
      sb.Append("</pre>");

      sb.Append("<script>(function(){");
      sb.Append("var t=document.getElementById('bt');");
      sb.Append("function has(p,v){try{return CSS.supports(p,v);}catch(e){return false;}}");
      sb.Append("var checks=[['User agent',navigator.userAgent],");
      sb.Append("['flexbox gap',has('gap','1px')?'yes':'NO - layout will break'],");
      sb.Append("['inset shorthand',has('inset','0')?'yes':'NO - overlays will be mispositioned'],");
      sb.Append("['clip-path',has('clip-path','polygon(0 0,1px 0,0 1px)')?'yes':'NO - blade frames will not cut'],");
      sb.Append("['CSS mask',(has('mask-image','none')||has('-webkit-mask-image','none'))?'yes':'NO - the shuriken will not draw'],");
      sb.Append("['backdrop blur',has('backdrop-filter','blur(1px)')?'yes':'no - frosted panels fall back to flat'],");
      sb.Append("['WebSocket',('WebSocket' in window)?'yes':'no - streams fall back to SSE'],");
      sb.Append("['EventSource',('EventSource' in window)?'yes':'NO - alerts cannot stream'],");
      sb.Append("['ResizeObserver',('ResizeObserver' in window)?'yes':'no - size readout unavailable']];");
      sb.Append("var h='';for(var i=0;i<checks.length;i++){h+='<tr><th>'+checks[i][0]+'</th><td>'+");
      sb.Append("String(checks[i][1]).replace(/</g,'&lt;')+'</td></tr>';}t.innerHTML=h;");
      sb.Append("var pre=document.getElementById('rep');var extra='\\n\\nBROWSER\\n';");
      sb.Append("for(var j=0;j<checks.length;j++){extra+='  '+checks[j][0]+': '+checks[j][1]+'\\n';}");
      sb.Append("pre.textContent+=extra;");
      sb.Append("})();</script>");

      sb.Append("</div></body></html>");
      return sb.ToString();
    }

    static List<Program.SessionInfo> AllSessionsSafe() {
      try { return Program.AllSessions(); } catch { return new List<Program.SessionInfo>(); }
    }

    // The same findings as plain text. A screenshot of a long page loses the
    // bottom half; this can be pasted into a message whole.
    static string PlainText(int port, string mode, string pinApp, bool elevated,
                            string itunesDetail, bool itunesRunning, Snapshot now,
                            string configPath, List<Program.SessionInfo> sessions) {
      var sb = new StringBuilder();
      sb.Append("NOW PLAYING OVERLAY - DIAGNOSTICS\r\n");
      sb.Append("  version      : ").Append(BuildInfo.Version).Append("\r\n");
      sb.Append("  built        : ").Append(BuildInfo.BuiltUtc).Append("\r\n");
      sb.Append("  windows      : ").Append(Environment.OSVersion.VersionString).Append("\r\n");
      sb.Append("  bitness      : ").Append(IntPtr.Size == 8 ? "64-bit" : "32-bit")
        .Append(elevated ? " (ELEVATED)" : "").Append("\r\n");
      sb.Append("  port         : ").Append(port).Append("\r\n");
      sb.Append("  mode / pin   : ").Append(mode).Append(" / ")
        .Append(pinApp.Length > 0 ? pinApp : "-").Append("\r\n");
      sb.Append("  showing      : ").Append(now == null ? "nothing"
        : (now.Source + " | " + now.Title + " | " + now.Artist)).Append("\r\n");
      sb.Append("  itunes.exe   : ").Append(itunesRunning ? "running" : "not running").Append("\r\n");
      sb.Append("  itunes detail: ").Append(itunesDetail).Append("\r\n");
      sb.Append("  config       : ").Append(configPath ?? "none").Append("\r\n");
      sb.Append("  twitch       : ").Append(TwitchEvents.Status).Append(" ").Append(TwitchEvents.Detail).Append("\r\n");
      sb.Append("  chat bot     : ").Append(TwitchChat.Status).Append(" ").Append(TwitchChat.Detail).Append("\r\n");
      int wFree, iFree, wMax, iMax, wMin, iMin;
      System.Threading.ThreadPool.GetAvailableThreads(out wFree, out iFree);
      System.Threading.ThreadPool.GetMaxThreads(out wMax, out iMax);
      System.Threading.ThreadPool.GetMinThreads(out wMin, out iMin);
      sb.Append("  streams      : eq ").Append(Program.SpectrumClients).Append("/").Append(Program.MaxSpectrumClients)
        .Append(", alerts ").Append(Program.TwitchClients).Append("/").Append(Program.MaxTwitchClients)
        .Append(", workers ").Append(wMax - wFree).Append(" (floor ").Append(wMin).Append(")\r\n");
      sb.Append("SESSIONS (").Append(sessions.Count).Append(")\r\n");
      foreach (var s in sessions) {
        sb.Append("  - ").Append(s.Aumid).Append(" [").Append(s.Status).Append("]")
          .Append(s.ExcludedByPin ? " EXCLUDED-BY-PIN" : "")
          .Append(" : ").Append(s.Title).Append("\r\n");
      }
      return sb.ToString();
    }
  }
}
