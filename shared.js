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
      the fallback for anything old enough to lack WebSocket. A page with no
      connection stays quiet rather than announce the problem over a live
      stream, so reconnects are silent and endless.

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

  /* ---- live streams (spectrum, twitch events) ------------------------------ */

  NPO.stream = function (path, onJson, opts) {
    opts = opts || {};
    var retry = opts.retryMs || 3000;
    function handle(ev) {
      var d;
      try { d = JSON.parse(ev.data); } catch (e) { return; }
      if (d) onJson(d);
    }
    if (window.WebSocket) {
      (function connect() {
        var ws = new WebSocket('ws://' + location.host + path);
        ws.onmessage = handle;
        // Unlike EventSource, a WebSocket does not retry on its own.
        ws.onclose = function () {
          if (opts.onDown) opts.onDown();
          setTimeout(connect, retry);
        };
      })();
    } else if (window.EventSource) {
      var es = new EventSource(path);
      es.onmessage = handle;
      // EventSource reconnects on its own; only report the gap.
      es.onerror = function () { if (opts.onDown) opts.onDown(); };
    }
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
    // theme=glass|solid|minimal are per-source chrome variants handled by the
    // pages themselves, not palettes; never treat them as a palette pin.
    if (forced === 'glass' || forced === 'solid' || forced === 'minimal') forced = null;

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
      (function poll() {
        NPO.themes(paint);
        setTimeout(poll, 5000);
      })();
    } else {
      NPO.themes(paint);
    }
  };
})();
