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
