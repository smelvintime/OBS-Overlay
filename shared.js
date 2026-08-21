/* shared.js - the one copy of what every page used to carry for itself.

   Two things live here and nothing else does:

   1. The theme engine. Themes used to be :root[data-theme="..."] blocks pasted
      into all ten pages, which is why adding a theme meant editing ten files.
      Now a theme is data - a JSON map of CSS variables served by /themes - and
      this file turns that data into a <style id="npo-theme"> tag. A style tag,
      not element.style on the root: the customizer pins per-source colours with
      root.style.setProperty(), and an inline style must keep beating the theme,
      otherwise every 5s poll would clobber a deliberately pinned accent.
      Each page keeps its own :root{} defaults (they genuinely differ - alerts
      has per-event hues, stats has the shuriken mark), so the default look
      needs no theme data at all and a page still renders if /themes never
      answers.

   2. The stream connector. WebSocket first because OBS runs every browser
      source in one Chromium and a WebSocket stays out of the six-connection
      HTTP pool for this address (see overlay.html for the full story); SSE is
      the fallback - chosen by failure, not by feature detection, because the
      machines that need it HAVE WebSocket. See NPO.stream for that story. A
      page with no connection stays quiet rather than announce the problem
      over a live stream, so reconnects are silent and endless.

   Pages served by an older exe cannot reference this file and pages served by
   this exe cannot miss it - both travel inside the same executable - so there
   is no version skew to defend against, only network hiccups. */
