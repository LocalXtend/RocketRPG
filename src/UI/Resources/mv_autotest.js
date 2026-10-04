// RocketRPG MV/MZ automated compatibility test (injected only when RR_AUTOTEST is set for the app).
// Same idea as the mkxp-z / EasyRPG autotests: confirm through the title, start a new game, then visit every map the
// game itself transfers to (arriving at those coordinates), walk and press OK so events run, wait for events to end
// before the next transfer, never leave a battle by force. Choices take the default first; a repeated choice gets the
// next option. Progress goes to the host (type 'autotest'), which writes the log file.
(function () {
  'use strict';
  if (location.hostname !== 'rocket.local') return;
  var MAX_MAPS = 60, PER_MAP = 150;
  var at = { frame: 0, phase: 0, exIdle: 0, exWait: 0, maps: [], dests: {}, index: -1, mapFrames: 0, offMap: 0, target: 0, done: false,
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
    if (at.phase === 10) { extraTick(); return; }
    if (at.phase === 0) {
      if (window.__rrExtraTest) {
        // 맵에 도착한 뒤 시작 이벤트가 끝나기를 기다림 (확인 키는 계속 눌러 줌). 너무 오래면 그대로 시험
        if (onMap() && $gameMap.mapId() > 0 && ((idle() && ++at.exIdle > 60) || ++at.exWait > 3000)) { log('EXTRA begin map=' + $gameMap.mapId()); at.phase = 10; at.et = 0; }
        else if (at.frame > 6000) { log('EXTRA FAIL never reached a map'); finish(); }
        else if (at.frame === 900 && !at.forced && !onMap()) forceNewGame();
        return;
      }
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

  // ---- 멀티 엑스트라 모드 시험 (RR_EXTRA_TEST): 가짜 참가자 캐릭터를 넣고 움직임·화면 제한·말 걸기·저장을 확인 ----
  function extraTick() {
    var ext = window.__rocketExtra, et = at.et++;
    var g = ext && ext.guests.get('t1'), c = g && g.char;
    var send = function (keys) { ext.command({ op: 'held', id: 't1', keys: keys }); };
    if (!ext || !ext.installed) { log('EXTRA FAIL script not installed'); finish(); return; }
    if (et === 0) {
      ext.command({ op: 'mode', on: true });
      ext.command({ op: 'guests', list: [{ id: 't1', name: 'test guest', color: '#ffd34d' }] });
      at.exViolations = 0;
      return;
    }
    if (!window.$dataMap || !$gameMap || $gameMap.mapId() <= 0) { at.et--; return; }   // 맵을 불러오는 중: 이 단계를 건너뛰지 않음
    if (et > 2 && c && !c.isMoving() && !c.rrInView(c.x, c.y) && et !== 271) at.exViolations++;
    if (et === 10 && c && !c.rrCanAct() && (at.exWaitAct = (at.exWaitAct || 0) + 1) < 2400) {
      // 이벤트·메시지가 끝나 움직일 수 있을 때까지 기다림 (확인 키로 메시지를 넘김)
      at.exPressOk = true;
      at.et = 10;
      return;
    }
    if (et === 10) {
      at.exPressOk = false;
      if (at.exWaitAct) log('EXTRA waited ' + at.exWaitAct + ' frames for the event to end');
      var sp = scene() && scene()._spriteset && scene()._spriteset._rrGuestSprites;
      log('EXTRA scene ' + sceneName() + ' ' + JSON.stringify(ext.debug && ext.debug()));
      log('EXTRA spawn ' + (c && sp && sp.size === 1 && c.x === $gamePlayer.x && c.y === $gamePlayer.y ? 'ok' : 'FAIL') +
          ' at ' + (c ? c.x + ',' + c.y : '-') + ' player ' + $gamePlayer.x + ',' + $gamePlayer.y + ' image ' + (c ? c.characterName() + '/' + c.characterIndex() : '-'));
      at.exStart = c ? [c.x, c.y] : [0, 0];
      // 갈 수 있는 방향 하나와 그 반대 (방향키 VK: 2 아래 0x28, 4 왼쪽 0x25, 6 오른쪽 0x27, 8 위 0x26)
      var vk = { 2: 0x28, 4: 0x25, 6: 0x27, 8: 0x26 };
      var pass = [2, 4, 6, 8].filter(function (d) { return c && c.canPass(c.x, c.y, d); });
      at.exDir = pass[0] || 6;
      at.exKey = vk[at.exDir];
      at.exBack = vk[10 - at.exDir];
      log('EXTRA passable ' + JSON.stringify(pass) + ' canAct ' + (c && c.rrCanAct()) + ' on=' + ext.on + ' map=' + (scene() instanceof Scene_Map) + ' ev=' + $gameMap.isEventRunning() + ' msg=' + $gameMessage.isBusy() + ' tr=' + $gamePlayer.isTransferring() + ' chg=' + SceneManager.isSceneChanging());
    }
    if (et >= 10 && et < 130 && et % 10 === 0) send([at.exKey]);
    if (et >= 130 && et < 250 && et % 10 === 0) send([at.exBack]);
    if (et === 130) log('EXTRA move ' + at.exDir + ' ' + (c && (c.x !== at.exStart[0] || c.y !== at.exStart[1]) ? 'ok' : 'FAIL') + ' ' + at.exStart + ' -> ' + (c ? c.x + ',' + c.y : '-'));
    if (et === 250) { send([]); log('EXTRA move back ' + (c ? c.x + ',' + c.y : '-') + ' camera violations ' + at.exViolations); }
    if (et === 270 && c) c.locate($gamePlayer.x + $gameMap.screenTileX() + 5, $gamePlayer.y);
    if (et === 274) log('EXTRA off-screen -> ' + (c && c.x === $gamePlayer.x && c.y === $gamePlayer.y ? 'back to host ok' : 'FAIL ' + (c ? c.x + ',' + c.y : '-')));
    if (et === 280 && c && !c.rrCanAct() && (at.exWaitTalk = (at.exWaitTalk || 0) + 1) < 1200) { at.exPressOk = true; at.et = 280; return; }
    if (et === 280) {
      at.exPressOk = false;
      // 말 걸기: 맵의 '결정 키' 이벤트 옆에 세우고 확인 키
      var target = null, spot = null;
      $gameMap.events().some(function (ev) {
        if (!ev.page() || !ev.isTriggerIn([0]) || !ev.isNormalPriority() || ev.list().length <= 1) return false;
        return [[0, 1, 8], [0, -1, 2], [1, 0, 4], [-1, 0, 6]].some(function (o) {
          var x = ev.x + o[0], y = ev.y + o[1];
          if (!$gameMap.isValid(x, y) || !$gameMap.isPassable(x, y, o[2]) || $gameMap.eventsXyNt(x, y).length) return false;
          target = ev; spot = [x, y, o[2]]; return true;
        });
      });
      if (!target) { log('EXTRA talk skipped (no talk event on this map)'); at.et = 300; return; }
      $gamePlayer.locate(spot[0], spot[1]);
      c.locate(spot[0], spot[1]);
      c.setDirection(spot[2]);
      at.exTarget = target;
    }
    if (et === 283 && at.exTarget) {
      var before = 'canAct=' + c.rrCanAct() + ' msg=' + $gameMessage.isBusy() + ' tr=' + $gamePlayer.isTransferring() + ' chg=' + SceneManager.isSceneChanging() + ' map=' + (scene() instanceof Scene_Map) + ' on=' + window.__rocketExtra.on + ' moving=' + c.isMoving() + ' at ' + c.x + ',' + c.y + ' dir ' + c.direction() + ' event ' + at.exTarget.x + ',' + at.exTarget.y +
        ' trig ' + at.exTarget._trigger + ' prio ' + at.exTarget._priorityType + ' running=' + $gameMap.isEventRunning();
      c.rrAction();
      log('EXTRA talk ' + (at.exTarget._starting || $gameMap.isEventRunning() ? 'ok' : 'FAIL') + ' event ' + at.exTarget.eventId() + ' (' + before + ')');
    }
    if (et === 300) {
      var json = '';
      try { json = JsonEx.stringify(DataManager.makeSaveContents()); } catch (e) { json = 'error ' + e; }
      log('EXTRA save ' + (json.indexOf('RocketGuest') < 0 && json.indexOf('_rrGuest') < 0 ? 'clean' : 'FAIL contains guest'));
      ext.command({ op: 'mode', on: false });
    }
    if (et === 306) {
      var sp2 = scene() && scene()._spriteset && scene()._spriteset._rrGuestSprites;
      log('EXTRA off ' + (!sp2 || sp2.size === 0 ? 'sprites removed' : 'FAIL ' + sp2.size));
      log('EXTRA DONE violations=' + at.exViolations);
      finish();
    }
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
    if (at.done || k !== 'ok' || at.frame % 12 !== 0 || needDown() || (at.phase === 10 && !at.exPressOk)) return false;
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
