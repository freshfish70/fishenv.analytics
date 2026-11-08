/*
 * Privacy-friendly analytics tracker. No cookies, no local storage writes, no fingerprinting.
 *
 *   <script defer data-site="example.com" src="https://analytics.example.com/script.js"></script>
 *
 * Options (all optional):
 *   data-site="example.com"   site identifier; defaults to location.hostname
 *   data-api="https://..."    event endpoint; defaults to <script origin>/api/event
 *   data-allow-local          also record events from localhost / file://
 *   data-global="name"        name of the global function for custom events; default "analytics"
 *   data-outbound="off"       do not record clicks on outbound links and file downloads
 *   data-user="42"            logged-in user id (only stored for sites with identify_users enabled)
 *
 * Custom events:
 *   analytics('signup', { plan: 'pro' })      (not defined if the page already has a global "analytics",
 *                                              e.g. Segment; use data-global to pick another name)
 *   <button data-track="signup_click" data-track-plan="pro">   (one event per click, props from data-track-*)
 *
 * Automatic events (unless data-outbound="off"), both with the prop url = host + path (no query string):
 *   outbound_link    click on a link to another host
 *   file_download    click on a link to a file (pdf, zip, csv, ...)
 *
 * Identified users (internal sites): analytics.identify('42') after login, analytics.identify(null) after logout.
 */
(function () {
  'use strict';

  var script = document.currentScript;
  if (!script) return;

  var endpoint = script.getAttribute('data-api') || new URL(script.src, location.href).origin + '/api/event';
  var site = script.getAttribute('data-site') || location.hostname;
  var allowLocal = script.hasAttribute('data-allow-local');
  var user = script.getAttribute('data-user') || null;
  var lastKey = null;
  var first = true;

  function skip() {
    if (!allowLocal && (location.protocol === 'file:' || /^(localhost|127\.|\[::1\]|$)/.test(location.hostname))) return true;
    if (navigator.webdriver || window._phantom || window.__nightmare || window.Cypress) return true;
    try {
      if (localStorage.getItem('analytics_ignore') === 'true') return true;
    } catch (e) { /* storage unavailable */ }
    return false;
  }

  function send(name, props, referrer) {
    if (skip()) return;
    var payload = { site: site, name: name, url: location.href, referrer: referrer || '' };
    if (props) payload.props = props;
    if (user) payload.user = user;
    try {
      // text/plain keeps this a "simple" CORS request (no preflight); the server parses the body as JSON.
      fetch(endpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'text/plain' },
        body: JSON.stringify(payload),
        keepalive: true,
        credentials: 'omit'
      }).catch(function () {});
    } catch (e) { /* never break the host page */ }
  }

  function pageview() {
    var key = location.pathname + location.search;
    if (key === lastKey) return;
    lastKey = key;
    send('pageview', null, first ? document.referrer : '');
    first = false;
  }

  // Single-page apps: record client-side navigations.
  var pushState = history.pushState;
  if (pushState) {
    history.pushState = function () {
      pushState.apply(this, arguments);
      pageview();
    };
    window.addEventListener('popstate', pageview);
  }

  // Declarative click tracking.
  document.addEventListener('click', function (e) {
    var el = e.target && e.target.closest ? e.target.closest('[data-track]') : null;
    if (!el) return;
    var name = el.getAttribute('data-track');
    if (!name) return;

    var props = {};
    var has = false;
    for (var i = 0; i < el.attributes.length; i++) {
      var attr = el.attributes[i];
      if (attr.name.indexOf('data-track-') === 0 && attr.name.length > 11) {
        props[attr.name.slice(11)] = attr.value;
        has = true;
      }
    }
    send(name, has ? props : null, '');
  }, true);

  // Outbound links and file downloads. auxclick covers middle-click (open in new tab).
  var FILE_EXT = /\.(pdf|zip|gz|tgz|bz2|xz|rar|7z|dmg|exe|msi|pkg|deb|rpm|apk|iso|csv|xlsx?|docx?|pptx?|odt|ods|odp|txt|epub|mp3|wav|mp4|mov|avi|mkv)$/i;
  function linkClick(e) {
    if (e.type === 'auxclick' && e.button !== 1) return;
    var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
    if (!a) return;
    var u;
    try { u = new URL(a.href, location.href); } catch (err) { return; }
    if (u.protocol !== 'http:' && u.protocol !== 'https:') return;
    var target = { url: u.host + u.pathname };
    if (FILE_EXT.test(u.pathname)) send('file_download', target, '');
    else if (u.host !== location.host) send('outbound_link', target, '');
  }
  if (script.getAttribute('data-outbound') !== 'off') {
    document.addEventListener('click', linkClick, true);
    document.addEventListener('auxclick', linkClick, true);
  }

  var globalName = script.getAttribute('data-global') || 'analytics';
  if (typeof window[globalName] === 'undefined') {
    var api = function (name, props) {
      send(name, props || null, '');
    };
    api.identify = function (id) {
      user = id === null || id === undefined || id === '' ? null : String(id);
    };
    window[globalName] = api;
  } else if (window.console && console.warn) {
    console.warn('analytics: window.' + globalName + ' already exists; set data-global to expose custom events under another name');
  }

  // Speculation-rules prerendering: the page may never be shown, so wait until it is activated.
  if (document.prerendering) {
    document.addEventListener('prerenderingchange', pageview, { once: true });
  } else if (document.visibilityState === 'prerender') {
    // Older, pre-speculation-rules prerendering.
    document.addEventListener('visibilitychange', function once() {
      if (document.visibilityState === 'visible') {
        document.removeEventListener('visibilitychange', once);
        pageview();
      }
    });
  } else {
    pageview();
  }
})();
