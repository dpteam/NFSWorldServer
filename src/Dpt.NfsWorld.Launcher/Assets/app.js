/* =========================================================================
   DPT.Server launcher - ES5 only (IE11 / MSHTML), bridge = window.external
   ========================================================================= */
(function (global) {
  'use strict';

  var ext = global.external || {};
  var state = {
    settings: {},
    checkTimer: null,
    lastOnline: -1,
    baseUrl: '',
    sound: true
  };

  // ------------------------------------------------------------------ helpers

  function $(id) { return document.getElementById(id); }

  function log(text, cls) {
    var box = $('log');
    if (!box) return;
    var line = document.createElement('div');
    if (cls) line.className = cls;
    var d = new Date();
    var p = function (n) { return n < 10 ? '0' + n : '' + n; };
    line.textContent = p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds()) + '  ' + text;
    box.appendChild(line);
    box.scrollTop = box.scrollHeight;
    while (box.childNodes.length > 200) box.removeChild(box.firstChild);
  }

  function parseSettings(text) {
    var out = {};
    if (!text) return out;
    var lines = String(text).split(/\r?\n/);
    for (var i = 0; i < lines.length; i++) {
      var line = lines[i];
      var eq = line.indexOf('=');
      if (eq <= 0) continue;
      out[line.substring(0, eq).trim()] = line.substring(eq + 1).trim();
    }
    return out;
  }

  function trimSlash(u) {
    u = String(u || '').replace(/^\s+|\s+$/g, '');
    while (u.length > 1 && u.charAt(u.length - 1) === '/') u = u.substring(0, u.length - 1);
    return u;
  }

  function baseOf(url) {
    var u = trimSlash(url);
    if (!u) return 'http://127.0.0.1:44600';
    if (u.indexOf('://') < 0) u = 'http://' + u;
    var idx = u.toLowerCase().indexOf('/engine.svc');
    if (idx >= 0) u = u.substring(0, idx);
    return u;
  }

  function setText(id, text) { var e = $(id); if (e) e.textContent = text; }

  function setSvc(id, ok) {
    var e = $(id);
    if (!e) return;
    e.className = 'svc ' + (ok ? 'ok' : 'bad');
  }

  function beep(kind) {
    if (!state.sound) return;
    var AC = global.AudioContext || global.webkitAudioContext;
    if (!AC) return;
    try {
      var ctx = new AC();
      var osc = ctx.createOscillator();
      var gain = ctx.createGain();
      osc.type = 'sine';
      osc.frequency.value = kind === 'err' ? 260 : kind === 'ok' ? 720 : 520;
      gain.gain.setValueAtTime(0.0001, ctx.currentTime);
      gain.gain.exponentialRampToValueAtTime(0.12, ctx.currentTime + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.22);
      osc.connect(gain); gain.connect(ctx.destination);
      osc.start(); osc.stop(ctx.currentTime + 0.25);
    } catch (e) { }
  }

  // ----------------------------------------------------------------- settings

  function applySettings(s) {
    if ($('serverUrl')) $('serverUrl').value = s.serverUrl || '';
    if ($('gamePath')) $('gamePath').value = s.gamePath || '';
    if ($('region')) $('region').value = (s.region || 'US').toUpperCase();
    if ($('extraArgs')) $('extraArgs').value = s.extraArgs || '';
    if ($('optSound')) $('optSound').checked = s.sound !== '0';
    if ($('optClose')) $('optClose').checked = s.closeOnPlay !== '0';
    state.sound = s.sound !== '0';
    document.body.className = (s.theme === 'light') ? 'light' : 'dark';
  }

  function collect() {
    return {
      serverUrl: $('serverUrl') ? $('serverUrl').value : '',
      gamePath: $('gamePath') ? $('gamePath').value : '',
      region: $('region') ? $('region').value : 'US',
      extraArgs: $('extraArgs') ? $('extraArgs').value : '',
      theme: document.body.className === 'light' ? 'light' : 'dark',
      sound: ($('optSound') && $('optSound').checked) ? '1' : '0',
      closeOnPlay: ($('optClose') && $('optClose').checked) ? '1' : '0'
    };
  }

  function save(quiet) {
    var c = collect();
    if (ext.SaveSettings) {
      ext.SaveSettings(c.serverUrl, c.gamePath, c.region, c.extraArgs, c.theme, c.sound, c.closeOnPlay);
    }
    if (!quiet) { log('settings saved', 'ok'); beep('ok'); }
  }

  // ------------------------------------------------------------------ checks

  function check(manual) {
    var base = baseOf($('serverUrl') ? $('serverUrl').value : '');
    state.baseUrl = base;
    setText('statusText', 'checking...');
    var dot = $('statusDot');
    if (dot) dot.className = 'dot warn';
    if (manual) log('checking ' + base + ' ...');

    var health = ext.HttpGet ? ext.HttpGet(base + '/api/health') : 'ERROR: no bridge';
    if (!health || health.indexOf('ERROR') === 0) {
      setStatus(false, manual ? health : 'offline');
      return;
    }
    var status = ext.HttpGet ? ext.HttpGet(base + '/api/status') : 'ERROR: no bridge';
    if (!status || status.indexOf('ERROR') === 0) {
      setStatus(false, status || 'no status');
      return;
    }
    var s = null;
    try { s = JSON.parse(status); } catch (e) { s = null; }
    if (!s || !s.ok) { setStatus(false, 'bad status payload'); return; }

    setStatus(true, 'online');
    var c = s.counts || {};
    var sv = s.services || {};
    setText('kOnline', c.online === undefined ? '-' : String(c.online));
    setText('kPersonas', c.personas === undefined ? '-' : String(c.personas));
    setText('kRooms', c.rooms === undefined ? '-' : String(c.rooms));
    setText('kUptime', s.uptime || '-');
    setText('kVersion', 'v' + (s.version || '?') + ' / ' + (s.buildDate || ''));
    setSvc('svcEngine', !!sv.engine);
    setSvc('svcXmpp', !!sv.xmpp);
    setSvc('svcRelay', !!sv.relay);
    setText('connLabel', 'connected to ' + base);

    if (state.lastOnline >= 0 && c.online > state.lastOnline) beep('ok');
    state.lastOnline = c.online;

    if (manual) log('server is up: ' + (c.online || 0) + ' online', 'ok');
    loadNews(base);
  }

  function setStatus(ok, text) {
    var dot = $('statusDot');
    if (dot) dot.className = 'dot ' + (ok ? 'ok' : '');
    setText('statusText', ok ? 'online' : 'offline');
    setText('connLabel', ok ? ('connected to ' + state.baseUrl) : ('not reachable (' + text + ')'));
    if (!ok) {
      setSvc('svcEngine', false);
      setSvc('svcXmpp', false);
      setSvc('svcRelay', false);
    }
    if (!ok) log('server check failed: ' + text, 'err');
  }

  function loadNews(base) {
    var box = $('newsBox');
    if (!box) return;
    var xml = ext.HttpGet ? ext.HttpGet(base + '/Engine.svc/LoginAnnouncements') : '';
    if (!xml || xml.indexOf('ERROR') === 0 || xml.indexOf('<') < 0) return;
    try {
      var doc = null;
      if (global.DOMParser) doc = new DOMParser().parseFromString(xml, 'text/xml');
      else { doc = new ActiveXObject('Microsoft.XMLDOM'); doc.async = false; doc.loadXML(xml); }
      if (!doc) return;
      var items = doc.getElementsByTagName('NewsArticleTrans');
      if (!items || items.length === 0) return;
      var html = '';
      for (var i = 0; i < items.length && i < 4; i++) {
        var t = tagText(items[i], 'Title');
        var b = tagText(items[i], 'Body');
        html += '<div class="news-item"><div class="news-title">' + esc(t) + '</div>' +
                '<div class="news-body">' + esc(b) + '</div></div>';
      }
      if (html) box.innerHTML = html;
    } catch (e) { }
  }

  function tagText(node, name) {
    var list = node.getElementsByTagName(name);
    if (!list || list.length === 0) return '';
    return list[0].textContent || list[0].text || '';
  }

  function esc(s) {
    return String(s === null || s === undefined ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  // ------------------------------------------------------------------ actions

  function play() {
    var c = collect();
    save(true);
    log('launching nfsw.exe ...');
    var result = ext.Play ? ext.Play(c.serverUrl, c.gamePath, c.region, c.extraArgs) : 'ERROR: no bridge';
    if (result && result.indexOf('OK') === 0) {
      log(result.substring(3), 'ok');
      beep('ok');
    } else {
      log(result || 'launch failed', 'err');
      beep('err');
      var hint = $('gameHint');
      if (hint) hint.style.color = '#e07a7a';
    }
  }

  function browse() {
    var path = ext.BrowseGame ? ext.BrowseGame() : '';
    if (path && path.indexOf('ERROR') !== 0 && path !== '') {
      if ($('gamePath')) $('gamePath').value = path;
      save(true);
      log('game path: ' + path, 'ok');
    }
  }

  function openSite() {
    var base = baseOf($('serverUrl') ? $('serverUrl').value : '');
    if (ext.OpenLink) ext.OpenLink(base + '/');
  }

  function toggleTheme() {
    document.body.className = document.body.className === 'light' ? 'dark' : 'light';
    save(true);
  }

  // --------------------------------------------------------------------- boot

  function boot() {
    var s = {};
    try { s = parseSettings(ext.GetSettings ? ext.GetSettings() : ''); } catch (e) { s = {}; }
    state.settings = s;
    applySettings(s);

    if ($('btnPlay')) $('btnPlay').onclick = play;
    if ($('btnCheck')) $('btnCheck').onclick = function () { check(true); };
    if ($('btnSave')) $('btnSave').onclick = function () { save(false); };
    if ($('btnBrowse')) $('btnBrowse').onclick = browse;
    if ($('btnSite')) $('btnSite').onclick = openSite;
    if ($('btnTheme')) $('btnTheme').onclick = toggleTheme;
    if ($('btnQuit')) $('btnQuit').onclick = function () { if (ext.CloseApp) ext.CloseApp(); };
    if ($('optSound')) $('optSound').onchange = function () { state.sound = this.checked; save(true); };

    log('DPT.Server launcher v' + (ext.Version ? ext.Version() : '?') + ' ready');
    log('bridge: ' + (ext.Play ? 'ok' : 'MISSING (window.external)'), ext.Play ? 'ok' : 'err');

    setTimeout(function () { check(false); }, 400);
    state.checkTimer = setInterval(function () { check(false); }, 8000);
  }

  if (document.readyState === 'complete' || document.readyState === 'interactive') boot();
  else global.attachEvent ? global.attachEvent('onload', boot) : global.addEventListener('load', boot, false);
})(this);