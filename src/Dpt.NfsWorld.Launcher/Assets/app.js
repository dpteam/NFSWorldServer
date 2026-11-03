/* =========================================================================
   DPT.Server launcher - ES5 (IE11 / MSHTML), bridge = window.external
   ========================================================================= */
(function (global) {
  'use strict';

  var ext = global.external || {};
  var booted = false;
  var state = {
    servers: [],
    selected: 0,
    mode: 'local',
    sound: true,
    checkPoll: null,
    playPoll: null,
    lastCheckSeq: -1,
    lastPlaySeq: -1,
    autoTimer: null,
    browseBusy: false
  };

  function $(id) { return document.getElementById(id); }

  function pad(n) { return n < 10 ? '0' + n : '' + n; }

  function log(text, cls) {
    var box = $('log');
    if (!box) return;
    var line = document.createElement('div');
    if (cls) line.className = cls;
    var d = new Date();
    line.textContent = pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds()) + '  ' + text;
    box.appendChild(line);
    box.scrollTop = box.scrollHeight;
    while (box.childNodes.length > 250) box.removeChild(box.firstChild);
  }

  function setText(id, text) { var e = $(id); if (e) e.textContent = text; }

  function parseLines(text) {
    var out = {};
    if (!text) return out;
    var lines = String(text).split(/\r?\n/);
    for (var i = 0; i < lines.length; i++) {
      var idx = lines[i].indexOf('=');
      if (idx <= 0) continue;
      out[lines[i].substring(0, idx).trim()] = lines[i].substring(idx + 1);
    }
    return out;
  }

  function esc(s) {
    return String(s === null || s === undefined ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
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

  // --------------------------------------------------------------- servers

  function parseServers(text) {
    var list = [];
    if (!text) return list;
    var chunks = text.split(';;');
    for (var i = 0; i < chunks.length; i++) {
      if (!chunks[i]) continue;
      var p = chunks[i].split('|');
      if (p.length < 2 || !p[1]) continue;
      list.push({
        name: p[0] || p[1],
        url: p[1],
        mode: (p[2] || 'remote').toLowerCase() === 'local' ? 'local' : 'remote',
        builtin: p[3] === '1'
      });
    }
    return list;
  }

  function serversString() {
    var parts = [];
    for (var i = 0; i < state.servers.length; i++) {
      var s = state.servers[i];
      parts.push(clean(s.name) + '|' + clean(s.url) + '|' + s.mode + '|' + (s.builtin ? '1' : '0'));
    }
    return parts.join(';;');
  }

  function clean(v) {
    return String(v === null || v === undefined ? '' : v).replace(/\|/g, '/').replace(/;/g, ',').replace(/[\r\n]/g, ' ').trim();
  }

  function renderServers() {
    var sel = $('serverSel');
    if (!sel) return;
    while (sel.options.length) sel.remove(0);
    for (var i = 0; i < state.servers.length; i++) {
      var o = document.createElement('option');
      o.value = String(i);
      o.text = state.servers[i].name;
      sel.add(o, null);
    }
    if (state.selected >= state.servers.length) state.selected = 0;
    sel.selectedIndex = state.selected;
    applyServer(state.selected);
  }

  function applyServer(index) {
    if (index < 0 || index >= state.servers.length) return;
    state.selected = index;
    var s = state.servers[index];
    state.mode = s.mode;
    if ($('serverUrl')) $('serverUrl').value = s.url;
    var hint = $('serverHint');
    if (hint) {
      hint.innerHTML = s.mode === 'local'
        ? 'Локальный режим: лаунчер сам поднимет <b>NFSWorldServer.exe --offline</b> (порты 44620+, сохранения в <b>data\\offline</b>).'
        : 'Удалённый сервер: игра подключится к этому адресу. Порт игрового endpoint — <b>44600</b>.';
    }
  }

  function addServer() {
    var name = global.prompt('Название сервера:', 'Мой сервер');
    if (name === null) return;
    var url = global.prompt('Адрес сервера (домен или URL):', 'http://example.com:44600');
    if (url === null) return;
    url = clean(url);
    if (!url) { log('пустой адрес — не добавил', 'err'); return; }
    if (url.indexOf('://') < 0) url = 'http://' + url;
    state.servers.push({ name: clean(name) || url, url: url, mode: 'remote', builtin: false });
    state.selected = state.servers.length - 1;
    renderServers();
    save(true);
    log('добавлен сервер: ' + name + ' -> ' + url, 'ok');
    check(false);
  }

  function removeServer() {
    var s = state.servers[state.selected];
    if (!s) return;
    if (s.builtin) { log('встроенный сервер удалить нельзя', 'warn'); return; }
    if (!global.confirm('Удалить «' + s.name + '»?')) return;
    state.servers.splice(state.selected, 1);
    if (state.selected >= state.servers.length) state.selected = state.servers.length - 1;
    renderServers();
    save(true);
    log('сервер удалён: ' + s.name, 'warn');
  }

  // -------------------------------------------------------------- settings

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
    // keep the selected entry in sync with the URL field
    var s = state.servers[state.selected];
    if (s) {
      s.url = c.serverUrl;
      state.mode = s.mode;
    }
    if (ext.SaveSettings) {
      try {
        ext.SaveSettings(c.serverUrl, c.gamePath, c.region, c.extraArgs, c.theme, c.sound, c.closeOnPlay,
                         serversString(), String(state.selected));
      } catch (e) { log('save: ' + e.message, 'err'); }
    }
    if (!quiet) { log('настройки сохранены', 'ok'); beep('ok'); }
  }

  function applySettings(s) {
    state.sound = s.sound !== '0';
    document.body.className = (s.theme === 'light') ? 'light' : 'dark';
    if ($('gamePath')) $('gamePath').value = s.gamePath || 'nfsw\\nfsw.exe';
    if ($('region')) $('region').value = (s.region || 'US').toUpperCase();
    if ($('extraArgs')) $('extraArgs').value = s.extraArgs || '';
    if ($('optSound')) $('optSound').checked = state.sound;
    if ($('optClose')) $('optClose').checked = s.closeOnPlay !== '0';

    state.servers = parseServers(s.servers);
    if (!state.servers.length) {
      state.servers = [
        { name: 'Локальный сервер', url: 'http://127.0.0.1:44620', mode: 'local', builtin: true },
        { name: 'DPT.Server (домен)', url: 'http://nfsw.dartpower.xyz:44600', mode: 'remote', builtin: true }
      ];
    }
    state.selected = parseInt(s.selected, 10);
    if (isNaN(state.selected) || state.selected < 0 || state.selected >= state.servers.length) state.selected = 0;
    if (s.serverUrl) state.servers[state.selected].url = s.serverUrl;
    renderServers();
  }

  // ----------------------------------------------------------------- checks

  function setStatus(kind, text) {
    var dot = $('statusDot');
    if (dot) dot.className = 'dot ' + (kind === 'ok' ? 'ok' : kind === 'busy' ? 'warn' : '');
    setText('statusText', text);
    setText('connLabel', kind === 'ok' ? ('подключено к ' + serverBase()) : (kind === 'busy' ? 'проверяю…' : text));
  }

  function serverBase() {
    var u = $('serverUrl') ? String($('serverUrl').value).replace(/^\s+|\s+$/g, '') : '';
    if (!u) return 'http://127.0.0.1:44620';
    if (u.indexOf('://') < 0) u = 'http://' + u;
    while (u.length > 1 && u.charAt(u.length - 1) === '/') u = u.substring(0, u.length - 1);
    var i = u.toLowerCase().indexOf('/engine.svc');
    if (i >= 0) u = u.substring(0, i);
    return u;
  }

  function check(manual) {
    if (!ext.StartCheck) { log('нет моста window.external', 'err'); setStatus('bad', 'нет моста'); return; }
    if (manual) log('проверяю ' + serverBase() + ' …');
    setStatus('busy', 'проверка…');
    try { ext.StartCheck(serverBase()); } catch (e) { setStatus('bad', 'ошибка'); log('check: ' + e.message, 'err'); return; }

    if (state.checkPoll) global.clearInterval(state.checkPoll);
    state.checkPoll = global.setInterval(function () {
      var raw;
      try { raw = ext.GetCheckResult(); } catch (e) { return; }
      if (!raw) return;
      var nl = raw.indexOf('\n');
      var head = parseLines(raw.substring(0, nl));
      if (head.seq && parseInt(head.seq, 10) === state.lastCheckSeq && !manual) { /* keep waiting */ }
      var body = parseLines(raw.substring(nl + 1));
      if (body.state === 'running') return;
      global.clearInterval(state.checkPoll);
      state.checkPoll = null;
      if (body.state === 'ok') {
        setStatus('ok', 'онлайн');
        var st = null;
        try { st = JSON.parse(body.status); } catch (e) { st = null; }
        if (st) renderStatus(st);
        if (manual) log('сервер отвечает', 'ok');
        beep('ok');
        loadNews(serverBase());
      } else {
        setStatus('bad', 'нет связи');
        setText('kOnline', '-'); setText('kPersonas', '-'); setText('kRooms', '-');
        setText('kUptime', '-'); setText('kVersion', '-');
        setSvc('svcEngine', false); setSvc('svcXmpp', false); setSvc('svcRelay', false);
        log('ошибка: ' + (body.message || 'сервер недоступен'), 'err');
        if (manual) beep('err');
      }
    }, 300);
  }

  function setSvc(id, ok) {
    var e = $(id);
    if (e) e.className = 'svc ' + (ok ? 'ok' : 'bad');
  }

  function renderStatus(s) {
    var c = s.counts || {};
    var sv = s.services || {};
    setText('kOnline', c.online === undefined ? '-' : String(c.online));
    setText('kPersonas', c.personas === undefined ? '-' : String(c.personas));
    setText('kRooms', c.rooms === undefined ? '-' : String(c.rooms));
    setText('kUptime', s.uptime || '-');
    setText('kVersion', 'v' + (s.version || '?') + ' / ' + (s.buildDate || '?'));
    setSvc('svcEngine', !!sv.engine);
    setSvc('svcXmpp', !!sv.xmpp);
    setSvc('svcRelay', !!sv.relay);
  }

  function loadNews(base) {
    var box = $('newsBox');
    if (!box || !ext.HttpGet) return;
    var xml = ext.HttpGet(base + '/Engine.svc/LoginAnnouncements', 3000);
    if (!xml || xml.indexOf('ERROR') === 0 || xml.indexOf('<') < 0) return;
    try {
      var doc = null;
      if (global.DOMParser) doc = new global.DOMParser().parseFromString(xml, 'text/xml');
      else { doc = new ActiveXObject('Microsoft.XMLDOM'); doc.async = false; doc.loadXML(xml); }
      if (!doc) return;
      var items = doc.getElementsByTagName('NewsArticleTrans');
      if (!items || items.length === 0) return;
      var html = '';
      for (var i = 0; i < items.length && i < 3; i++) {
        html += '<div class="news-item"><div class="news-title">' + esc(tagText(items[i], 'Title')) +
                '</div><div class="news-body">' + esc(tagText(items[i], 'Body')) + '</div></div>';
      }
      if (html) box.innerHTML = html;
    } catch (e) { }
  }

  function tagText(node, name) {
    var list = node.getElementsByTagName(name);
    if (!list || list.length === 0) return '';
    return list[0].textContent || list[0].text || '';
  }

  // ------------------------------------------------------------------- play

  function play() {
    if (!ext.BeginPlay) { log('нет моста window.external', 'err'); return; }
    var c = collect();
    save(true);
    log('запуск: ' + (state.mode === 'local' ? 'локальный сервер' : c.serverUrl));
    var btn = $('btnPlay');
    if (btn) { btn.disabled = true; btn.innerHTML = '&#8987; Запуск…'; }
    setStatus('busy', 'запускаю…');
    try { ext.BeginPlay(c.serverUrl, c.gamePath, c.region, c.extraArgs, state.mode); }
    catch (e) { log('play: ' + e.message, 'err'); resetPlay(btn); return; }

    if (state.playPoll) global.clearInterval(state.playPoll);
    state.playPoll = global.setInterval(function () {
      var raw;
      try { raw = ext.GetPlayResult(); } catch (e) { return; }
      if (!raw) return;
      var nl = raw.indexOf('\n');
      var body = parseLines(raw.substring(nl + 1));
      if (body.state === 'running' || body.state === 'idle') return;
      global.clearInterval(state.playPoll);
      state.playPoll = null;
      resetPlay(btn);
      if (body.state === 'ok') {
        setStatus('ok', 'онлайн');
        log('игра запущена: ' + (body.exe || ''), 'ok');
        log('аргументы: ' + (body.args || ''), '');
        beep('ok');
        if ($('optClose') && $('optClose').checked && ext.CloseApp) {
          global.setTimeout(function () { try { ext.CloseApp(); } catch (e) { } }, 1200);
        }
      } else {
        setStatus('bad', 'ошибка');
        log('не удалось: ' + (body.message || '?'), 'err');
        beep('err');
      }
    }, 350);
  }

  function resetPlay(btn) {
    if (!btn) return;
    btn.disabled = false;
    btn.innerHTML = '&#9654; Играть';
  }

  // ------------------------------------------------------------------- wire

  function browse() {
    if (state.browseBusy) return;
    state.browseBusy = true;
    try {
      var path = ext.BrowseGame ? ext.BrowseGame() : '';
      if (path && path.indexOf('ERROR') !== 0) {
        if ($('gamePath')) $('gamePath').value = path;
        save(true);
        log('путь к игре: ' + path, 'ok');
      }
    } finally {
      state.browseBusy = false;
    }
  }

  function openSite() {
    if (ext.OpenLink) ext.OpenLink(serverBase() + '/');
  }

  function toggleTheme() {
    document.body.className = document.body.className === 'light' ? 'dark' : 'light';
    save(true);
  }

  function wire() {
    var sel = $('serverSel');
    if (sel) sel.onchange = function () { applyServer(this.selectedIndex); save(true); check(false); };
    if ($('btnAddServer')) $('btnAddServer').onclick = addServer;
    if ($('btnDelServer')) $('btnDelServer').onclick = removeServer;
    if ($('btnPlay')) $('btnPlay').onclick = play;
    if ($('btnCheck')) $('btnCheck').onclick = function () { check(true); };
    if ($('btnSave')) $('btnSave').onclick = function () { save(false); };
    if ($('btnBrowse')) $('btnBrowse').onclick = browse;
    if ($('btnSite')) $('btnSite').onclick = openSite;
    if ($('btnTheme')) $('btnTheme').onclick = toggleTheme;
    if ($('btnQuit')) $('btnQuit').onclick = function () { if (ext.CloseApp) ext.CloseApp(); };

    var fields = ['serverUrl', 'gamePath', 'region', 'extraArgs'];
    for (var i = 0; i < fields.length; i++) {
      var el = $(fields[i]);
      if (!el) continue;
      el.onchange = function () { save(true); };
      el.onblur = function () { save(true); };
    }
    if ($('optSound')) $('optSound').onchange = function () { state.sound = this.checked; save(true); };
    if ($('optClose')) $('optClose').onchange = function () { save(true); };
  }

  function boot() {
    if (booted) return;
    booted = true;

    var s = {};
    try { s = parseLines(ext.GetSettings ? ext.GetSettings() : ''); } catch (e) { s = {}; }
    applySettings(s);
    wire();

    log('DPT.Server launcher v' + (ext.Version ? ext.Version() : '?'));
    log('bridge: ' + (ext.BeginPlay ? 'ok' : 'MISSING (window.external)'), ext.BeginPlay ? 'ok' : 'err');

    check(false);
    state.autoTimer = global.setInterval(function () {
      if (!state.checkPoll) check(false);
    }, 15000);

    global.onbeforeunload = function () { save(true); };
  }

  if (document.readyState === 'complete' || document.readyState === 'interactive') boot();
  else if (global.attachEvent) global.attachEvent('onload', boot);
  else global.addEventListener('load', boot, false);
})(this);