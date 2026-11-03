/* =========================================================================
   DPT.Server launcher (WebView2). Talks to the host window with
   chrome.webview.postMessage - no COM, no protocol prompt.
   ========================================================================= */
(function (global) {
  'use strict';

  var wv = global.chrome && global.chrome.webview ? global.chrome.webview : null;
  var state = {
    servers: [],
    selected: 0,
    mode: 'local',
    sound: true,
    retryPlay: false,
    checking: false
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

  function post(msg) {
    if (!wv) { log('нет chrome.webview - страница открыта вне лаунчера', 'err'); return; }
    wv.postMessage(msg);
  }

  function clean(v) {
    return String(v === null || v === undefined ? '' : v)
      .replace(/\|/g, '/').replace(/;/g, ',').replace(/[\r\n]/g, ' ').trim();
  }

  /* --------------------------------------------------------------- servers */

  function renderServers() {
    var sel = $('serverSel');
    sel.options.length = 0;
    for (var i = 0; i < state.servers.length; i++) {
      var o = document.createElement('option');
      o.value = String(i);
      o.text = state.servers[i].name;
      sel.options[sel.options.length] = o;
    }
    if (state.selected >= state.servers.length) state.selected = 0;
    sel.selectedIndex = state.selected;
    applyServer(state.selected);
    renderList();
  }

  function renderList() {
    var box = $('serverList');
    if (!box) return;
    var html = '';
    for (var i = 0; i < state.servers.length; i++) {
      var s = state.servers[i];
      html += '<div class="li"><b>' + esc(s.name) + '</b> <span class="muted">' + esc(s.url) + '</span></div>';
    }
    box.innerHTML = html;
  }

  function applyServer(index) {
    if (index < 0 || index >= state.servers.length) return;
    state.selected = index;
    state.lastOk = undefined; // next answer is a fresh state, so it gets logged
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
    var name = prompt('Название сервера:', 'Мой сервер');
    if (name === null) return;
    var url = prompt('Адрес сервера (домен или URL):', 'http://example.com:44600');
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
    if (!confirm('Удалить «' + s.name + '»?')) return;
    state.servers.splice(state.selected, 1);
    if (state.selected >= state.servers.length) state.selected = state.servers.length - 1;
    renderServers();
    save(true);
    log('сервер удалён: ' + s.name, 'warn');
  }

  /* -------------------------------------------------------------- settings */

  function collect() {
    return {
      serverUrl: $('serverUrl') ? $('serverUrl').value : '',
      gamePath: $('gamePath') ? $('gamePath').value : '',
      region: $('region') ? $('region').value : 'US',
      extraArgs: $('extraArgs') ? $('extraArgs').value : '',
      theme: document.body.className === 'light' ? 'light' : 'dark',
      sound: ($('optSound') && $('optSound').checked),
      selected: state.selected,
      servers: state.servers
    };
  }

  function save(quiet) {
    var s = state.servers[state.selected];
    if (s) s.url = $('serverUrl') ? $('serverUrl').value : s.url;
    post({ action: 'save', data: collect() });
    if (!quiet) log('настройки сохранены', 'ok');
  }

  function applySettings(s) {
    document.body.className = s.theme === 'light' ? 'light' : 'dark';
    state.sound = s.sound !== false;
    if ($('gamePath')) $('gamePath').value = s.gamePath || 'nfsw\\nfsw.exe';
    if ($('region')) $('region').value = (s.region || 'US').toUpperCase();
    if ($('extraArgs')) $('extraArgs').value = s.extraArgs || '';
    if ($('optSound')) $('optSound').checked = state.sound;

    state.servers = [];
    var list = s.servers || [];
    for (var i = 0; i < list.length; i++) {
      state.servers.push({
        name: list[i].name, url: list[i].url,
        mode: list[i].mode === 'local' ? 'local' : 'remote',
        builtin: !!list[i].builtin
      });
    }
    state.selected = parseInt(s.selected, 10) || 0;
    if (state.servers.length === 0) {
      state.servers = [
        { name: 'Локальный сервер', url: 'http://127.0.0.1:44620', mode: 'local', builtin: true },
        { name: 'DPT.Server (домен)', url: 'http://nfsw.dartpower.xyz:44600', mode: 'remote', builtin: true }
      ];
    }
    // The selected server keeps its own URL: serverUrl is just a mirror of the address
    // field for the current session. Overwriting the bookmark here made "this PC" turn
    // back into the domain after every restart.
    if (s.serverUrl && state.servers[state.selected] && !state.servers[state.selected].url) {
      state.servers[state.selected].url = s.serverUrl;
    }
    renderServers();
    setTimeout(function () { state.checking = false; check(false); }, 60);
    setText('versionPill', 'v' + (s.version || '?'));
    log('DPT.Server launcher v' + (s.version || '?') + ' (WebView2 ' + (s.webview2 || '?') + ')');
    if (s.webview2) setText('connLabel', 'WebView2 ' + s.webview2);
  }

  /* ---------------------------------------------------------------- checks */

  function serverBase() {
    var u = $('serverUrl') ? String($('serverUrl').value).replace(/^\s+|\s+$/g, '') : '';
    if (!u) return 'http://127.0.0.1:44620';
    if (u.indexOf('://') < 0) u = 'http://' + u;
    while (u.length > 1 && u.charAt(u.length - 1) === '/') u = u.substring(0, u.length - 1);
    var i = u.toLowerCase().indexOf('/engine.svc');
    if (i >= 0) u = u.substring(0, i);
    return u;
  }

  /// The board always listens one port above the engine (44600 -> 44601,
  /// offline 44620 -> 44621), so the site button needs no extra setting.
  function boardUrl() {
    var base = serverBase();
    var m = /^(https?:\/\/[^\/:]+)(?::(\d+))?/i.exec(base);
    if (!m) return base + '/';
    var port = m[2] ? (parseInt(m[2], 10) + 1) : '';
    return m[1] + (port ? ':' + port : '') + '/';
  }

  function setStatus(kind, text) {
    var dot = $('statusDot');
    if (dot) dot.className = 'dot ' + (kind === 'ok' ? 'ok' : kind === 'busy' ? 'warn' : '');
    setText('statusText', text);
    if (kind === 'ok') setText('connLabel', 'подключено к ' + serverBase());
  }

  function check(manual) {
    if (state.checking) return;
    state.checking = true;
    if (manual) log('проверяю ' + serverBase() + ' …');
    setStatus('busy', 'проверка…');
    post({ action: 'check', server: serverBase() });
  }

  function onCheck(msg) {
    state.checking = false;
    var was = state.lastOk;          // undefined on the very first answer
    var now = !!msg.ok;
    state.lastOk = now;
    if (now) {
      setStatus('ok', 'онлайн');
      var st = null;
      try { st = JSON.parse(msg.status); } catch (e) { st = null; }
      if (st) renderStatus(st);
      if (was !== true) log('сервер отвечает: ' + (msg.server || serverBase()), 'ok');
    } else {
      setStatus('bad', 'нет связи');
      setText('kOnline', '-'); setText('kPersonas', '-'); setText('kRooms', '-');
      setText('kUptime', '-'); setText('kVersion', '-');
      setSvc('svcEngine', false); setSvc('svcXmpp', false); setSvc('svcRelay', false);
      // only on a state change - the 15s poll must not flood the log
      if (was !== false) log('сервер не отвечает: ' + (msg.server || serverBase()), 'err');
    }
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

  /* ------------------------------------------------------------------ play */

  function play() {
    var btn = $('btnPlay');
    if (btn) { btn.disabled = true; btn.innerHTML = '&#8987; Запуск…'; }
    setStatus('busy', 'запускаю…');
    log('запуск: ' + (state.mode === 'local' ? 'локальный сервер' : serverBase()));
    post({
      action: 'play',
      server: serverBase(),
      local: state.mode === 'local',
      region: $('region') ? $('region').value : 'US',
      extraArgs: $('extraArgs') ? $('extraArgs').value : '',
      gamePath: $('gamePath') ? $('gamePath').value : ''
    });
  }

  function resetPlay() {
    var btn = $('btnPlay');
    if (!btn) return;
    btn.disabled = false;
    btn.innerHTML = '&#9654; Играть';
  }

  function onPlay(msg) {
    resetPlay();
    if (msg.ok) {
      setStatus('ok', 'онлайн');
      log('игра запущена: ' + (msg.exe || ''), 'ok');
      log('аргументы: ' + (msg.args || ''));
      if ($('optClose') && $('optClose').checked) post({ action: 'close' });
      return;
    }
    if (msg.needGame) {
      log('укажите путь к nfsw.exe…', 'warn');
      state.retryPlay = true;
      post({ action: 'browse' });
      return;
    }
    setStatus('bad', 'ошибка');
    log('не удалось: ' + (msg.message || '?'), 'err');
  }

  function onGamePath(msg) {
    if (!msg.path) { resetPlay(); state.retryPlay = false; return; }
    if ($('gamePath')) $('gamePath').value = msg.path;
    log('путь к игре: ' + msg.path, 'ok');
    if (state.retryPlay) { state.retryPlay = false; play(); }
  }

  /* ------------------------------------------------------------------ wire */

  function esc(s) {
    return String(s === null || s === undefined ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  function onMessage(e) {
    var m = e && e.data ? e.data : null;
    if (!m || !m.type) return;
    switch (m.type) {
      case 'settings': applySettings(m); break;
      case 'check': onCheck(m); break;
      case 'play': onPlay(m); break;
      case 'playStarted': break;
      case 'gamePath': onGamePath(m); break;
      case 'handler': log(m.ok ? 'dptnfsw:// зарегистрирован' : ('не удалось: ' + m.message), m.ok ? 'ok' : 'err'); break;
      case 'error': log('ошибка: ' + m.message, 'err'); break;
    }
  }

  function boot() {
    if (!wv) {
      log('Откройте лаунчер через NFSWorldLauncher.exe (нет chrome.webview)', 'err');
      return;
    }
    wv.addEventListener('message', onMessage);
    wv.postMessage({ action: 'ready' });

    var sel = $('serverSel');
    if (sel) sel.onchange = function () { applyServer(this.selectedIndex); save(true); check(false); };
    if ($('btnAddServer')) $('btnAddServer').onclick = addServer;
    if ($('btnDelServer')) $('btnDelServer').onclick = removeServer;
    if ($('btnPlay')) $('btnPlay').onclick = play;
    if ($('btnCheck')) $('btnCheck').onclick = function () { state.checking = false; check(true); };
    if ($('btnSave')) $('btnSave').onclick = function () { save(false); };
    if ($('btnBrowse')) $('btnBrowse').onclick = function () { post({ action: 'browse' }); };
    if ($('btnSite')) $('btnSite').onclick = function () { post({ action: 'openSite', url: boardUrl() }); };
    if ($('btnTheme')) $('btnTheme').onclick = function () {
      document.body.className = document.body.className === 'light' ? 'dark' : 'light';
      save(true);
    };
    if ($('btnHandler')) $('btnHandler').onclick = function () { post({ action: 'installHandler' }); };
    if ($('btnQuit')) $('btnQuit').onclick = function () { post({ action: 'close' }); };

    var fields = ['serverUrl', 'gamePath', 'region', 'extraArgs'];
    for (var i = 0; i < fields.length; i++) {
      var el = $(fields[i]);
      if (!el) continue;
      el.onchange = function () { save(true); };
      el.onblur = function () { save(true); };
    }
    if ($('optSound')) $('optSound').onchange = function () { state.sound = this.checked; save(true); };
    setInterval(function () { if (!state.checking) check(false); }, 15000);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot, false);
  else boot();
})(this);