(function () {
  'use strict';
  var NPO = window.NPO = {};

  NPO.qs = function () {
    try { return new URLSearchParams(location.search); }
    catch (e) { return { get: function () { return null; } }; }
  };

  /* ---- social marks --------------------------------------------------------
     Lives here rather than in a page because two pages want it now - the
     socials overlay and the webcam frame's name plate - and a logo copied
     into two files is a logo that will be fixed in one of them.
     currentColor throughout, so a single CSS rule paints them.
     TikTok and YouTube are one filled path each; YouTube's play triangle is a
     counter-wound subpath, so the default fill rule punches it out as a hole
     rather than painting over it. Instagram is three shapes, with the body
     and lens stroked - filled, they blot into a square at this size. */
  NPO.SOCIALS = ['tiktok', 'youtube', 'instagram'];
  NPO.SOCIAL_BRAND = { tiktok: '#25F4EE', youtube: '#FF0000', instagram: '#E1306C' };

  var MARKS = {
    tiktok: '<path fill="currentColor" d="M12.53.02C13.84 0 15.14.01 16.44 0c.08 1.53.63 3.09 1.75 4.17 1.12 1.11 2.7 1.62 4.24 1.79v4.03c-1.44-.05-2.89-.35-4.2-.97-.57-.26-1.1-.59-1.62-.93-.01 2.92.01 5.84-.02 8.75-.08 1.4-.54 2.79-1.35 3.94-1.31 1.92-3.58 3.17-5.91 3.21-1.43.08-2.86-.31-4.08-1.03-2.02-1.19-3.44-3.37-3.65-5.71-.02-.5-.03-1-.01-1.49.18-1.9 1.12-3.72 2.58-4.96 1.66-1.44 3.98-2.13 6.15-1.72.02 1.48-.04 2.96-.04 4.44-.99-.32-2.15-.23-3.02.37-.63.41-1.11 1.04-1.36 1.75-.21.51-.15 1.07-.14 1.61.24 1.64 1.82 3.02 3.5 2.87 1.12-.01 2.19-.66 2.77-1.61.19-.33.4-.67.41-1.06.1-1.79.06-3.57.07-5.36.01-4.03-.01-8.05.02-12.07z"/>',
    youtube: '<path fill="currentColor" d="M23.5 6.2a3 3 0 0 0-2.1-2.1C19.5 3.5 12 3.5 12 3.5s-7.5 0-9.4.6A3 3 0 0 0 .5 6.2 31 31 0 0 0 0 12a31 31 0 0 0 .5 5.8 3 3 0 0 0 2.1 2.1c1.9.6 9.4.6 9.4.6s7.5 0 9.4-.6a3 3 0 0 0 2.1-2.1A31 31 0 0 0 24 12a31 31 0 0 0-.5-5.8zM9.5 15.6V8.4l6.3 3.6-6.3 3.6z"/>',
    instagram: '<path d="M7 2.2h10A4.8 4.8 0 0 1 21.8 7v10A4.8 4.8 0 0 1 17 21.8H7A4.8 4.8 0 0 1 2.2 17V7A4.8 4.8 0 0 1 7 2.2z" fill="none" stroke="currentColor" stroke-width="2"/>'
      + '<circle cx="12" cy="12" r="4.2" fill="none" stroke="currentColor" stroke-width="2"/>'
      + '<circle cx="17.6" cy="6.4" r="1.3" fill="currentColor" stroke="none"/>'
  };

  /* An <svg> element for one platform, or null for a name nobody has heard
     of. Built through innerHTML on a scratch container rather than assembled
     with createElementNS by hand: the markup above is a constant in this
     file, never anything a user typed. */
  NPO.socialMark = function (platform, cls) {
    var d = MARKS[platform];
    if (!d) return null;
    var box = document.createElement('div');
    box.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true">' + d + '</svg>';
    var svg = box.firstChild;
    if (cls) svg.setAttribute('class', cls);
    svg.setAttribute('data-p', platform);
    return svg;
  };

  /* Which platforms to show and what handle each carries, resolved from the
     shared query-string vocabulary both pages use: ?handle= for the common
     "same name everywhere", per-platform overrides for the odd one out, and
     ?show= to pick the set and its order. Returns [] when nothing resolves,
     which both callers treat as "draw nothing". */
  NPO.socialList = function (qs, prefix) {
    function clean(s) {
      s = (s || '').trim();
      while (s.charAt(0) === '@') s = s.slice(1);   /* the prefix redraws it */
      /* Cut where a mangled query starts. One hand-edited OBS URL that ran two
         params together painted "@ALEXSZEDS=40" on stream, so everything from
         the first character that cannot occur in a handle but does occur in a
         query string is dropped.

         Deliberately a blocklist, not an allowlist: the first cut of this
         allowed [A-Za-z0-9._-] only, which silently emptied every non-ASCII
         handle - and a YouTube handle may be Cyrillic, Japanese or emoji, so
         that turned a legitimate name into no plate at all. Anything that is
         not URL punctuation or whitespace is somebody's name somewhere. */
      var cut = s.search(/[\s=&?#/\\<>"'`]/);
      if (cut >= 0) s = s.slice(0, cut);
      return s.length > 32 ? s.slice(0, 32) : s;
    }
    var shared = clean(qs.get('handle'));
    var order = [], raw = (qs.get('show') || '').split(','), i, part;
    for (i = 0; i < raw.length; i++) {
      part = raw[i].trim().toLowerCase();
      if (NPO.SOCIALS.indexOf(part) >= 0 && order.indexOf(part) < 0) order.push(part);
    }
    if (!order.length) order = NPO.SOCIALS.slice();
    var out = [];
    for (i = 0; i < order.length; i++) {
      var want = clean(qs.get(order[i])) || shared;
      if (want) out.push({ p: order[i], h: (prefix || '') + want, raw: want });
    }
    return out;
  };

  /* ---- accent corners ------------------------------------------------------
     The house look is a plate with two corners cut square and bracketed in the
     accent. Five pages drew that pair, and every one of them hard-wired it to
     the top-right and bottom-left with a ::before/::after couple - which is a
     hard ceiling of two, because an element has exactly two pseudo-elements.
     Choosing corners means real elements, and five pages drawing the same
     bracket means one copy here rather than five that drift apart.

     The bracketed corners are also the CUT corners: a square bracket sitting
     on a 14px rounded corner reads as a mistake rather than a style. So this
     owns the host's border-radius too - picked corners go sharp, the rest keep
     var(--radius) - which reproduces exactly the shape each page hard-coded,
     since the old pair was the top-right and bottom-left of
     "var(--radius) 3px var(--radius) 3px".

     Size, colour, thickness, glow, opacity and offset all come from the host
     as custom properties, because the five pages genuinely differ: the stat
     boxes are 15px at .95 opacity on a per-box colour, the webcam frame rides
     its own border width. The defaults here are the values four of the five
     used, so most hosts set nothing at all. */
  NPO.CORNERS = ['tl', 'tr', 'br', 'bl'];   /* CSS border-radius order, and canonical */
  var CORNER_DEFAULT = ['tr', 'bl'];        /* the diagonal every page drew before */

  /* Which corners a URL asks for. Absent means the house diagonal, so every
     link written before this existed keeps its look. */
  NPO.cornerList = function (qs) {
    var raw = qs.get('corners');
    if (raw === null) return CORNER_DEFAULT.slice();
    raw = String(raw).trim().toLowerCase();
    if (raw === '' || raw === 'none') return [];
    if (raw === 'all') return NPO.CORNERS.slice();
    var want = raw.split(','), out = [], i, j;
    /* Read in canonical order rather than the order they were typed: the
       corners are a set, and "tr,bl" and "bl,tr" must build the same plate. */
    for (i = 0; i < NPO.CORNERS.length; i++) {
      for (j = 0; j < want.length; j++) {
        if (want[j].trim() === NPO.CORNERS[i]) { out.push(NPO.CORNERS[i]); break; }
      }
    }
    /* A value that resolved to nothing is a typo, not a request for a bare
       plate - "none" is how that is asked for. Falling back to the house look
       beats silently stripping the accent off a live overlay. */
    return out.length ? out : CORNER_DEFAULT.slice();
  };

  var cornerCssDone = false;
  function cornerCss() {
    if (cornerCssDone) return;
    cornerCssDone = true;
    var css = '.npo-cnr{position:absolute;pointer-events:none;box-sizing:border-box;'
            + 'width:var(--cnr-size,18px);height:var(--cnr-size,18px);'
            + 'border:0 solid var(--cnr-c,var(--accent));'
            + 'opacity:var(--cnr-op,1);transition:opacity .3s ease;'
            + 'filter:drop-shadow(0 0 var(--cnr-glow,6px) var(--cnr-c,var(--accent)))}';
    var sides = { tl: ['top', 'left'], tr: ['top', 'right'],
                  br: ['bottom', 'right'], bl: ['bottom', 'left'] };
    for (var k in sides) {
      if (!Object.prototype.hasOwnProperty.call(sides, k)) continue;
      var v = sides[k];
      css += '.npo-cnr.npo-' + k + '{'
           + v[0] + ':var(--cnr-off,-1px);' + v[1] + ':var(--cnr-off,-1px);'
           + 'border-' + v[0] + '-width:var(--cnr-w,2px);'
           + 'border-' + v[1] + '-width:var(--cnr-w,2px);'
           + 'border-' + v[0] + '-' + v[1] + '-radius:3px}';
    }
    var s = document.createElement('style');
    s.id = 'npo-corners';
    s.textContent = css;
    (document.head || document.documentElement).appendChild(s);
  }

  /* Hang the brackets on a host and cut the matching corners. The host must be
     position:relative (all five already are) and is never emptied by its page,
     so these survive every re-render. Safe to call again: the previous set is
     cleared first. */
  NPO.corners = function (host, list) {
    if (!host) return;
    cornerCss();
    var old = host.querySelectorAll('.npo-cnr'), i;
    for (i = 0; i < old.length; i++) old[i].parentNode.removeChild(old[i]);
    for (i = 0; i < list.length; i++) {
      var el = document.createElement('span');
      el.className = 'npo-cnr npo-' + list[i];
      host.appendChild(el);
    }
    /* Inline, so it beats the page's own blade rule - which is the shape this
       is replacing, and which stays in the stylesheet as the look this page
       falls back to if shared.js never loads. */
    var r = [];
    for (i = 0; i < NPO.CORNERS.length; i++)
      r.push(list.indexOf(NPO.CORNERS[i]) >= 0 ? '3px' : 'var(--radius)');
    host.style.borderRadius = r.join(' ');
  };

  /* ---- live streams (spectrum, twitch events) ------------------------------

     Which transport gets used is decided by what WORKS, not by what exists.
     The first cut asked only "is there a WebSocket object?" - and every
     browser this app will ever meet has one, so the SSE path was dead code
     on precisely the machines that needed it. Antivirus web shields and
     other local proxies sit on 127.0.0.1, wave ordinary GETs through and
     quietly kill the Upgrade handshake: on such a machine every plain
     request succeeds - the song card fills in, covers load - while every
     stream dies before its first frame, so the live equaliser and the
     alerts just never come on, with nothing anywhere saying why. The server
     has always served an SSE twin of every stream (kept for pages from an
     older exe); such machines just need it actually tried.

     So: a connection that dies without ever delivering a message is treated
     as "this transport may not work here", and the next attempt uses the
     other one. Whichever transport actually delivers is kept - a WebSocket
     that worked and then dropped reconnects as a WebSocket, preserving the
     six-connection story above. If the server itself is down, the attempts
     just alternate quietly at the usual retry pace until it returns, which
     is no worse than retrying one transport forever and ends the same way. */

  NPO.stream = function (path, onJson, opts) {
    opts = opts || {};
    var retry = opts.retryMs || 3000;
    var delivered;                  // did the CURRENT attempt produce a message?
    var wsWorked = false;           // has ANY WebSocket attempt delivered, ever?
    function handle(ev) {
      var d;
      try { d = JSON.parse(ev.data); } catch (e) { return; }
      if (!d) return;
      delivered = true;
      onJson(d);
    }
    function down() { if (opts.onDown) opts.onDown(); }

    var hasWs = !!window.WebSocket, hasSse = !!window.EventSource;
    if (!hasWs && !hasSse) return;

    function connectWs() {
      delivered = false;
      var ws;
      // The constructor itself can throw (a mangled host, a lying webview);
      // uncaught it would end reconnection for good, which breaks the
      // "silent and endless" promise every caller relies on.
      try { ws = new WebSocket('ws://' + location.host + path); }
      catch (e) { down(); setTimeout(hasSse ? connectSse : connectWs, retry); return; }
      ws.onmessage = function (ev) { wsWorked = true; handle(ev); };
      // Unlike EventSource, a WebSocket does not retry on its own.
      ws.onclose = function () {
        down();
        // No frame ever arrived: a refused or stripped handshake looks
        // exactly like a server that is restarting, and the close code
        // cannot tell them apart (both are 1006) - so give SSE the next
        // turn rather than this socket forever. But only on a machine
        // WebSocket has never worked on: once it has delivered, a dead
        // attempt means the server is away, not that the transport is
        // broken, and falling back then would leak OBS sources into the
        // six-connection pool after every self-update restart.
        setTimeout(!delivered && !wsWorked && hasSse ? connectSse : connectWs, retry);
      };
    }

    function connectSse() {
      delivered = false;
      var es;
      try { es = new EventSource(path); }
      catch (e) { down(); setTimeout(hasWs ? connectWs : connectSse, retry); return; }
      es.onmessage = handle;
      es.onerror = function () {
        down();
        // Once SSE has delivered, its own reconnection is left alone - it
        // is the transport that provably works here. Failing before the
        // first message may equally be the transport's fault (a proxy that
        // buffers the response into never arriving), so close it and give
        // WebSocket its turn back.
        if (!delivered) {
          es.close();
          setTimeout(hasWs ? connectWs : connectSse, retry);
        }
      };
    }

    if (hasWs) connectWs(); else connectSse();
  };

  /* ---- theme engine --------------------------------------------------------- */

  function styleTag(doc) {
    var el = doc.getElementById('npo-theme');
    if (!el) {
      el = doc.createElement('style');
      el.id = 'npo-theme';
      (doc.head || doc.documentElement).appendChild(el);
    }
    return el;
  }

  // Theme files can be written by hand and dropped into the themes folder, so
  // the values pass through a gate: variable names only, and no character that
  // could close the declaration and smuggle arbitrary CSS into every page.
  function cssFor(def, mode) {
    var vars = def && def[mode];
    if (!vars) return '';
    var out = [];
    for (var k in vars) {
      if (!Object.prototype.hasOwnProperty.call(vars, k)) continue;
      if (!/^--[a-zA-Z0-9_-]+$/.test(k)) continue;
      var v = String(vars[k]);
      if (/[;{}<>]/.test(v)) continue;
      out.push(k + ':' + v);
    }
    return out.length ? ':root{' + out.join(';') + '}' : '';
  }
  NPO.cssFor = cssFor;

  // mode is 'source' (overlay, alerts, stats - the pages OBS captures) or
  // 'dashboard' (everything a human looks at). Same theme, two variable sets,
  // because the two families never shared variable names to begin with.
  // The data-theme attribute is still set alongside: a few page rules key off
  // it for things that are not colours (setup keeps none today, but the
  // attribute is the cheap forward-compatible hook).
  NPO.applyThemeDef = function (def, mode, doc) {
    doc = doc || document;
    styleTag(doc).textContent = cssFor(def, mode);
    var name = def && def.name;
    var root = doc.documentElement;
    if (!name || name === 'shockblade') root.removeAttribute('data-theme');
    else root.setAttribute('data-theme', name);
  };

  var cache = null;
  NPO.themes = function (cb) {
    fetch('/themes', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (j) { if (j && j.themes) cache = j; cb(cache); })
      .catch(function () { cb(cache); });   // a stale answer beats none at all
  };
  NPO.findTheme = function (j, name) {
    if (!j || !j.themes) return null;
    for (var i = 0; i < j.themes.length; i++) {
      if (j.themes[i] && j.themes[i].name === name) return j.themes[i];
    }
    return null;
  };

  /* The boot every page calls once.

     source pages poll forever: an OBS browser source is its own browser profile
     that never opens the dashboard, so the only way a theme switch (or a hub
     edit to the current theme's colours) reaches the thing actually on stream
     is the source asking again. Short requests that close immediately - not a
     held stream, so the six-connection story above does not apply.

     dashboard pages paint once. Before the network answers they use the copy
     cached in localStorage from the previous visit, applied synchronously, so
     a themed dashboard does not flash blue on every load. localStorage is only
     that flash-guard - the app's /themes answer is the real store, because
     localhost and 127.0.0.1 are different origins with different storage and
     only the server sees both.

     An explicit ?theme= wins over the app's choice so one OBS source can be
     pinned to a look on purpose; it still repaints when that theme's data
     changes. */
  NPO.themeBoot = function (mode) {
    var forced = NPO.qs().get('theme');
    /* theme= is overloaded: the overlay reads it as the chrome behind the
       content (auto|glass|solid|none, plus the legacy layout alias "minimal"),
       and this reads it as a palette to pin. Any chrome value that reached the
       palette side found no theme by that name and painted the EMPTY string
       over the active theme - then did it again on every 5s poll, so a
       Shadow-red channel got one stubbornly blue source and no way to fix it.
       "none" is the one that actually shipped: it is what the customizer's
       Chrome -> None button emits. */
    var CHROME = ['glass', 'solid', 'minimal', 'none', 'auto'];
    if (CHROME.indexOf(forced) >= 0) forced = null;

    if (mode === 'dashboard' && !forced) {
      try {
        var css = localStorage.getItem('nowplaying.themeCss');
        var nm = localStorage.getItem('nowplaying.theme');
        if (css !== null) styleTag(document).textContent = css;
        if (nm && nm !== 'shockblade') document.documentElement.setAttribute('data-theme', nm);
      } catch (e) { }
    }

    var lastPainted = null;
    function paint(j) {
      if (!j) return;
      var name = forced || j.active || 'shockblade';
      var def = NPO.findTheme(j, name) || { name: name };
      var key;
      try { key = name + '|' + JSON.stringify(def[mode] || {}); }
      catch (e) { key = name; }
      if (key === lastPainted) return;
      lastPainted = key;
      NPO.applyThemeDef(def, mode, document);
      if (mode === 'dashboard') {
        try {
          localStorage.setItem('nowplaying.theme', name);
          localStorage.setItem('nowplaying.themeCss', cssFor(def, mode));
        } catch (e) { }
      }
    }

    if (mode === 'source') {
      /* Chained on completion, not a fixed-rate interval - the pattern every
         data poll in this app already uses. OBS runs every browser source in
         one Chromium sharing six connections to this address, so a slow answer
         (the app rebuilding itself during an in-app update, say) used to have
         each source stacking a fresh /themes request every 5s onto a queue
         that could not drain, and two overlapping answers could resolve out of
         order and paint a stale palette. One request in flight per source. */
      (function poll() {
        var done = false;
        function next(ms) { if (done) return; done = true; setTimeout(poll, ms); }
        /* ...with a watchdog, because the other failure mode of chaining is
           worse than the one it fixes: a fetch that never settles at all would
           otherwise end theme polling for this source permanently, and a theme
           switch would never again reach the thing actually on stream. */
        var guard = setTimeout(function () { next(0); }, 20000);
        try {
          NPO.themes(function (j) {
            clearTimeout(guard);
            /* Arm the next poll BEFORE painting. paint() writes to the DOM,
               and a throw in there would otherwise skip the scheduling and
               freeze this source on its current palette for the rest of the
               stream - the very thing the watchdog above exists to stop, let
               back in through the door it was guarding. */
            next(5000);
            paint(j);
          });
        } catch (e) { clearTimeout(guard); next(5000); }
      })();
    } else {
      NPO.themes(paint);
    }
  };
})();
