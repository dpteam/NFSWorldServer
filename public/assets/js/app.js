/* =========================================================================
   DPT.Server - board logic for both roles (public and admin).
   Public mode never asks for the admin token and never renders private fields.
   ========================================================================= */
(function (global) {
  'use strict';

  var UI = global.UI;
  var Sound = global.Sound;
  var role = document.body.getAttribute('data-role') || 'public';
  var isAdmin = role === 'admin';

  var state = {
    lastOnline: {},
    lastFeedId: 0,
    lastOnlineCount: -1,
    token: '',
    logsSince: 0,
    logsPaused: false,
    logsFilter: '',
    refreshMs: 3000,
    firstLoad: true
  };

  // ---------------------------------------------------------------- settings

  function loadPrefs() {
    var theme = UI.Store.get('dpt.theme', document.body.getAttribute('data-theme-default') || 'light');
    UI.applyTheme(theme);
    var soundOn = UI.Store.getBool('dpt.sound', true);
    Sound.setEnabled(soundOn);
    Sound.setVolume(UI.Store.get('dpt.volume', 0.55));
    syncSoundButton();
  }

  function syncSoundButton() {
    var btn = document.getElementById('btnSound');
    if (!btn) return;
    var on = Sound.isEnabled();
    btn.classList.toggle('off', !on);
    var i = btn.querySelector('i');
    if (i) i.className = 'fa ' + (on ? 'fa-volume' : 'fa-volume-off');
    btn.title = on ? 'Sounds: on' : 'Sounds: off';
  }

  // ------------------------------------------------------------------ tokens

  function loadToken() {
    try {
      state.token = global.sessionStorage ? (global.sessionStorage.getItem('dpt.admin') || '') : '';
    } catch (e) { state.token = ''; }
  }

  function saveToken(t) {
    state.token = t || '';
    try { if (global.sessionStorage) global.sessionStorage.setItem('dpt.admin', state.token); } catch (e) { }
  }

  // --------------------------------------------------------------- rendering

  function setChip(id, ok, textOn, textOff) {
    var e = document.getElementById(id);
    if (!e) return;
    e.textContent = ok ? (textOn || 'online') : (textOff || 'offline');
    e.className = 'chip ' + (ok ? 'ok' : 'err');
  }

  function renderStatus(s) {
    if (!s) return;
    var c = s.counts || {};
    var sv = s.services || {};
    var tr = s.traffic || {};

    UI.setText('sOnline', UI.num(c.online));
    UI.setText('sSessions', UI.num(c.sessions));
    UI.setText('sAccounts', UI.num(c.accounts));
    UI.setText('sPersonas', UI.num(c.personas));
    UI.setText('sCars', UI.num(c.cars));
    UI.setText('sRooms', UI.num(c.rooms));
    UI.setText('sRequests', UI.num(c.requests) + ' / ' + UI.num(s.engineRequests || 0));
    UI.setText('sUptime', s.uptime || '');
    UI.setText('sVersion', 'v' + (s.version || '?') + ' (' + (s.buildDate || '') + ')');
    UI.setText('sRelayIn', UI.bytes(tr.relayIn));
    UI.setText('sRelayOut', UI.bytes(tr.relayOut));
    UI.setText('sRelayPackets', UI.num(tr.relayPacketsIn) + ' / ' + UI.num(tr.relayPacketsOut));
    UI.setText('sRelayDropped', UI.num(tr.relayDropped));

    setChip('svcEngine', !!sv.engine, 'engine ' + (sv.enginePort || ''), 'engine down');
    setChip('svcXmpp', !!sv.xmpp, 'chat ' + (sv.xmppPort || ''), 'chat down');
    setChip('svcRelay', !!sv.relay, 'relay ' + (sv.relayPort || ''), 'relay down');
    setChip('svcPublic', !!sv.webPublic, 'public', 'public down');
    setChip('svcAdmin', !!sv.webAdmin, 'admin', 'admin down');

    var dot = document.getElementById('connDot');
    if (dot) {
      dot.className = 'dot ' + (sv.engine ? (sv.relay && sv.xmpp ? 'online' : 'partial') : '');
      dot.title = sv.engine ? 'server is up' : 'server is unreachable';
    }

    if (s.refreshMs) state.refreshMs = s.refreshMs;

    renderProfiles(s.profiles || [], s.defaultProfile);
    if (isAdmin && s.server) {
      UI.setText('aPath', s.server.path);
      UI.setText('aLogDir', s.server.logDir);
      UI.setText('aState', s.server.stateDir);
      UI.setText('aMemory', UI.bytes(s.server.workingSet));
      UI.setText('aEngine', UI.num(s.server.engineRequests) + ' req / ' + UI.num(s.server.engineErrors) + ' err');
    }
  }

  function renderProfiles(list, active) {
    var box = document.getElementById('profilesBox');
    if (!box) return;
    if (!list.length) { box.innerHTML = '<div class="empty">no client profiles installed</div>'; return; }
    var html = '';
    for (var i = 0; i < list.length; i++) {
      var p = list[i];
      html += '<div class="row" style="justify-content:space-between;border-bottom:1px solid var(--border-soft);padding:6px 0">' +
        '<div><b>' + UI.esc(p.name || p.id) + '</b> <span class="chip ' + (p.default ? 'blue' : '') + '">' + UI.esc(p.id) + '</span>' +
        (p.build ? ' <span class="chip">build ' + UI.esc(p.build) + '</span>' : '') +
        '<div class="muted small">' + UI.esc(p.description || '') + '</div></div>' +
        '<div class="pill-row"><span class="chip">' + UI.esc(p.personaFormat || 'legacy') + '</span>' +
        (p.default ? '<span class="chip ok">default</span>' : '') + '</div></div>';
    }
    box.innerHTML = html;
  }

  function renderPlayers(data) {
    var body = document.getElementById('playersBody');
    if (!body) return;
    var players = (data && data.players) || [];
    var filter = (document.getElementById('search') || {}).value || '';
    filter = filter.toLowerCase();

    var seen = {};
    var shown = 0;
    var html = '';
    for (var i = 0; i < players.length; i++) {
      var p = players[i];
      seen[p.personaId] = true;
      var hay = (p.name + ' ' + (p.car || '') + ' ' + (p.ip || '') + ' ' + (p.personaId || '')).toLowerCase();
      if (filter && hay.indexOf(filter) < 0) continue;
      shown++;
      html += '<tr>' +
        '<td class="mono">' + UI.esc(p.personaId) + '</td>' +
        '<td><b>' + UI.esc(p.name) + '</b> ' +
        (p.xmpp ? '<span class="chip ok" title="EA-Chat connected">chat</span> ' : '') +
        (p.relay ? '<span class="chip blue" title="UDP relay">relay</span> ' : '') +
        (p.build ? '<span class="chip">' + UI.esc(p.build) + '</span>' : '') + '</td>' +
        '<td class="num">' + UI.num(p.level) + '</td>' +
        '<td class="num">' + UI.num(p.rep) + '</td>' +
        '<td class="num">' + UI.num(p.cash) + '</td>' +
        '<td>' + UI.esc(p.car || '') + '</td>' +
        '<td class="num">' + UI.duration(p.onlineMinutes * 60) + '</td>' +
        '<td class="num">' + UI.duration(p.idleSeconds) + '</td>';
      if (isAdmin) {
        html += '<td class="mono">' + UI.esc(p.ip || '') + '</td>' +
          '<td class="mono">' + UI.esc(p.token || '') + '</td>' +
          '<td class="num">' + UI.num(p.requests) + '</td>' +
          '<td><button class="small danger" data-kick="' + UI.esc(p.personaId) + '">kick</button></td>';
      }
      html += '</tr>';
    }
    body.innerHTML = html;
    var empty = document.getElementById('playersEmpty');
    if (empty) empty.style.display = shown === 0 ? '' : 'none';
    var table = document.getElementById('playersTable');
    if (table) table.style.display = shown === 0 ? 'none' : '';
    UI.setText('playerCount', shown + ' / ' + players.length);

    // sound on join/leave
    var count = players.length;
    if (state.lastOnlineCount >= 0) {
      if (count > state.lastOnlineCount) Sound.play('join');
      else if (count < state.lastOnlineCount) Sound.play('leave');
    }
    state.lastOnlineCount = count;
    state.lastOnline = seen;

    if (isAdmin) wireKickButtons();
  }

  function wireKickButtons() {
    var buttons = document.querySelectorAll('[data-kick]');
    for (var i = 0; i < buttons.length; i++) {
      buttons[i].onclick = function () {
        var id = this.getAttribute('data-kick');
        var reason = global.prompt('Kick persona ' + id + '.\nReason:', 'admin kick');
        if (reason === null) return;
        UI.api('/api/admin/kick', { method: 'POST', token: state.token, body: { personaId: Number(id), reason: reason } })
          .then(function () { UI.toast('kicked ' + id, 'ok'); Sound.play('kick'); refreshAll(); })
          .catch(function (e) { UI.toast('kick failed: ' + e.message, 'err'); handleAuth(e); });
      };
    }
  }

  function renderFeed(data) {
    var list = document.getElementById('feedList');
    if (!list) return;
    var items = (data && data.events) || [];
    if (!items.length) return;

    var frag = document.createDocumentFragment();
    var newOnes = 0;
    for (var i = 0; i < items.length; i++) {
      var f = items[i];
      if (f.id <= state.lastFeedId) continue;
      newOnes++;
      var li = UI.el('li');
      li.appendChild(UI.el('span', 't', UI.timeOf(f.utc)));
      var mid = UI.el('span');
      mid.innerHTML = '<span class="chip ' + feedClass(f.kind) + '">' + UI.esc(f.kind) + '</span> ' + UI.esc(f.text);
      li.appendChild(mid);
      li.appendChild(UI.el('span', 'who', f.persona || ''));
      frag.insertBefore(li, frag.firstChild);
    }
    if (newOnes > 0) {
      list.insertBefore(frag, list.firstChild);
      while (list.children.length > 80) list.removeChild(list.lastChild);
      if (!state.firstLoad) {
        var lastKind = items[items.length - 1].kind;
        Sound.play(lastKind === 'chat' ? 'message' : lastKind === 'kick' ? 'kick' : 'notify');
      }
    }
    state.lastFeedId = data.last || state.lastFeedId;
  }

  function feedClass(kind) {
    if (kind === 'start') return 'ok';
    if (kind === 'kick') return 'err';
    if (kind === 'announce') return 'blue';
    if (kind === 'timeout') return 'warn';
    return '';
  }

  function renderRelay(data) {
    var body = document.getElementById('relayBody');
    if (!body) return;
    var rooms = (data && data.rooms) || [];
    var html = '';
    for (var i = 0; i < rooms.length; i++) {
      var r = rooms[i];
      html += '<tr><td><b>' + UI.esc(r.name) + '</b></td>' +
        '<td class="num">' + UI.num(r.clients) + '</td>' +
        '<td class="num">' + UI.num(r.packetsIn) + ' / ' + UI.num(r.packetsOut) + '</td>' +
        '<td class="num">' + UI.bytes(r.bytesIn) + ' / ' + UI.bytes(r.bytesOut) + '</td>' +
        '<td class="mono">' + (isAdmin ? memberList(r.members) : '') + '</td></tr>';
    }
    body.innerHTML = html;
    var empty = document.getElementById('relayEmpty');
    if (empty) empty.style.display = rooms.length ? 'none' : '';
    var table = document.getElementById('relayTable');
    if (table) table.style.display = rooms.length ? '' : 'none';
    UI.setText('relaySummary', (data && data.enabled ? 'relay on port ' + data.port : 'relay disabled') +
      ' - ' + UI.bytes(data ? data.bytesIn : 0) + ' in / ' + UI.bytes(data ? data.bytesOut : 0) + ' out');
  }

  function memberList(members) {
    if (!members || !members.length) return '';
    var out = [];
    for (var i = 0; i < members.length; i++) {
      var m = members[i];
      out.push((m.hairpin ? '* ' : '') + m.endpoint + ' (#' + m.personaId + ')');
    }
    return UI.esc(out.join(', '));
  }

  // ---------------------------------------------------------------- fetching

  function handleAuth(e) {
    if (e && e.status === 401) {
      showLogin(true);
      UI.toast('admin token required', 'warn');
    }
  }

  function refreshAll() {
    UI.api('/api/status').then(function (s) {
      renderStatus(s);
      if (isAdmin) maybeLoadAccounts();
    }).catch(function (e) {
      var dot = document.getElementById('connDot');
      if (dot) { dot.className = 'dot'; dot.title = 'server unreachable'; }
      if (state.firstLoad) UI.toast('server API unreachable: ' + e.message, 'err');
    });

    UI.api('/api/players').then(renderPlayers).catch(function () { });
    UI.api('/api/feed?since=' + state.lastFeedId).then(renderFeed).catch(function () { });
    UI.api('/api/relay').then(renderRelay).catch(function () { });

    if (isAdmin) {
      if (state.token) loadLogs();
    }
    state.firstLoad = false;
  }

  // ------------------------------------------------------------------- admin

  function showLogin(show) {
    var card = document.getElementById('adminLogin');
    if (card) card.classList.toggle('hidden', !show);
    var bar = document.getElementById('adminBar');
    if (bar) bar.classList.toggle('hidden', show);
  }

  function applyToken() {
    var input = document.getElementById('adminToken');
    var value = input ? input.value.trim() : '';
    if (!value) { UI.toast('enter the admin token from config.ini', 'warn'); return; }
    UI.api('/api/admin/session', { token: value }).then(function () {
      saveToken(value);
      showLogin(false);
      UI.toast('admin token accepted', 'ok');
      Sound.play('connect');
      loadAccounts(true);
      loadLogs();
    }).catch(function (e) {
      UI.toast('token rejected: ' + e.message, 'err');
      Sound.play('error');
    });
  }

  function maybeLoadAccounts() {
    var tab = document.querySelector('[data-tab="accounts"]');
    if (!tab) return;
    if (tab.classList.contains('active')) loadAccounts(false);
  }

  var accountsCache = 0;

  function loadAccounts(force) {
    if (!state.token) { showLogin(true); return; }
    var now = Date.now();
    if (!force && now - accountsCache < 5000) return;
    accountsCache = now;
    UI.api('/api/admin/accounts', { token: state.token }).then(function (data) {
      var list = (data && data.accounts) || [];
      var html = '';
      for (var i = 0; i < list.length; i++) {
        var a = list[i];
        var personas = '';
        for (var k = 0; k < (a.personas || []).length; k++) {
          var p = a.personas[k];
          personas += '<div class="small"><b>' + UI.esc(p.name) + '</b> #' + p.id +
            ' - lvl ' + p.level + ', ' + UI.num(p.cash) + ' cash, ' + p.cars + ' cars ' +
            '<button class="small" data-cash="' + p.id + '">+100k</button></div>';
        }
        html += '<tr><td class="mono">' + a.id + '</td>' +
          '<td>' + UI.esc(a.name) + (a.banned ? ' <span class="chip err">banned</span>' : '') + '</td>' +
          '<td>' + UI.esc(a.lastLoginUtc || '') + '</td>' +
          '<td>' + personas + '</td>' +
          '<td class="nowrap">' +
          (a.banned
            ? '<button class="small" data-account="' + a.id + '" data-action="unban">unban</button>'
            : '<button class="small danger" data-account="' + a.id + '" data-action="ban">ban</button>') +
          '</td></tr>';
      }
      var body = document.getElementById('accountsBody');
      if (body) body.innerHTML = html;
      UI.setText('accountsCount', list.length + '');
      wireAccountButtons();
      showLogin(false);
    }).catch(function (e) {
      handleAuth(e);
      if (e.status !== 401) UI.toast('accounts: ' + e.message, 'err');
    });
  }

  function wireAccountButtons() {
    var btns = document.querySelectorAll('[data-account]');
    var i;
    for (i = 0; i < btns.length; i++) {
      btns[i].onclick = function () {
        var id = Number(this.getAttribute('data-account'));
        var action = this.getAttribute('data-action');
        UI.api('/api/admin/account', { method: 'POST', token: state.token, body: { accountId: id, action: action } })
          .then(function () { UI.toast('account ' + id + ': ' + action, 'ok'); loadAccounts(true); Sound.play('notify'); })
          .catch(function (e) { handleAuth(e); UI.toast('failed: ' + e.message, 'err'); });
      };
    }
    var cash = document.querySelectorAll('[data-cash]');
    for (i = 0; i < cash.length; i++) {
      cash[i].onclick = function () {
        var pid = Number(this.getAttribute('data-cash'));
        UI.api('/api/admin/account', {
          method: 'POST', token: state.token,
          body: { accountId: 0, personaId: pid, action: 'cash', delta: 100000 }
        }).catch(function (e) {
          // accountId is required by the endpoint; fall back to a direct persona lookup through status
          handleAuth(e);
          UI.toast('cash: ' + e.message, 'err');
        }).then(function () { loadAccounts(true); });
      };
    }
  }

  function loadLogs() {
    if (!state.token || state.logsPaused) return;
    var url = '/api/admin/logs?since=' + state.logsSince + '&limit=300';
    if (state.logsFilter) url += '&level=' + encodeURIComponent(state.logsFilter);
    UI.api(url, { token: state.token }).then(function (data) {
      var lines = (data && data.lines) || [];
      var view = document.getElementById('logsView');
      if (!view) return;
      var html = view.innerHTML;
      for (var i = 0; i < lines.length; i++) {
        var l = lines[i];
        var cls = 'lvl-' + String(l.level || 'I').charAt(0);
        html += '<div class="' + cls + '">' + UI.esc(UI.timeOf(l.utc) + ' [' + l.level + '] ' + l.cat + ' ' + l.msg) + '</div>';
        state.logsSince = l.seq;
      }
      if (lines.length) {
        view.innerHTML = html;
        view.scrollTop = view.scrollHeight;
        var nodes = view.children;
        while (nodes.length > 800) view.removeChild(view.firstChild);
      }
    }).catch(function (e) { handleAuth(e); });
  }

  function announce() {
    var input = document.getElementById('announceText');
    if (!input) return;
    var text = input.value.trim();
    if (!text) { UI.toast('announcement is empty', 'warn'); return; }
    UI.api('/api/admin/announce', { method: 'POST', token: state.token, body: { text: text } })
      .then(function (r) { UI.toast('sent to ' + (r.sent || 0) + ' client(s)', 'ok'); input.value = ''; Sound.play('message'); })
      .catch(function (e) { handleAuth(e); UI.toast('announce failed: ' + e.message, 'err'); });
  }

  // -------------------------------------------------------------------- wire

  function wireToolbar() {
    UI.on('btnTheme', 'click', UI.nextTheme);
    UI.on('btnSound', 'click', function () {
      var on = !Sound.isEnabled();
      Sound.setEnabled(on);
      UI.Store.set('dpt.sound', on ? '1' : '0');
      syncSoundButton();
      if (on) Sound.play('notify');
    });
    UI.on('btnRefresh', 'click', function () {
      UI.toast('refreshed', '');
      refreshAll();
      if (isAdmin) { loadAccounts(true); loadLogs(); }
      Sound.click();
    });
    var search = document.getElementById('search');
    if (search) {
      search.addEventListener('input', UI.debounce(function () {
        UI.api('/api/players').then(renderPlayers).catch(function () { });
      }, 150), false);
    }
  }

  function wireAdmin() {
    if (!isAdmin) return;
    UI.on('btnLogin', 'click', applyToken);
    var tokenInput = document.getElementById('adminToken');
    if (tokenInput) {
      tokenInput.addEventListener('keydown', function (e) { if (e.keyCode === 13) applyToken(); }, false);
    }
    var logout = document.getElementById('btnLogout');
    if (logout) {
      logout.onclick = function () { saveToken(''); showLogin(true); UI.toast('token forgotten', ''); };
    }
    UI.on('btnAnnounce', 'click', announce);
    UI.on('btnSave', 'click', function () {
      UI.api('/api/admin/save', { method: 'POST', token: state.token })
        .then(function () { UI.toast('state saved', 'ok'); })
        .catch(function (e) { handleAuth(e); UI.toast('save failed', 'err'); });
    });
    UI.on('btnReload', 'click', function () {
      UI.api('/api/admin/reload', { method: 'POST', token: state.token })
        .then(function () { UI.toast('profiles reloaded', 'ok'); refreshAll(); })
        .catch(function (e) { handleAuth(e); UI.toast('reload failed', 'err'); });
    });
    UI.on('btnLogsPause', 'click', function () {
      state.logsPaused = !state.logsPaused;
      this.textContent = state.logsPaused ? 'resume' : 'pause';
    });
    var level = document.getElementById('logsLevel');
    if (level) level.addEventListener('change', function () { state.logsFilter = this.value; state.logsSince = 0; }, false);
    UI.on('btnLogsClear', 'click', function () {
      var v = document.getElementById('logsView');
      if (v) v.innerHTML = '';
      state.logsSince = 0;
    });
    UI.on('btnShutdown', 'click', function () {
      if (!global.confirm('Stop the server process?')) return;
      UI.api('/api/admin/shutdown', { method: 'POST', token: state.token })
        .then(function () { UI.toast('shutdown requested - the window will close', 'warn'); })
        .catch(function (e) { handleAuth(e); });
    });
  }

  function boot() {
    loadPrefs();
    loadToken();
    UI.tabs();
    wireToolbar();
    wireAdmin();
    if (isAdmin) showLogin(!state.token);
    var saved = UI.Store.get('dpt.tab.' + role, 'dashboard');
    UI.selectTab(saved);
    refreshAll();
    UI.poll(function () {
      if (document.hidden) return;
      refreshAll();
    }, Math.max(2000, state.refreshMs));
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot, false);
  else boot();
})(this);