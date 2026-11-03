/* =========================================================================
   DPT.Server launcher page - ES5, no frameworks.
   The heavy lifting (game process, offline server) is done by the dptnfsw://
   protocol handler; this page only chooses a server and builds the link.
   ========================================================================= */
(function (global) {
  'use strict';

  var UI = global.UI;
  var KEY = 'dpt.launch.servers';
  var state = { servers: [], selected: 0 };

  function $(id) { return document.getElementById(id); }

  function defaults() {
    var list = [
      { name: 'Локальный сервер', url: 'http://127.0.0.1:44620', mode: 'local', builtin: true },
      { name: 'DPT.Server (домен)', url: 'http://nfsw.dartpower.xyz:44600', mode: 'remote', builtin: true }
    ];
    try {
      var here = global.location.origin;
      if (here && here.indexOf('http') === 0 && list[1].url.indexOf(global.location.hostname) < 0) {
        list.push({ name: 'Этот сервер (' + global.location.host + ')', url: here, mode: 'remote', builtin: true });
      }
    } catch (e) { }
    return list;
  }

  function load() {
    var raw = UI.Store.get(KEY, '');
    if (!raw) { state.servers = defaults(); return; }
    try {
      var parsed = JSON.parse(raw);
      state.servers = (parsed && parsed.length) ? parsed : defaults();
    } catch (e) { state.servers = defaults(); }
  }

  function save() {
    UI.Store.set(KEY, JSON.stringify(state.servers));
  }

  function render() {
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
    apply();
    renderList();
  }

  function renderList() {
    var box = $('serverList');
    if (!box) return;
    var html = '';
    for (var i = 0; i < state.servers.length; i++) {
      var s = state.servers[i];
      html += '<div class="li">' +
        '<i class="fas ' + (s.mode === 'local' ? 'fa-server' : 'fa-tower-broadcast') + ' ico"></i>' +
        '<div class="txt"><b>' + UI.esc(s.name) + '</b>' +
        '<small>' + UI.esc(s.url) + ' · ' + (s.mode === 'local' ? 'локальный' : 'удалённый') + '</small></div>' +
        '</div>';
    }
    box.innerHTML = html || '<div class="empty small">пусто</div>';
  }

  function protocolUrl() {
    var s = state.servers[state.selected];
    if (!s) return 'dptnfsw://play';
    return 'dptnfsw://play?server=' + encodeURIComponent(s.url) +
           '&local=' + (s.mode === 'local' ? '1' : '0') + '&region=US';
  }

  function apply() {
    var s = state.servers[state.selected];
    if (!s) return;
    var link = $('btnPlay');
    if (link) link.href = protocolUrl();
    UI.setText('modeBadge', s.mode === 'local' ? 'локальный' : 'удалённый');
    UI.setText('addrText', s.url);
    var hint = $('serverHint');
    if (hint) {
      hint.innerHTML = s.mode === 'local'
        ? 'Локальный режим: обработчик поднимет <b>NFSWorldServer.exe --offline</b> (порты 44620+, сохранения в <b>data\\offline</b>) и запустит игру.'
        : 'Игра подключится к <b>' + UI.esc(s.url) + '</b>. Порт игрового endpoint — <b>44600</b>.';
    }
    check(false);
  }

  function check(manual) {
    var s = state.servers[state.selected];
    if (!s) return;
    var sameOrigin = false;
    try { sameOrigin = (new URL(s.url)).origin === global.location.origin; } catch (e) { sameOrigin = false; }
    if (!sameOrigin) {
      UI.setText('serverState', 'не проверяется из браузера (другой домен)');
      var pill0 = $('connPill');
      if (pill0) { pill0.className = 'conn-pill'; UI.setText('connText', 'не проверено'); }
      if (manual) UI.toast('Проверить можно только сервер, с которого открыта эта страница', 'warn');
      return;
    }
    UI.setText('serverState', 'проверка…');
    fetch('/api/health', { cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (j) {
      var ok = j && j.ok;
      UI.setText('serverState', ok ? 'онлайн' : 'ошибка');
      var pill = $('connPill');
      if (pill) pill.className = 'conn-pill ' + (ok ? 'ok' : 'bad');
      UI.setText('connText', ok ? 'онлайн' : 'нет связи');
      if (manual) UI.toast(ok ? 'сервер отвечает' : 'сервер не отвечает', ok ? 'ok' : 'err');
    }).catch(function (e) {
      UI.setText('serverState', 'нет связи');
      var pill = $('connPill');
      if (pill) pill.className = 'conn-pill bad';
      UI.setText('connText', 'нет связи');
      if (manual) UI.toast('нет связи: ' + e.message, 'err');
    });
  }

  function addServer() {
    var name = global.prompt('Название сервера:', 'Мой сервер');
    if (name === null) return;
    var url = global.prompt('Адрес (домен или URL):', 'http://example.com:44600');
    if (url === null) return;
    url = String(url).replace(/^\s+|\s+$/g, '');
    if (!url) return;
    if (url.indexOf('://') < 0) url = 'http://' + url;
    state.servers.push({ name: name || url, url: url, mode: 'remote', builtin: false });
    state.selected = state.servers.length - 1;
    save();
    render();
    UI.toast('сервер добавлен', 'ok');
  }

  function removeServer() {
    var s = state.servers[state.selected];
    if (!s) return;
    if (s.builtin) { UI.toast('встроенный сервер удалить нельзя', 'warn'); return; }
    if (!global.confirm('Удалить «' + s.name + '»?')) return;
    state.servers.splice(state.selected, 1);
    if (state.selected >= state.servers.length) state.selected = state.servers.length - 1;
    save();
    render();
  }

  function syncThemeIcon() {
    var btn = $('btnTheme');
    if (!btn) return;
    var theme = document.documentElement.getAttribute('data-theme');
    var i = btn.querySelector('i');
    if (i) i.className = 'fas ' + (theme === 'light' ? 'fa-moon' : 'fa-sun');
  }

  function boot() {
    load();
    UI.tabs();
    render();
    UI.selectTab('play');
    UI.applyTheme(UI.Store.get('dpt.theme', 'light'));
    syncThemeIcon();
    if ($('btnTheme')) $('btnTheme').onclick = function () { UI.nextTheme(); syncThemeIcon(); };
    if ($('serverSel')) $('serverSel').onchange = function () { state.selected = this.selectedIndex; apply(); };
    if ($('btnAdd')) $('btnAdd').onclick = addServer;
    if ($('btnDel')) $('btnDel').onclick = removeServer;
    if ($('btnCheck')) $('btnCheck').onclick = function () { check(true); };
    check(false);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot, false);
  else boot();
})(this);