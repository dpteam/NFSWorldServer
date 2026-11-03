/* =========================================================================
   DPT.Server - shared UI helpers (ES5, no dependencies)
   ========================================================================= */
(function (global) {
  'use strict';

  var Store = {
    get: function (key, def) {
      try {
        var v = global.localStorage ? global.localStorage.getItem(key) : null;
        return v === null ? def : v;
      } catch (e) { return def; }
    },
    set: function (key, value) {
      try { if (global.localStorage) global.localStorage.setItem(key, String(value)); } catch (e) { }
    },
    getBool: function (key, def) {
      var v = Store.get(key, null);
      if (v === null) return def;
      return v === '1' || v === 'true';
    }
  };

  var THEMES = ['light', 'dark', 'oled'];
  var THEME_ICON = { light: 'fa-moon', dark: 'fa-sun', oled: 'fa-sun' };

  function applyTheme(theme) {
    if (THEMES.indexOf(theme) < 0) theme = 'light';
    document.documentElement.setAttribute('data-theme', theme);
    Store.set('dpt.theme', theme);
    var btn = document.getElementById('btnTheme');
    if (btn) {
      var i = btn.querySelector('i');
      if (i) { i.className = 'fa ' + THEME_ICON[theme]; }
      btn.title = 'Theme: ' + theme + ' (click to change)';
    }
  }

  function nextTheme() {
    var cur = document.documentElement.getAttribute('data-theme') || 'light';
    var idx = THEMES.indexOf(cur);
    applyTheme(THEMES[(idx + 1) % THEMES.length]);
    if (global.Sound) global.Sound.click();
  }

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined && text !== null) e.textContent = String(text);
    return e;
  }

  function escapeHtml(s) {
    if (s === null || s === undefined) return '';
    return String(s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function toast(text, kind, ms) {
    var host = document.getElementById('toasts');
    if (!host) return;
    var t = el('div', 'toast ' + (kind || ''));
    var icon = el('i', 'fas ' + (kind === 'ok' ? 'fa-check' : kind === 'err' ? 'fa-xmark' : kind === 'warn' ? 'fa-circle-info' : 'fa-circle-info'));
    var body = el('div');
    body.appendChild(el('b', null, text));
    t.appendChild(icon);
    t.appendChild(body);
    host.appendChild(t);
    var life = ms || 4200;
    setTimeout(function () {
      t.classList.add('out');
      setTimeout(function () { if (t.parentNode) t.parentNode.removeChild(t); }, 240);
    }, life);
    while (host.children.length > 5) host.removeChild(host.firstChild);
  }

  function num(v) {
    if (v === null || v === undefined || v === '') return '0';
    var n = Number(v);
    if (!isFinite(n)) return String(v);
    return n.toLocaleString();
  }

  function bytes(v) {
    var n = Number(v) || 0;
    if (n < 1024) return n + ' B';
    if (n < 1048576) return (n / 1024).toFixed(1) + ' KB';
    if (n < 1073741824) return (n / 1048576).toFixed(1) + ' MB';
    return (n / 1073741824).toFixed(2) + ' GB';
  }

  function duration(sec) {
    var s = Math.max(0, Math.floor(Number(sec) || 0));
    if (s < 60) return s + 's';
    var m = Math.floor(s / 60);
    if (m < 60) return m + 'm ' + (s % 60) + 's';
    var h = Math.floor(m / 60);
    if (h < 24) return h + 'h ' + (m % 60) + 'm';
    return Math.floor(h / 24) + 'd ' + (h % 24) + 'h';
  }

  function timeOf(iso) {
    if (!iso) return '';
    var d = new Date(iso);
    if (isNaN(d.getTime())) return '';
    var p = function (n) { return n < 10 ? '0' + n : '' + n; };
    return p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
  }

  function api(path, options) {
    options = options || {};
    var headers = options.headers || {};
    headers['Accept'] = 'application/json';
    if (options.body) headers['Content-Type'] = 'application/json';
    if (options.token) headers['X-Admin-Token'] = options.token;
    return fetch(path, {
      method: options.method || 'GET',
      headers: headers,
      body: options.body ? JSON.stringify(options.body) : undefined,
      cache: 'no-store'
    }).then(function (r) {
      return r.text().then(function (text) {
        var data = null;
        try { data = text ? JSON.parse(text) : null; } catch (e) { data = { raw: text }; }
        if (!r.ok) {
          var err = new Error((data && data.error) || ('HTTP ' + r.status));
          err.status = r.status;
          err.data = data;
          throw err;
        }
        return data;
      });
    });
  }

  function setText(id, value) {
    var e = document.getElementById(id);
    if (e) e.textContent = value === undefined || value === null ? '' : String(value);
  }

  function on(id, evt, fn) {
    var e = document.getElementById(id);
    if (e) e.addEventListener(evt, fn, false);
    return e;
  }

  function tabs() {
    var buttons = document.querySelectorAll('[data-tab]');
    var i;
    for (i = 0; i < buttons.length; i++) {
      buttons[i].addEventListener('click', function () {
        var name = this.getAttribute('data-tab');
        selectTab(name);
        if (global.Sound) global.Sound.click();
      }, false);
    }
  }

  function selectTab(name) {
    var buttons = document.querySelectorAll('[data-tab]');
    var i;
    for (i = 0; i < buttons.length; i++) {
      if (buttons[i].getAttribute('data-tab') === name) buttons[i].classList.add('active');
      else buttons[i].classList.remove('active');
    }
    var panels = document.querySelectorAll('[data-panel]');
    for (i = 0; i < panels.length; i++) {
      if (panels[i].getAttribute('data-panel') === name) panels[i].classList.add('active');
      else panels[i].classList.remove('active');
    }
    Store.set('dpt.tab.' + (document.body.getAttribute('data-role') || 'public'), name);
  }

  function poll(fn, ms) {
    var stopped = false;
    function tick() {
      if (stopped) return;
      try { fn(); } catch (e) { /* never kill the timer */ }
      setTimeout(tick, ms);
    }
    tick();
    return function stop() { stopped = true; };
  }

  function debounce(fn, ms) {
    var t = null;
    return function () {
      var args = arguments, self = this;
      clearTimeout(t);
      t = setTimeout(function () { fn.apply(self, args); }, ms);
    };
  }

  global.UI = {
    Store: Store,
    themes: THEMES,
    applyTheme: applyTheme,
    nextTheme: nextTheme,
    el: el,
    esc: escapeHtml,
    toast: toast,
    num: num,
    bytes: bytes,
    duration: duration,
    timeOf: timeOf,
    api: api,
    setText: setText,
    on: on,
    tabs: tabs,
    selectTab: selectTab,
    poll: poll,
    debounce: debounce
  };
})(this);