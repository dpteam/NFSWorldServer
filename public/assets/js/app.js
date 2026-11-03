/* =========================================================================
   DPT.Server - board logic (public + admin), AdHoc visual language.
   No dependencies, ES5.
   ========================================================================= */
(function (global) {
  'use strict';

  var UI = global.UI;
  var Sound = global.Sound;
  var role = document.documentElement.getAttribute('data-role') || 'public';
  var isAdmin = role === 'admin';

  var state = {
    lastFeedId: 0,
    lastOnlineCount: -1,
    token: '',
    logsSince: 0,
    logsPaused: false,
    logsFilter: '',
    refreshMs: 3000,
    firstLoad: true,
    accountsAt: 0
  };

  var $ = function (id) { return document.getElementById(id); };

  // ---------------------------------------------------------------- settings

  function loadPrefs() {
    UI.applyTheme(UI.Store.get('dpt.theme', document.documentElement.getAttribute('data-theme') || 'light'));
    Sound.setEnabled(UI.Store.getBool('dpt.sound', true));
    Sound.setVolume(UI.Store.get('dpt.volume', 0.55));
    syncSound();
  }

  function syncSound() {
    var btn = $('btnSound');
    if (!btn) return;
    var on = Sound.isEnabled();
    btn.classList.toggle('muted', !on);
    var i = btn.querySelector('i');
    if (i) i.className = 'fas ' + (on ? 'fa-volume-high' : 'fa-volume-off');
    btn.title = on ? 'Звуки включены' : 'Звуки выключены';
  }

  function loadToken() {
    try { state.token = global.sessionStorage ? (global.sessionStorage.getItem('dpt.admin') || '') : ''; }
    catch (e) { state.token = ''; }
  }

  function saveToken(t) {
    state.token = t || '';
    try { if (global.sessionStorage) global.sessionStorage.setItem('dpt.admin', state.token); } catch (e) { }
  }

  // --------------------------------------------------------------- rendering

  function setChip(id, ok, label) {
    var e = $(id);
    if (!e) return;
    e.textContent = label;
    e.className = 'chip ' + (ok ? 'ok' : 'bad');
  }

  function renderStatus(s) {
    if (!s) return;
    var c = s.counts || {};
    var sv = s.services || {};
    var tr = s.traffic || {};

    UI.setText('sOnline', UI.num(c.online));
    UI.setText('sOnlineSub', UI.num(c.sessions) + ' сессий');
    UI.setText('sPersonas', UI.num(c.personas));
    UI.setText('sPersonasSub', UI.num(c.accounts) + ' аккаунтов');
    UI.setText('sCars', UI.num(c.cars));
    UI.setText('sCarsSub', 'в гаражах');
    UI.setText('sRooms', UI.num(c.rooms));
    UI.setText('sRoomsSub', UI.num(c.relayClients) + ' клиентов');
    UI.setText('sTraffic', UI.bytes(tr.relayIn));
    UI.setText('sTrafficSub', '↓' + UI.bytes(tr.relayIn) + ' ↑' + UI.bytes(tr.relayOut));
    UI.setText('sHealth', sv.engine ? 'ok' : 'down');
    UI.setText('sHealthSub', sv.engine ? 'сервер в сети' : 'engine недоступен');
    UI.setText('sRequests', UI.num(c.requests));
    UI.setText('sUptime', s.uptime || '–');
    UI.setText('sVersion', 'v' + (s.version || '?'));

    UI.setText('tUptime', s.uptime || '–');
    UI.setText('tOnline', UI.num(c.online));
    UI.setText('tPersonas', UI.num(c.personas));
    UI.setText('tEngine', sv.engine ? (sv.enginePort || 'on') : 'off');
    UI.setText('tXmpp', sv.xmpp ? (sv.xmppPort || 'on') : 'off');
    UI.setText('tRelay', sv.relay ? (sv.relayPort || 'on') : 'off');
    if (sv.enginePort) UI.setText('tPort', sv.enginePort);
    if (isAdmin) UI.setText('tWebPort', sv.webAdmin ? 'admin' : 'off');

    setChip('svcEngine', !!sv.engine, 'engine ' + (sv.enginePort || ''));
    setChip('svcXmpp', !!sv.xmpp, 'chat ' + (sv.xmppPort || ''));
    setChip('svcRelay', !!sv.relay, 'relay ' + (sv.relayPort || ''));
    setChip('svcPublic', !!sv.webPublic, 'public');
    setChip('svcAdmin', !!sv.webAdmin, 'admin');

    var pill = $('connPill');
    if (pill) {
      pill.className = 'conn-pill ' + (sv.engine ? 'ok' : 'bad');
      UI.setText('connText', sv.engine ? 'онлайн' : 'нет связи');
    }

    UI.setText('aProfile', s.defaultProfile || '–');
    UI.setText('aPublicUrl', s.publicUrl || '–');
    UI.setText('aVersion', s.version || '–');
    UI.setText('aBuild', s.buildDate || '–');
    UI.setText('aEnginePort', sv.enginePort || '–');
    UI.setText('aXmppPort', sv.xmppPort || '–');
    UI.setText('aRelayPort', sv.relayPort || '–');
    UI.setText('fName', s.name || 'DPT.Server');
    UI.setText('fVersion', 'v' + (s.version || '?'));
    UI.setText('fBuild', s.buildDate || '');
    UI.setText('fUptime', 'uptime ' + (s.uptime || '–'));
    UI.setText('hEngine', 'http://' + (global.location.hostname || 'localhost') + ':' + (sv.enginePort || 44600) + '/Engine.svc');

    if (isAdmin && s.server) {
      UI.setText('aPath', s.server.path);
      UI.setText('aLogDir', s.server.logDir);
      UI.setText('aState', s.server.stateDir);
      UI.setText('aMemory', UI.bytes(s.server.workingSet));
      UI.setText('aEngine', UI.num(s.server.engineRequests) + ' req / ' + UI.num(s.server.engineErrors) + ' err');
    }

    if (s.refreshMs) state.refreshMs = s.refreshMs;
    renderProfiles(s.profiles || []);
  }

  function renderProfiles(list) {
    var box = $('profilesBox');
    if (!box) return;
    if (!list.length) { box.innerHTML = '<div class="empty small">профилей нет</div>'; return; }
    var html = '';
    for (var i = 0; i < list.length; i++) {
      var p = list[i];
      html += '<div class="li">' +
        '<span class="avatar">' + UI.esc((p.build || p.id || '?').toString().substring(0, 4)) + '</span>' +
        '<div class="who"><b>' + UI.esc(p.name || p.id) + (p['default'] ? ' <span class="tag info">default</span>' : '') + '</b>' +
        '<span>' + UI.esc(p.id) + ' · ' + UI.esc(p.personaFormat || 'legacy') + (p.description ? ' · ' + UI.esc(p.description) : '') + '</span></div>' +
        '</div>';
    }
    box.innerHTML = html;
  }

  function renderPlayers(data) {
    var body = $('playersBody');
    if (!body) return;
    var players = (data && data.players) || [];
    var filter = ($('search') && $('search').value || '').toLowerCase();
    var shown = 0;
    var html = '';
    for (var i = 0; i < players.length; i++) {
      var p = players[i];
      var hay = (p.name + ' ' + (p.car || '') + ' ' + (p.ip || '') + ' ' + p.personaId).toLowerCase();
      if (filter && hay.indexOf(filter) < 0) continue;
      shown++;
      html += '<tr>' +
        '<td class="mono">' + UI.esc(p.personaId) + '</td>' +
        '<td class="name">' + UI.esc(p.name) +
        (p.xmpp ? ' <span class="tag ok">chat</span>' : '') +
        (p.relay ? ' <span class="tag info">relay</span>' : '') +
        (p.build ? ' <span class="tag">' + UI.esc(p.build) + '</span>' : '') +
        '</td>' +
        '<td class="num">' + UI.num(p.level) + '</td>' +
        (isAdmin
          ? '<td class="num">' + UI.num(p.cash) + '</td>'
          : '<td class="num">' + UI.num(p.rep) + '</td><td class="num">' + UI.num(p.cash) + '</td>') +
        '<td>' + UI.esc(p.car || '–') + '</td>' +
        '<td class="num">' + UI.duration(p.onlineMinutes * 60) + '</td>';
      if (isAdmin) {
        html += '<td class="mono">' + UI.esc(p.ip || '') + '</td>' +
          '<td class="num">' + UI.num(p.requests) + '</td>' +
          '<td class="actions"><button class="btn danger sm" data-kick="' + UI.esc(p.personaId) + '">kick</button></td>';
      } else {
        html += '<td class="num">' + UI.duration(p.idleSeconds) + '</td>';
      }
      html += '</tr>';
    }
    body.innerHTML = html;
    var empty = $('playersEmpty');
    if (empty) empty.style.display = shown === 0 ? '' : 'none';
    var table = $('playersTable');
    if (table) table.style.display = shown === 0 ? 'none' : '';
    UI.setText('playerCount', shown + ' / ' + players.length);

    if (state.lastOnlineCount >= 0) {
      if (players.length > state.lastOnlineCount) Sound.play('join');
      else if (players.length < state.lastOnlineCount) Sound.play('leave');
    }
    state.lastOnlineCount = players.length;

    if (isAdmin) wireKick();
  }

  function wireKick() {
    var btns = document.querySelectorAll('[data-kick]');
    for (var i = 0; i < btns.length; i++) {
      btns[i].onclick = function () {
        var id = this.getAttribute('data-kick');
        var reason = global.prompt('Кикнуть персонажа ' + id + '.\nПричина:', 'admin kick');
        if (reason === null) return;
        UI.api('/api/admin/kick', { method: 'POST', token: state.token, body: { personaId: Number(id), reason: reason } })
          .then(function () { UI.toast('кик ' + id, 'ok'); Sound.play('kick'); refreshAll(); })
          .catch(function (e) { authFail(e); UI.toast('кик не удался: ' + e.message, 'err'); });
      };
    }
  }

  var FEED_ICON = {
    start: 'fa-play', kick: 'fa-ban', announce: 'fa-comment', timeout: 'fa-clock',
    chat: 'fa-comment', admin: 'fa-user-shield', info: 'fa-circle-info'
  };
  var FEED_CLASS = {
    start: 't-group', kick: 't-kick', announce: 't-chat', timeout: 't-leave',
    chat: 't-chat', admin: 't-group', info: 't-join'
  };

  function renderFeed(data) {
    var list = $('feedList');
    if (!list) return;
    var items = (data && data.events) || [];
    if (!items.length) return;
    if (state.lastFeedId === 0 && list.firstChild && list.firstChild.className === 'empty small') list.innerHTML = '';

    var fresh = 0;
    var frag = document.createDocumentFragment();
    for (var i = 0; i < items.length; i++) {
      var f = items[i];
      if (f.id <= state.lastFeedId) continue;
      fresh++;
      var row = UI.el('div', 'li ' + (FEED_CLASS[f.kind] || 't-join'));
      row.appendChild(UI.el('i', 'fas ' + (FEED_ICON[f.kind] || 'fa-circle-info') + ' ico'));
      var txt = UI.el('div', 'txt');
      txt.appendChild(UI.el('b', null, f.text));
      txt.appendChild(UI.el('small', null, (f.persona || 'server') + ' · ' + UI.timeOf(f.utc)));
      row.appendChild(txt);
      frag.insertBefore(row, frag.firstChild);
    }
    if (fresh) {
      list.insertBefore(frag, list.firstChild);
      while (list.children.length > 80) list.removeChild(list.lastChild);
      if (!state.firstLoad) {
        var k = items[items.length - 1].kind;
        Sound.play(k === 'chat' ? 'message' : k === 'kick' ? 'kick' : 'notify');
      }
    }
    state.lastFeedId = data['last'] || state.lastFeedId;
    UI.setText('feedCount', String(list.children.length));
  }

  function renderRelay(data) {
    var body = $('relayBody');
    if (!body) return;
    var rooms = (data && data.rooms) || [];
    var html = '';
    for (var i = 0; i < rooms.length; i++) {
      var r = rooms[i];
      html += '<tr><td class="name">' + UI.esc(r.name) + '</td>' +
        '<td class="num">' + UI.num(r.clients) + '</td>' +
        '<td class="num">' + UI.num(r.packetsIn) + ' / ' + UI.num(r.packetsOut) + '</td>' +
        '<td class="num">' + UI.bytes(r.bytesIn) + ' / ' + UI.bytes(r.bytesOut) + '</td>';
      if (isAdmin) html += '<td class="mono">' + members(r.members) + '</td>';
      html += '</tr>';
    }
    body.innerHTML = html;
    if ($('relayEmpty')) $('relayEmpty').style.display = rooms.length ? 'none' : '';
    if ($('relayTable')) $('relayTable').style.display = rooms.length ? '' : 'none';
    UI.setText('relaySummary', (data && data.enabled ? 'udp ' + data.port : 'off') +
      ' · ↓' + UI.bytes(data ? data.bytesIn : 0));
  }

  function members(list) {
    if (!list || !list.length) return '';
    var out = [];
    for (var i = 0; i < list.length; i++) {
      out.push((list[i].hairpin ? '* ' : '') + list[i].endpoint + ' (#' + list[i].personaId + ')');
    }
    return UI.esc(out.join(', '));
  }

  // ---------------------------------------------------------------- fetching

  function authFail(e) {
    if (e && e.status === 401) { showGate(true); UI.toast('нужен admin token', 'warn'); }
  }

  function refreshAll() {
    UI.api('/api/status').then(renderStatus).catch(function (e) {
      var pill = $('connPill');
      if (pill) { pill.className = 'conn-pill bad'; UI.setText('connText', 'нет связи'); }
      if (state.firstLoad) UI.toast('API недоступен: ' + e.message, 'err');
    });
    UI.api('/api/players').then(renderPlayers).catch(function () { });
    UI.api('/api/feed?since=' + state.lastFeedId).then(renderFeed).catch(function () { });
    UI.api('/api/relay').then(renderRelay).catch(function () { });
    if (isAdmin && state.token) {
      if (currentTab() === 'logs') loadLogs();
      if (currentTab() === 'accounts') loadAccounts(false);
    }
    state.firstLoad = false;
  }

  function currentTab() {
    var t = document.querySelector('.tab.active');
    return t ? t.getAttribute('data-tab') : '';
  }

  // ------------------------------------------------------------------- admin

  function showGate(show) {
    var gate = $('authGate');
    if (gate) gate.hidden = !show;
    if (show) { var i = $('authToken'); if (i) setTimeout(function () { i.focus(); }, 50); }
  }

  function applyToken() {
    var input = $('authToken');
    var value = input ? input.value.trim() : '';
    if (!value) { UI.toast('введите токен из config.ini', 'warn'); return; }
    UI.api('/api/admin/session', { token: value }).then(function () {
      saveToken(value);
      showGate(false);
      UI.toast('токен принят', 'ok');
      Sound.play('connect');
      loadAccounts(true);
      refreshAll();
    }).catch(function (e) {
      UI.setText('authError', 'Неверный токен (' + e.message + ')');
      UI.toast('токен отклонён', 'err');
      Sound.play('error');
    });
  }

  function loadAccounts(force) {
    if (!state.token) { showGate(true); return; }
    var now = Date.now();
    if (!force && now - state.accountsAt < 5000) return;
    state.accountsAt = now;
    UI.api('/api/admin/accounts', { token: state.token }).then(function (data) {
      var list = (data && data.accounts) || [];
      var html = '';
      for (var i = 0; i < list.length; i++) {
        var a = list[i];
        var personas = '';
        for (var k = 0; k < (a.personas || []).length; k++) {
          var p = a.personas[k];
          personas += '<div class="tiny">' + UI.esc(p.name) + ' <span class="muted">#' + p.id +
            ' · lvl ' + p.level + ' · ' + UI.num(p.cash) + ' cash · ' + p.cars + ' машин</span> ' +
            '<button class="btn ghost sm" data-cash="' + p.id + '">+100k</button></div>';
        }
        html += '<tr><td class="mono">' + a.id + '</td>' +
          '<td class="name">' + UI.esc(a.name) + (a.banned ? ' <span class="tag err">ban</span>' : '') + '</td>' +
          '<td class="mono">' + UI.esc(a.lastLoginUtc || '') + '</td>' +
          '<td>' + personas + '</td>' +
          '<td class="actions">' +
          (a.banned
            ? '<button class="btn ghost sm" data-account="' + a.id + '" data-action="unban">unban</button>'
            : '<button class="btn danger sm" data-account="' + a.id + '" data-action="ban">ban</button>') +
          '</td></tr>';
      }
      if ($('accountsBody')) $('accountsBody').innerHTML = html;
      UI.setText('accountsCount', String(list.length));
      wireAccount();
      showGate(false);
    }).catch(function (e) {
      authFail(e);
      if (e.status !== 401) UI.toast('аккаунты: ' + e.message, 'err');
    });
  }

  function wireAccount() {
    var i;
    var btns = document.querySelectorAll('[data-account]');
    for (i = 0; i < btns.length; i++) {
      btns[i].onclick = function () {
        var id = Number(this.getAttribute('data-account'));
        var action = this.getAttribute('data-action');
        UI.api('/api/admin/account', { method: 'POST', token: state.token, body: { accountId: id, action: action } })
          .then(function () { UI.toast('аккаунт ' + id + ': ' + action, 'ok'); loadAccounts(true); Sound.play('notify'); })
          .catch(function (e) { authFail(e); UI.toast('ошибка: ' + e.message, 'err'); });
      };
    }
    var cash = document.querySelectorAll('[data-cash]');
    for (i = 0; i < cash.length; i++) {
      cash[i].onclick = function () {
        var pid = Number(this.getAttribute('data-cash'));
        UI.api('/api/admin/account', {
          method: 'POST', token: state.token,
          body: { personaId: pid, action: 'cash', delta: 100000 }
        }).then(function () { UI.toast('+100000 персонажу #' + pid, 'ok'); loadAccounts(true); Sound.play('reward'); })
          .catch(function (e) { authFail(e); UI.toast('ошибка: ' + e.message, 'err'); });
      };
    }
  }

  function loadLogs() {
    if (!state.token || state.logsPaused) return;
    var url = '/api/admin/logs?since=' + state.logsSince + '&limit=250';
    if (state.logsFilter) url += '&level=' + encodeURIComponent(state.logsFilter);
    UI.api(url, { token: state.token }).then(function (data) {
      var view = $('logsView');
      if (!view) return;
      var lines = (data && data.lines) || [];
      if (!lines.length) return;
      if (view.firstChild && view.firstChild.className === 'log-empty') view.innerHTML = '';
      var frag = document.createDocumentFragment();
      for (var i = 0; i < lines.length; i++) {
        var l = lines[i];
        var row = UI.el('div', 'log-line lv-' + String(l.level || 'I').charAt(0));
        row.appendChild(UI.el('span', 't', UI.timeOf(l.utc)));
        row.appendChild(UI.el('span', 'cat', l.cat));
        row.appendChild(UI.el('span', 'm', l.msg));
        frag.appendChild(row);
        state.logsSince = l.seq;
      }
      view.appendChild(frag);
      view.scrollTop = view.scrollHeight;
      while (view.children.length > 700) view.removeChild(view.firstChild);
    }).catch(function (e) { authFail(e); });
  }

  function announce() {
    var input = $('announceText');
    var text = input ? input.value.trim() : '';
    if (!text) { UI.toast('объявление пустое', 'warn'); return; }
    UI.api('/api/admin/announce', { method: 'POST', token: state.token, body: { text: text } })
      .then(function (r) { UI.toast('отправлено ' + (r.sent || 0) + ' клиентам', 'ok'); input.value = ''; Sound.play('message'); })
      .catch(function (e) { authFail(e); UI.toast('не отправлено: ' + e.message, 'err'); });
  }

  // -------------------------------------------------------------------- wire

  function wire() {
    UI.on('btnTheme', 'click', UI.nextTheme);
    UI.on('btnSound', 'click', function () {
      var on = !Sound.isEnabled();
      Sound.setEnabled(on);
      UI.Store.set('dpt.sound', on ? '1' : '0');
      syncSound();
      if (on) Sound.play('notify');
    });
    var doRefresh = function () { UI.toast('обновлено', ''); refreshAll(); Sound.click(); };
    UI.on('btnRefresh', 'click', doRefresh);
    UI.on('btnRefresh2', 'click', doRefresh);

    var search = $('search');
    if (search) {
      search.addEventListener('input', UI.debounce(function () {
        UI.api('/api/players').then(renderPlayers).catch(function () { });
      }, 150), false);
    }

    if (!isAdmin) return;

    UI.on('authForm', 'submit', function (e) { e.preventDefault(); applyToken(); });
    UI.on('btnLogout', 'click', function () { saveToken(''); showGate(true); UI.toast('токен забыт', ''); });
    UI.on('btnAnnounce', 'click', announce);
    UI.on('btnSave', 'click', function () {
      UI.api('/api/admin/save', { method: 'POST', token: state.token })
        .then(function () { UI.toast('состояние сохранено', 'ok'); })
        .catch(function (e) { authFail(e); UI.toast('save не удался', 'err'); });
    });
    UI.on('btnReload', 'click', function () {
      UI.api('/api/admin/reload', { method: 'POST', token: state.token })
        .then(function () { UI.toast('профили перечитаны', 'ok'); refreshAll(); })
        .catch(function (e) { authFail(e); UI.toast('reload не удался', 'err'); });
    });
    UI.on('btnLogsPause', 'click', function () {
      state.logsPaused = !state.logsPaused;
      var i = this.querySelector('i');
      if (i) i.className = 'fas ' + (state.logsPaused ? 'fa-play' : 'fa-pause');
      this.title = state.logsPaused ? 'Продолжить' : 'Пауза';
    });
    UI.on('btnLogsClear', 'click', function () {
      var v = $('logsView');
      if (v) v.innerHTML = '<div class="log-empty">нет записей</div>';
      state.logsSince = 0;
    });
    var level = $('logsLevel');
    if (level) level.addEventListener('change', function () { state.logsFilter = this.value; state.logsSince = 0; }, false);
    UI.on('btnShutdown', 'click', function () {
      if (!global.confirm('Остановить сервер?')) return;
      UI.api('/api/admin/shutdown', { method: 'POST', token: state.token })
        .then(function () { UI.toast('остановка запрошена', 'warn'); })
        .catch(function (e) { authFail(e); });
    });
  }

  function boot() {
    loadPrefs();
    loadToken();
    UI.tabs();
    wire();
    if (isAdmin) showGate(!state.token);
    UI.selectTab(UI.Store.get('dpt.tab.' + role, 'dashboard'));
    refreshAll();
    UI.poll(function () {
      if (document.hidden) return;
      refreshAll();
    }, 3000);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot, false);
  else boot();
})(this);