// RocketRPG MV/MZ automated compatibility test (injected only when RR_AUTOTEST is set for the app).
// Same idea as the mkxp-z / EasyRPG autotests: confirm through the title, start a new game, then visit every map the
// game itself transfers to (arriving at those coordinates), walk and press OK so events run, wait for events to end
// before the next transfer, never leave a battle by force. Choices take the default first; a repeated choice gets the
// next option. Progress goes to the host (type 'autotest'), which writes the log file.
(function () {
  'use strict';
  if (location.hostname !== 'rocket.local') return;
  var MAX_MAPS = 60, PER_MAP = 150;
  var at = { frame: 0, phase: 0, maps: [], dests: {}, index: -1, mapFrames: 0, offMap: 0, target: 0, done: false,
             forced: false, building: false, lastScene: '', choiceKey: null, choiceSeen: {}, downsNeeded: 0, downsDone: 0 };

  function log(line) { try { chrome.webview.postMessage({ type: 'autotest', line: at.frame + '\t' + line }); } catch (e) {} }
  function finish() {
    if (at.done) return;
    at.done = true;
    log('DONE');
    try { chrome.webview.postMessage({ type: 'autotest', done: true }); } catch (e) {}
  }
  window.addEventListener('error', function (e) {
    log('CRASH ' + (e.error && e.error.name || 'Error') + ': ' + (e.message || '') + ' @' + (e.filename || '').split('/').pop() + ':' + (e.lineno || 0));
  });
  window.addEventListener('unhandledrejection', function (e) { log('CRASH unhandled rejection: ' + (e.reason && (e.reason.message || e.reason))); });

  function scene() { return (typeof SceneManager !== 'undefined' && SceneManager._scene) || null; }
  function sceneName() { var s = scene(); return s ? (s.constructor && s.constructor.name) || '?' : 'null'; }
  function onMap() { return typeof Scene_Map !== 'undefined' && scene() instanceof Scene_Map; }
  function inBattle() { return typeof Scene_Battle !== 'undefined' && scene() instanceof Scene_Battle; }
  function idle() {
    return onMap() && $gameMap && !$gameMap.isEventRunning() && !($gameMessage && $gameMessage.isBusy()) && transferSafe();
  }
  // Never request a transfer while one is still in progress or the scene is changing (map data would be null).
  function transferSafe() {
    return !($gamePlayer && $gamePlayer.isTransferring && $gamePlayer.isTransferring()) && !SceneManager.isSceneChanging() && !!window.$dataMap;
  }
  function currentChoice() {
    if (!$gameMessage || !$gameMessage.isChoice || !$gameMessage.isChoice()) return null;
    // allText() is texts.reduce() without an initial value: throws for a choice shown without a message
    var texts = ($gameMessage._texts || []).join('\n');
    return { key: texts + '|' + $gameMessage.choices().join('|'), n: $gameMessage.choices().length };
  }
  function trackChoice() {
    var c = currentChoice();
    if (!c) { at.choiceKey = null; return; }
    if (c.key === at.choiceKey) return;
    at.choiceKey = c.key;
    var seen = (at.choiceSeen[c.key] = (at.choiceSeen[c.key] || 0) + 1);
    at.downsNeeded = (seen - 1) % Math.max(c.n, 1);
    at.downsDone = 0;
  }
  function needDown() { return at.choiceKey && at.downsDone < at.downsNeeded; }

  function mapFile(id) { return 'data/Map' + String(id).padStart(3, '0') + '.json'; }
  function buildMapList() {
    at.building = true;
    var ids = [];
    for (var i = 1; i < $dataMapInfos.length; i++) if ($dataMapInfos[i]) ids.push(i);
    var scan = function (list) {
      (list || []).forEach(function (c) {
        if (c && c.code === 201 && c.parameters && c.parameters[0] === 0 && !(c.parameters[1] in at.dests))
          at.dests[c.parameters[1]] = [c.parameters[2], c.parameters[3], c.parameters[4] || 2];
      });
    };
    ($dataCommonEvents || []).forEach(function (ce) { if (ce) scan(ce.list); });
    Promise.all(ids.map(function (id) {
      return fetch(mapFile(id)).then(function (r) { return r.ok ? r.json() : null; }).then(function (m) {
        if (m && m.events) m.events.forEach(function (ev) { if (ev && ev.pages) ev.pages.forEach(function (pg) { scan(pg.list); }); });
      }).catch(function () {});
    })).then(function () {
      var list = [$dataSystem.startMapId];
      Object.keys(at.dests).forEach(function (k) { list.push(+k); });
      list = list.filter(function (id, i) { return list.indexOf(id) === i && $dataMapInfos[id]; }).sort(function (a, b) { return a - b; });
      if (list.length <= 1) list = ids;
      at.maps = list.slice(0, MAX_MAPS);
      log('START map=' + $gameMap.mapId() + ' maps=' + at.maps.length);
      at.phase = 1;
      at.building = false;
      nextMap();
    });
  }
  function nextMap() {
    if (at.target) {
      var now = $gameMap.mapId();
      log('MAP ' + at.target + (now === at.target ? ' ok' : ' not reached (now ' + now + ')') + ' scene=' + sceneName());
    }
    at.index++;
    at.mapFrames = 0;
    if (at.index >= at.maps.length) { finish(); return; }
    var id = at.maps[at.index], d = at.dests[id];
    var x = d ? d[0] : 0, y = d ? d[1] : 0, dir = d ? d[2] : 2;
    if (!d && $dataMapInfos[id]) { x = 1; y = 1; }
    log('GO ' + id + ' ' + (($dataMapInfos[id] && $dataMapInfos[id].name) || ''));
    $gamePlayer.reserveTransfer(id, x, y, dir, 0);
    at.target = id;
  }
  function forceNewGame() {
    at.forced = true;
    try {
      // Custom titles can stay "busy" forever, which blocks the scene change.
      var cur = scene();
      if (cur) cur.isBusy = function () { return false; };
      DataManager.setupNewGame();
      SceneManager.goto(Scene_Map);
      log('FORCED new game');
    } catch (e) { log('FORCE NEW GAME failed ' + e); }
  }

  function tick() {
    if (at.done) return;
    at.frame++;
    if (at.frame % 60 === 1) hookInput();
    var name = sceneName();
    if (name !== at.lastScene) { at.lastScene = name; log('SCENE ' + name); }
    trackChoice();
    if (at.phase === 0) {
      if (onMap() && $gameMap.mapId() > 0 && !at.building && ++at.offMap > 60) { at.offMap = 0; buildMapList(); }
      else if (at.frame > 3600 && !at.building) { log('STUCK before map scene=' + name); finish(); }
      else if (at.frame === 900 && !at.forced && !onMap()) forceNewGame();
      return;
    }
    // Pressing OK alone can make fights drag on or be lost over and over: after 600 frames defeat the enemies
    // (normal victory flow), after 1500 abort the battle.
    if (inBattle()) {
      at.battleFrames = (at.battleFrames || 0) + 1;
      if (at.battleFrames === 600) {
        log('BATTLE too long -> enemies defeated');
        $gameTroop.members().forEach(function (e) { if (e.isAlive()) { e.setHp(0); e.refresh(); } });
      } else if (at.battleFrames === 1500) {
        log('BATTLE still running -> abort');
        try { BattleManager.abort(); } catch (e) {}
      }
    } else {
      at.battleFrames = 0;
    }
    if (at.phase !== 1) return;
    if (onMap()) at.offMap = 0;
    else if (!inBattle() && ++at.offMap > 180 && at.offMap % 60 === 0) {
      if (at.offMap === 240) log('LEFT MAP scene=' + name + ' -> cancel');
      at.cancel = true;
    }
    at.mapFrames++;
    if (onMap() && transferSafe() && at.mapFrames >= PER_MAP && (idle() || (at.mapFrames >= PER_MAP + 300 && !($gameMessage && $gameMessage.isBusy())) || at.mapFrames >= PER_MAP + 2400)) nextMap();
  }

  // ---- input simulation ----
  // Plugins (keyboard / title plugins) may replace Input methods after we hooked them; re-wrap only when our layer
  // is gone. Each wrapper records what it wraps (__rrWraps) and its owner, so the bridge's own input hook and ours
  // do not keep wrapping each other (that made the call chain grow without bound).
  function chainHas(fn, owner) {
    for (var i = 0; fn && i < 40; i++) { if (fn.__rrOwner === owner) return true; fn = fn.__rrWraps; }
    return false;
  }
  function wrap(name, make) {
    var cur = Input[name];
    if (typeof cur !== 'function' || chainHas(cur, 'autotest')) return;
    var w = make(cur);
    w.__rrOwner = 'autotest';
    w.__rrWraps = cur;
    Input[name] = w;
  }
  // After the map's time is up, OK only closes an open message (otherwise the event in front keeps restarting its talk).
  function ok(k) {
    if (at.done || k !== 'ok' || at.frame % 12 !== 0 || needDown()) return false;
    return !(at.phase === 1 && at.mapFrames >= PER_MAP && !($gameMessage && $gameMessage.isBusy()));
  }
  function down(k) {
    if (at.done || k !== 'down' || !needDown() || at.frame % 12 !== 6) return false;
    at.downsDone++;
    return true;
  }
  function cancel(k) { if (k === 'cancel' && at.cancel && at.frame % 12 === 3) { at.cancel = false; return true; } return false; }
  function hookInput() {
    if (typeof Input === 'undefined') return false;
    Input.__rrAutotest = true;
    wrap('isTriggered', function (f) { return function (k) { return ok(k) || down(k) || cancel(k) || f.apply(this, arguments); }; });
    wrap('isRepeated', function (f) { return function (k) { return ok(k) || down(k) || f.apply(this, arguments); }; });
    wrap('isPressed', function (f) { return function (k) { return ok(k) || f.apply(this, arguments); }; });
    wrap('update', function (f) {
      return function () {
        f.apply(this, arguments);
        if (at.done || at.phase !== 1 || !onMap() || ($gameMessage && $gameMessage.isBusy())) return;
        var w = Math.floor(at.mapFrames / 20);
        if (w % 4 !== 3) { this._dir4 = [2, 4, 6, 8][(w * 7 + at.index) % 4]; this._dir8 = this._dir4; }
      };
    });
    return true;
  }
  function hookLoop() {
    if (typeof SceneManager === 'undefined' || SceneManager.__rrAutotest) return false;
    SceneManager.__rrAutotest = true;
    var up = SceneManager.updateScene;
    SceneManager.updateScene = function () {
      try { tick(); } catch (e) { log('AUTOTEST ' + e); }
      return up.apply(this, arguments);
    };
    return true;
  }
  var tries = 0;
  var t = setInterval(function () {
    var a = hookInput(), b = hookLoop();
    if ((a && typeof SceneManager !== 'undefined' && SceneManager.__rrAutotest) || ++tries > 600) {
      clearInterval(t);
      log('BOOT ' + (typeof Utils !== 'undefined' ? Utils.RPGMAKER_NAME + ' ' + Utils.RPGMAKER_VERSION : '?') + ' nwjs=' + (typeof Utils !== 'undefined' && Utils.isNwjs && Utils.isNwjs()));
    }
  }, 50);
})();
