(function() {
    if (window.__rocketBridgeInstalled) return;
    window.__rocketBridgeInstalled = true;

    const bridge = {
        speed: 1.0,
        paused: false,
        espEnabled: false,
        tileInspectorEnabled: false,
        frozenSwitches: {},
        frozenVariables: {},
        mousePos: { x: 0, y: 0 },
        font: null,              // { family, size, bold }
        brightness: 1.0,
        autoMessage: false,
        autoSpeed: 1.0,
        skipMessage: false,
        msgBusy: false,
        msgWaitStart: 0,
        guestOkAt: 0,            // 멀티 엑스트라 모드: 참가자가 대화 중에 결정 키를 누른 때 (rocket_extra.js)
        guestOkHitAt: 0
    };
    window.__rocketBridge = bridge;
    // 참가자 결정 키: 방장이 누른 것처럼 대사를 한 번 넘김 (선택지·숫자 입력은 넘기지 않음)
    bridge.guestConfirm = function() { bridge.guestOkAt = performance.now(); bridge.guestOkHitAt = 0; };

    // 1. Host -> Web 메시징
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', function(event) {
            try {
                const msg = event.data;
                if (!msg || !msg.type) return;
                handleHostCommand(msg);
            } catch (err) {
                console.error('[RocketBridge] Command error:', err);
            }
        });
    }

    function sendToHost(type, payload) {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(Object.assign({ type: type }, payload));
        }
    }

    // 2. DOM 인게임 오버레이 레이어 관리
    let overlayDiv = null;
    let espContainer = null;
    let tileHudDiv = null;
    let toastContainer = null;

    function ensureOverlay() {
        if (!overlayDiv && document.body) {
            overlayDiv = document.createElement('div');
            overlayDiv.id = '__rocketOverlay';
            overlayDiv.style.cssText = 'position:fixed;top:0;left:0;width:100%;height:100%;pointer-events:none;z-index:2147483647;overflow:hidden;font-family:sans-serif;user-select:none;';

            espContainer = document.createElement('div');
            espContainer.id = '__rocketEspContainer';
            espContainer.style.cssText = 'position:absolute;top:0;left:0;width:100%;height:100%;pointer-events:none;';
            overlayDiv.appendChild(espContainer);

            tileHudDiv = document.createElement('div');
            tileHudDiv.id = '__rocketTileHud';
            tileHudDiv.style.cssText = 'position:fixed;left:16px;bottom:16px;padding:8px 12px;background:rgba(30,30,30,0.92);border:1px solid #007acc;border-radius:4px;color:#00ffcc;font-size:13px;font-family:Consolas,monospace;font-weight:600;display:none;pointer-events:none;box-shadow:0 4px 12px rgba(0,0,0,0.5);';
            overlayDiv.appendChild(tileHudDiv);

            toastContainer = document.createElement('div');
            toastContainer.id = '__rocketToastContainer';
            toastContainer.style.cssText = 'position:fixed;bottom:32px;left:50%;transform:translateX(-50%);display:flex;flex-direction:column;align-items:center;gap:8px;pointer-events:none;z-index:2147483647;';
            overlayDiv.appendChild(toastContainer);

            document.body.appendChild(overlayDiv);
        }
        return overlayDiv;
    }

    // 알림은 RocketRPG 오버레이 한 곳에서만 보여 줍니다 (페이지 안에도 그리면 두 개가 겹쳐 보였음).
    function showToast(msg) {
        try { window.chrome.webview.postMessage({ type: 'notification', message: String(msg) }); } catch (e) { }
    }

    // 불러오기가 끝나 첫 장면이 그려지기 시작하면 RocketRPG에 알립니다 (그때 '불러오는 중' 화면을 닫음)
    (function rrReadyPoll() {
        try {
            // Scene_Boot(파일 불러오는 장면)이 끝나고 첫 진짜 장면(로고/타이틀/맵)이 떴을 때
            if (window.SceneManager && SceneManager._scene && window.Scene_Boot && !(SceneManager._scene instanceof Scene_Boot) && Graphics.frameCount > 2) {
                window.chrome.webview.postMessage({ type: 'gameReady' });
                return;
            }
        } catch (e) { }
        setTimeout(rrReadyPoll, 100);
    })();

    // 화면 프레임 상한 (화면 > 프레임). MV/MZ는 게임 진행이 60 FPS 고정 단계로 돌기 때문에 그리는 횟수만 바뀝니다.
    // 0 = 제한 없음(모니터 주사율). requestAnimationFrame을 감싸 상한보다 이른 프레임은 건너뜁니다.
    // 같은 프레임(같은 t)의 콜백들은 한 번 내린 결정을 함께 씁니다. 예전에는 '마지막 프레임 시각'을 콜백마다 갱신해서,
    // 게임 루프·ESP 등 여러 콜백이 서로를 다음 프레임으로 계속 미루다 게임이 아예 멈췄습니다(검은 화면).
    let rrFrameCap = 60, rrLastAllowed = -1e9, rrDecisionT = -1, rrAllowed = true, rrNextId = 1;
    const rrRaf = window.requestAnimationFrame.bind(window);
    const rrCaf = window.cancelAnimationFrame.bind(window);
    const rrLive = new Map(); // 우리 id -> 지금 걸려 있는 실제 id (미룬 콜백도 취소되게)
    function rrAllow(t) {
        if (rrFrameCap <= 0) return true;
        if (t !== rrDecisionT) {
            rrDecisionT = t;
            rrAllowed = t - rrLastAllowed >= 1000 / rrFrameCap - 1;
            if (rrAllowed) rrLastAllowed = t;
        }
        return rrAllowed;
    }
    window.requestAnimationFrame = function (cb) {
        const id = rrNextId++;
        const step = function (t) {
            if (!rrAllow(t)) { rrLive.set(id, rrRaf(step)); return; }
            rrLive.delete(id);
            cb(t);
        };
        rrLive.set(id, rrRaf(step));
        return id;
    };
    window.cancelAnimationFrame = function (id) {
        const real = rrLive.get(id);
        if (real !== undefined) { rrCaf(real); rrLive.delete(id); }
    };

    function handleHostCommand(msg) {
        switch (msg.type) {
            case 'setFrameRate':
                rrFrameCap = Math.max(0, Number(msg.fps) || 0);
                break;
            case 'setSpeed':
                bridge.speed = Number(msg.speed) || 1.0;
                break;
            case 'setPause':
                bridge.paused = !!msg.paused;
                break;
            case 'togglePause':
                bridge.paused = !bridge.paused;
                break;
            case 'toggleNoclip':
                if (typeof $gamePlayer !== 'undefined' && $gamePlayer) {
                    $gamePlayer._through = (msg.through !== undefined && msg.through !== null) ? !!msg.through : !$gamePlayer._through;
                }
                break;
            case 'warp':
                if (typeof $gamePlayer !== 'undefined' && $gamePlayer && $gamePlayer.reserveTransfer) {
                    const dir = msg.direction || ($gamePlayer.direction ? $gamePlayer.direction() : 2);
                    $gamePlayer.reserveTransfer(Number(msg.mapId), Number(msg.x), Number(msg.y), dir, 0);
                    if (typeof SceneManager !== 'undefined' && typeof Scene_Map !== 'undefined' && SceneManager._scene && !(SceneManager._scene instanceof Scene_Map)) {
                        SceneManager.goto(Scene_Map);
                    }
                    const warpMsg = '맵 ' + msg.mapId + ' (' + msg.x + ', ' + msg.y + ') 워프 완료';
                    showToast(warpMsg);
                }
                break;
            case 'quickSave':
                performQuickSave();
                break;
            case 'quickLoad':
                performQuickLoad();
                break;
            case 'forceSaveMenu':
                if (typeof SceneManager !== 'undefined' && typeof Scene_Save !== 'undefined') {
                    if (typeof $gameSystem !== 'undefined' && $gameSystem.enableSave) {
                        $gameSystem.enableSave();
                    }
                    if (!SceneManager._scene || !(SceneManager._scene instanceof Scene_Save)) {
                        SceneManager.push(Scene_Save);
                        const fsMsg = '강제 세이브 메뉴 열림';
                        showToast(fsMsg);
                    }
                }
                break;
            case 'forceLoadMenu':
                if (typeof SceneManager !== 'undefined' && typeof Scene_Load !== 'undefined') {
                    if (!SceneManager._scene || !(SceneManager._scene instanceof Scene_Load)) {
                        SceneManager.push(Scene_Load);
                        const flMsg = '강제 로드 메뉴 열림';
                        showToast(flMsg);
                    }
                }
                break;
            case 'setVolume':
                applyVolume(Number(msg.volume));
                break;
            case 'setSwitch':
                if (typeof $gameSwitches !== 'undefined' && $gameSwitches) {
                    $gameSwitches.setValue(Number(msg.id), !!msg.value);
                }
                break;
            case 'setVariable':
                if (typeof $gameVariables !== 'undefined' && $gameVariables) {
                    let val = msg.value;
                    if (!isNaN(val) && val !== '' && val !== null) val = Number(val);
                    $gameVariables.setValue(Number(msg.id), val);
                }
                break;
            case 'freezeSwitch':
                if (msg.frozen) {
                    bridge.frozenSwitches[msg.id] = !!msg.value;
                    if (typeof $gameSwitches !== 'undefined') $gameSwitches.setValue(Number(msg.id), !!msg.value);
                } else {
                    delete bridge.frozenSwitches[msg.id];
                }
                break;
            case 'freezeVariable':
                if (msg.frozen) {
                    let fval = msg.value;
                    if (!isNaN(fval) && fval !== '' && fval !== null) fval = Number(fval);
                    bridge.frozenVariables[msg.id] = fval;
                    if (typeof $gameVariables !== 'undefined') $gameVariables.setValue(Number(msg.id), fval);
                } else {
                    delete bridge.frozenVariables[msg.id];
                }
                break;
            case 'enableEsp':
                bridge.espEnabled = !!msg.enabled;
                /* 호스트가 알림을 띄움 */
                break;
            case 'enableTileInspector':
                bridge.tileInspectorEnabled = !!msg.enabled;
                /* 호스트가 알림을 띄움 */
                break;
            case 'requestDataInspector':
                sendDataInspector();
                break;
            case 'setFont':
                bridge.font = { family: msg.family || '', size: Number(msg.size) || 0, bold: !!msg.bold };
                applyFont();
                break;
            case 'setBrightness':
                bridge.brightness = Math.max(0.05, Math.min(4.0, Number(msg.value) || 1.0));
                applyBrightness();
                break;
            case 'setAutoMessage':
                bridge.autoMessage = !!msg.enabled;
                bridge.autoSpeed = Number(msg.speed) || 1.0;
                break;
            case 'setSkipMessage':
                bridge.skipMessage = !!msg.enabled;
                break;
            case 'advanceMessage':
                advanceMessage();
                break;
            case 'setFilter':
                bridge.filter = String(msg.name || 'none');
                break;
            case 'restart':
                window.location.reload();
                break;
        }
    }

    function applyVolume(vol) {
        try {
            if (typeof AudioManager !== 'undefined') {
                AudioManager.masterVolume = vol;
            }
            if (typeof WebAudio !== 'undefined' && WebAudio.setMasterVolume) {
                WebAudio.setMasterVolume(vol);
            }
        } catch (e) { }
    }

    function performQuickSave() {
        try {
            if (typeof $gameSystem !== 'undefined' && typeof DataManager !== 'undefined') {
                $gameSystem.onBeforeSave();
                const res = DataManager.saveGame(1);
                if (res && typeof res.then === 'function') {
                    res.then(() => {
                        if (typeof SoundManager !== 'undefined') SoundManager.playSave();
                        const sMsg = '1번 슬롯에 퀵 세이브 완료';
                        showToast(sMsg);
                    }).catch(() => {
                        if (typeof SoundManager !== 'undefined') SoundManager.playBuzzer();
                        const eMsg = '퀵 세이브 실패';
                        showToast(eMsg);
                    });
                } else if (res) {
                    if (typeof StorageManager !== 'undefined' && StorageManager.cleanBackup) StorageManager.cleanBackup(1);
                    if (typeof SoundManager !== 'undefined') SoundManager.playSave();
                    const sMsg = '1번 슬롯에 퀵 세이브 완료';
                    showToast(sMsg);
                } else {
                    if (typeof SoundManager !== 'undefined') SoundManager.playBuzzer();
                    const eMsg = '퀵 세이브 실패';
                    showToast(eMsg);
                }
            }
        } catch (e) {
            console.error('[RocketBridge] QuickSave error:', e);
            if (typeof SoundManager !== 'undefined') SoundManager.playBuzzer();
            const eMsg = '퀵 세이브 오류: ' + (e.message || e);
            showToast(eMsg);
        }
    }

    function performQuickLoad() {
        try {
            if (typeof DataManager !== 'undefined') {
                const res = DataManager.loadGame(1);
                if (res && typeof res.then === 'function') {
                    res.then(() => {
                        if (typeof SoundManager !== 'undefined') SoundManager.playLoad();
                        if (typeof SceneManager !== 'undefined') SceneManager.goto(Scene_Map);
                        if (typeof $gameSystem !== 'undefined') $gameSystem.onAfterLoad();
                        const lMsg = '1번 슬롯 퀵 로드 완료';
                        showToast(lMsg);
                    }).catch(() => {
                        if (typeof SoundManager !== 'undefined') SoundManager.playBuzzer();
                        const eMsg = '1번 슬롯에 저장 데이터가 없습니다';
                        showToast(eMsg);
                    });
                } else if (res) {
                    if (typeof SoundManager !== 'undefined') SoundManager.playLoad();
                    if (typeof SceneManager !== 'undefined') SceneManager.goto(Scene_Map);
                    if (typeof $gameSystem !== 'undefined') $gameSystem.onAfterLoad();
                    const lMsg = '1번 슬롯 퀵 로드 완료';
                    showToast(lMsg);
                } else {
                    if (typeof SoundManager !== 'undefined') SoundManager.playBuzzer();
                    const eMsg = '1번 슬롯에 저장 데이터가 없습니다';
                    showToast(eMsg);
                }
            }
        } catch (e) {
            console.error('[RocketBridge] QuickLoad error:', e);
            if (typeof SoundManager !== 'undefined') SoundManager.playBuzzer();
            const eMsg = '퀵 로드 오류: ' + (e.message || e);
            showToast(eMsg);
        }
    }

    function enforceFrozen() {
        if (typeof $gameSwitches !== 'undefined' && $gameSwitches && $gameSwitches.setValue && $gameSwitches.value) {
            for (let id in bridge.frozenSwitches) {
                if (Object.prototype.hasOwnProperty.call(bridge.frozenSwitches, id)) {
                    const numId = Number(id);
                    const target = bridge.frozenSwitches[id];
                    if ($gameSwitches.value(numId) !== target) {
                        $gameSwitches.setValue(numId, target);
                    }
                }
            }
        }
        if (typeof $gameVariables !== 'undefined' && $gameVariables && $gameVariables.setValue && $gameVariables.value) {
            for (let id in bridge.frozenVariables) {
                if (Object.prototype.hasOwnProperty.call(bridge.frozenVariables, id)) {
                    const numId = Number(id);
                    const target = bridge.frozenVariables[id];
                    if ($gameVariables.value(numId) !== target) {
                        $gameVariables.setValue(numId, target);
                    }
                }
            }
        }
    }

    // 2-1. 인게임 글꼴: MV(standardFontFace/Size), MZ(Game_System.mainFontFace/Size) 훅
    function hookFont() {
        if (typeof Window_Base === 'undefined') return false;
        if (Window_Base.prototype.__rocketFont) return true;
        Window_Base.prototype.__rocketFont = true;
        const wrapFace = function(orig) {
            return function() {
                const f = bridge.font;
                const base = orig.call(this);
                return (f && f.family) ? ('\'' + f.family + '\', ' + base) : base;
            };
        };
        const wrapSize = function(orig) {
            return function() {
                const f = bridge.font;
                return (f && f.size > 0) ? f.size : orig.call(this);
            };
        };
        const wb = Window_Base.prototype;
        if (typeof wb.standardFontFace === 'function') wb.standardFontFace = wrapFace(wb.standardFontFace);
        if (typeof wb.standardFontSize === 'function') wb.standardFontSize = wrapSize(wb.standardFontSize);
        if (typeof Game_System !== 'undefined') {
            const gs = Game_System.prototype;
            if (typeof gs.mainFontFace === 'function') gs.mainFontFace = wrapFace(gs.mainFontFace);
            if (typeof gs.mainFontSize === 'function') gs.mainFontSize = wrapSize(gs.mainFontSize);
        }
        if (typeof wb.resetFontSettings === 'function') {
            const reset = wb.resetFontSettings;
            wb.resetFontSettings = function() {
                reset.call(this);
                const f = bridge.font;
                if (f && f.bold && this.contents) this.contents.fontBold = true;
            };
        }
        return true;
    }

    function applyFont() {
        hookFont();
        try {
            const f = bridge.font;
            if (f && f.family && document.fonts && document.fonts.load) {
                document.fonts.load('16px \'' + f.family + '\'');
            }
        } catch (e) { }
    }

    // 2-2. 밝기: 게임 캔버스에 CSS 필터 적용 (WebView는 WPF 쉐이더가 닿지 않음)
    function applyBrightness() {
        const canvas = (typeof Graphics !== 'undefined' && Graphics._canvas) ? Graphics._canvas : document.querySelector('canvas');
        if (!canvas) return;
        const want = Math.abs(bridge.brightness - 1.0) < 0.001 ? '' : 'brightness(' + bridge.brightness + ')';
        if (canvas.style.filter !== want) canvas.style.filter = want;
    }

    // 2-3. 메시지 자동 넘김 / 고속 스킵 / 대화 감지
    function autoWaitMs() {
        const len = (typeof $gameMessage !== 'undefined' && $gameMessage && $gameMessage.allText) ? $gameMessage.allText().length : 20;
        const raw = Math.min(4500, Math.max(750, 1100 + len * 30));
        return raw / Math.max(0.2, bridge.autoSpeed || 1.0);
    }

    function hookMessage() {
        if (typeof Window_Message === 'undefined') return false;
        const wm = Window_Message.prototype;
        if (wm.__rocketMsg) return true;
        wm.__rocketMsg = true;

        if (typeof wm.startPause === 'function') {
            const origPause = wm.startPause;
            wm.startPause = function() {
                bridge.msgWaitStart = performance.now();
                return origPause.apply(this, arguments);
            };
        }
        if (typeof wm.isTriggered === 'function') {
            const origTrig = wm.isTriggered;
            wm.isTriggered = function rrMsgTriggered() {
                if (bridge.forceAdvance) { bridge.forceAdvance = false; return true; }
                if (guestOkPending()) { bridge.guestOkAt = 0; return true; }
                if (bridge.skipMessage) return true;
                if (bridge.autoMessage && this.pause && bridge.msgWaitStart &&
                    performance.now() - bridge.msgWaitStart >= autoWaitMs()) {
                    bridge.msgWaitStart = 0;
                    bridge.lastAutoOk = performance.now();
                    return true;
                }
                return origTrig.call(this);
            };
            wm.isTriggered.__rrMsg = true;
        }
        if (typeof wm.updateShowFast === 'function') {
            const origFast = wm.updateShowFast;
            wm.updateShowFast = function() {
                origFast.call(this);
                if (bridge.skipMessage) this._showFast = true;
            };
        }
        return true;
    }

    // 메시지 플러그인이 Window_Message를 바꿔 둔 게임에서도 자동 진행/스킵이 되도록, 입력 단계에서도 확인을 넣습니다.
    // $gameMessage에 글이 떠 있고 선택지/숫자 입력이 아닐 때만. 표준 창 훅이 이미 넘겼으면 끼어들지 않습니다.
    // 우리 감싸기(브릿지/자동 테스트)는 감싼 대상(__rrWraps)과 주인(__rrOwner)을 기록합니다. 사슬에 내 것이 남아 있으면 다시
    // 감싸지 않습니다 (서로를 '바뀐 것'으로 보고 번갈아 감싸면 호출이 끝없이 깊어져 게임이 멈췄음).
    function rrChainHas(fn, owner) {
        for (let i = 0; fn && i < 40; i++) { if (fn.__rrOwner === owner) return true; fn = fn.__rrWraps; }
        return false;
    }
    function rrWrap(obj, name, owner, make) {
        const cur = obj[name];
        if (typeof cur !== 'function' || rrChainHas(cur, owner)) return;
        const w = make(cur);
        w.__rrOwner = owner;
        w.__rrWraps = cur;
        obj[name] = w;
    }
    // 일반 대사가 떠 있는지 (선택지/숫자 입력/아이템 선택이 아님)
    function msgPlain() {
        if (typeof $gameMessage === 'undefined' || !$gameMessage || !$gameMessage.hasText || !$gameMessage.hasText()) return false;
        return !(($gameMessage.isChoice && $gameMessage.isChoice()) || ($gameMessage.isNumberInput && $gameMessage.isNumberInput()) ||
            ($gameMessage.isItemChoice && $gameMessage.isItemChoice()));
    }
    // 참가자 결정 키 (엑스트라 모드). 대화창이 묻지 않는 사이(입력 대기 전 잠깐 쉬는 startWait 10 등)에 누른 것은
    // 0.5초만 기억합니다 (늦게 다음 대사까지 넘기지 않게).
    function guestOkPending() {
        if (!bridge.guestOkAt) return false;
        if (performance.now() - bridge.guestOkAt > 500 || !msgPlain()) { bridge.guestOkAt = 0; return false; }
        return true;
    }
    // 메시지 창을 바꿔 둔 게임: 입력 단계에서 진짜 키처럼 한 프레임 동안만 'ok'를 눌린 것으로.
    // 표준 대화창(우리 isTriggered를 쓰는 창)이면 그 창이 직접 가져갑니다. 여기서도 주면, 창이 쉬는 동안 매 프레임 'ok'를 묻는
    // 다른 플러그인이 가져가 대사가 넘어가지 않습니다.
    function guestOkInput() {
        if (!guestOkPending()) return false;
        const s = (typeof SceneManager !== 'undefined') ? SceneManager._scene : null;
        const w = s && s._messageWindow;
        if (w && w.isTriggered && w.isTriggered.__rrMsg) return false;
        const now = performance.now();
        if (!bridge.guestOkHitAt) bridge.guestOkHitAt = now;
        if (now - bridge.guestOkHitAt < 8) return true;
        bridge.guestOkAt = 0;
        return false;
    }
    function msgWantsOk() {
        if (guestOkInput()) return true;
        if (!msgPlain()) return false;
        if (bridge.skipMessage) return true;
        if (!bridge.autoMessage) return false;
        const since = Math.max(bridge.msgShownAt || 0, bridge.lastAutoOk || 0);
        if (performance.now() - since < autoWaitMs() * 1.5) return false;
        bridge.lastAutoOk = performance.now();
        return true;
    }
    function hookInputAuto() {
        if (typeof Input === 'undefined' || !Input.isTriggered) return false;
        rrWrap(Input, 'isTriggered', 'bridge', function(t) { return function(k) { return (k === 'ok' && msgWantsOk()) || t.apply(this, arguments); }; });
        rrWrap(Input, 'isRepeated', 'bridge', function(r) { return function(k) { return (k === 'ok' && ((bridge.skipMessage && msgWantsOk()) || guestOkInput())) || r.apply(this, arguments); }; });
        return true;
    }
    function advanceMessage() {
        bridge.forceAdvance = true;
    }

    // 대사 기록용: \C[n], \N[n], \{, \. 같은 제어 문자를 제거하고 \N[n]/\V[n]은 실제 값으로 치환
    function cleanMessageText(raw) {
        let t = String(raw || '');
        try {
            t = t.replace(/\\V\[(\d+)\]/gi, function(_, n) { return (typeof $gameVariables !== 'undefined') ? String($gameVariables.value(Number(n))) : ''; });
            t = t.replace(/\\N\[(\d+)\]/gi, function(_, n) {
                const a = (typeof $gameActors !== 'undefined') ? $gameActors.actor(Number(n)) : null;
                return a ? a.name() : '';
            });
            t = t.replace(/\\P\[(\d+)\]/gi, function(_, n) {
                const a = (typeof $gameParty !== 'undefined') ? $gameParty.members()[Number(n) - 1] : null;
                return a ? a.name() : '';
            });
            t = t.replace(/\\G/gi, (typeof TextManager !== 'undefined' && TextManager.currencyUnit) ? TextManager.currencyUnit : '');
        } catch (e) { }
        t = t.replace(/\\[A-Za-z]+\[[^\]]*\]/g, '').replace(/\\[A-Za-z]+<[^>]*>/g, '').replace(/\\[{}.|!><^$]/g, '').replace(/\\\\/g, '\\');
        return t;
    }

    // 선택지 투표(멀티): 선택지 창이 떠 있는 동안 글과 고를 수 있는지를 알림 (바뀔 때만)
    let lastChoiceKey = '', choiceGen = 0;
    function choiceWindow() {
        const s = (typeof SceneManager !== 'undefined') ? SceneManager._scene : null;
        if (!s) return null;
        return s._choiceListWindow || (s._messageWindow && s._messageWindow._choiceWindow) || null;
    }
    // WebGL 화면을 잃으면(그래픽 메모리 부족, GPU 재시작) 알림. MV/MZ는 스스로 되살리지 못해 화면이 깨집니다.
    function watchContextLoss() {
        const c = (typeof Graphics !== 'undefined' && Graphics._canvas) ? Graphics._canvas : null;
        if (!c || c.__rrCtxWatch) return;
        c.__rrCtxWatch = true;
        c.addEventListener('webglcontextlost', function () { sendToHost('contextLost', {}); });
    }

    function pollChoiceState() {
        if (typeof $gameMessage === 'undefined' || !$gameMessage || !$gameMessage.isChoice) return;
        const w = choiceWindow();
        const open = !!($gameMessage.isChoice() && w && (w.active || (w.isOpen && w.isOpen())));
        let items = [];
        if (open) {
            const list = $gameMessage.choices() || [];
            items = list.slice(0, 16).map(function (c, i) {
                let enabled = true;
                try { if (w._list && w._list[i] && w._list[i].enabled === false) enabled = false; } catch (e) { }
                return { t: cleanMessageText(String(c)).replace(/\n/g, ' '), e: enabled };
            });
        }
        const key = open ? JSON.stringify(items) : '';
        if (key === lastChoiceKey) return;
        let picked = -1;
        if (!open) { try { picked = (typeof $gameMessage._choiceIndexResult === 'number') ? $gameMessage._choiceIndexResult : -1; } catch (e) { } }
        if (open) choiceGen++;
        else if (!lastChoiceKey) return;
        lastChoiceKey = key;
        sendToHost('choiceState', { open: open, gen: choiceGen, items: items, picked: picked });
    }

    let lastMsgText = null;
    function pollMessageState() {
        if (typeof $gameMessage === 'undefined' || !$gameMessage) return;
        const busy = !!($gameMessage.hasText && $gameMessage.hasText());
        const text = busy && $gameMessage.allText ? cleanMessageText($gameMessage.allText()) : '';
        const speaker = (busy && typeof $gameMessage.speakerName === 'function') ? ($gameMessage.speakerName() || '') : '';
        if (busy !== bridge.msgBusy || text !== lastMsgText) {
            if (busy) bridge.msgShownAt = performance.now();
            bridge.msgBusy = busy;
            lastMsgText = text;
            sendToHost('messageState', { busy: busy, speaker: speaker, text: text });
        }
    }

    // 2-4. ESP: 단일 2D 캔버스에 그림 (DOM 재생성 없음), 화면 줌/스케일 추종
    let espCanvas = null;
    let espCtx = null;
    function ensureEspCanvas() {
        ensureOverlay();
        if (!espCanvas && espContainer) {
            espCanvas = document.createElement('canvas');
            espCanvas.style.cssText = 'position:absolute;top:0;left:0;width:100%;height:100%;pointer-events:none;';
            espContainer.appendChild(espCanvas);
            espCtx = espCanvas.getContext('2d');
        }
        return espCtx;
    }

    function clearEsp() {
        if (espCtx && espCanvas) espCtx.clearRect(0, 0, espCanvas.width, espCanvas.height);
    }

    const espColors = ['#0099ff', '#00cc44', '#00cc44', '#ff8800', '#bb33ff'];
    function drawEsp() {
        requestAnimationFrame(drawEsp);
        // 오버레이 오류가 게임 오류로 번지지 않게 합니다.
        try { drawEspFrame(); } catch (e) { clearEsp(); }
    }
    function drawEspFrame() {
        if (!bridge.espEnabled) { clearEsp(); return; }
        // 맵 이동 중에는 $dataMap이 잠깐 null — 이때 이벤트 화면 좌표를 구하면 게임 함수가 예외를 내고, 그게 게임 오류로 잡히던 문제
        if (typeof $gameMap === 'undefined' || !$gameMap || !$gameMap.events || !window.$dataMap) { clearEsp(); return; }
        if (typeof SceneManager === 'undefined' || typeof Scene_Map === 'undefined' || !(SceneManager._scene instanceof Scene_Map)) { clearEsp(); return; }
        const canvas = (typeof Graphics !== 'undefined' && Graphics._canvas) ? Graphics._canvas : document.querySelector('canvas');
        const ctx = ensureEspCanvas();
        if (!canvas || !ctx) return;

        const dpr = window.devicePixelRatio || 1;
        const cw = Math.round(window.innerWidth * dpr), ch = Math.round(window.innerHeight * dpr);
        if (espCanvas.width !== cw || espCanvas.height !== ch) { espCanvas.width = cw; espCanvas.height = ch; }
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, cw, ch);

        const rect = canvas.getBoundingClientRect();
        const gw = Graphics.width || canvas.width || 816;
        const gh = Graphics.height || canvas.height || 624;
        const sx = rect.width / gw, sy = rect.height / gh;
        const tw = $gameMap.tileWidth ? $gameMap.tileWidth() : 48;
        const th = $gameMap.tileHeight ? $gameMap.tileHeight() : 48;

        // $gameScreen 줌 반영: 화면 좌표 p → zoomX + (p - zoomX) * scale
        let zs = 1, zx = 0, zy = 0;
        if (typeof $gameScreen !== 'undefined' && $gameScreen && $gameScreen.zoomScale) {
            zs = $gameScreen.zoomScale() || 1;
            zx = $gameScreen.zoomX();
            zy = $gameScreen.zoomY();
        }

        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.save();
        ctx.beginPath();
        ctx.rect(rect.left, rect.top, rect.width, rect.height);
        ctx.clip();
        ctx.lineWidth = 2;
        ctx.font = '600 10px Malgun Gothic, sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'bottom';

        const events = $gameMap.events();
        for (let i = 0; i < events.length; i++) {
            const ev = events[i];
            if (!ev || typeof ev.screenX !== 'function') continue;
            const ex = zx + (ev.screenX() - tw / 2 - zx) * zs;
            const ey = zy + (ev.screenY() - th - zy) * zs;
            const x = rect.left + ex * sx, y = rect.top + ey * sy;
            const w = tw * zs * sx, h = th * zs * sy;
            if (x + w < rect.left || y + h < rect.top || x > rect.right || y > rect.bottom) continue;

            const trig = (typeof ev._trigger === 'number') ? ev._trigger : 0;
            const color = espColors[trig] || '#00ffff';
            ctx.strokeStyle = color;
            ctx.fillStyle = color + '33';
            ctx.fillRect(x, y, w, h);
            ctx.strokeRect(x + 1, y + 1, w - 2, h - 2);

            const data = ev.event ? ev.event() : null;
            const name = data && data.name ? data.name : ('EV' + ev.eventId());
            const tw2 = ctx.measureText(name).width + 6;
            ctx.fillStyle = 'rgba(0,0,0,0.75)';
            ctx.fillRect(x + w / 2 - tw2 / 2, y - 14, tw2, 13);
            ctx.fillStyle = '#ffffff';
            ctx.fillText(name, x + w / 2, y - 2);
        }
        ctx.restore();
    }
    requestAnimationFrame(drawEsp);

    // 2-4b. 화면 필터: PIXI 필터(GLSL)로 게임 장면에 직접 적용 (CRT 주사선/마스크, 선명도 보정)
    //   PIXI v4(MV)는 filterArea, v5(MZ)는 inputSize 기본 유니폼으로 픽셀 좌표를 계산합니다.
    const filterCache = {};
    function pixiMajor() {
        try { return parseInt(String(PIXI.VERSION).split('.')[0], 10) || 4; } catch (e) { return 4; }
    }
    function makeFilter(name) {
        if (typeof PIXI === 'undefined' || !PIXI.Filter) return null;
        const v5 = pixiMajor() >= 5;
        const head = [
            // v5 기본 버텍스 셰이더의 inputSize(highp)와 정밀도가 달라지면 링크 오류가 나므로 highp로 맞춥니다.
            'precision highp float;',
            'varying vec2 vTextureCoord;',
            'uniform sampler2D uSampler;',
            v5 ? 'uniform vec4 inputSize;' : 'uniform vec4 filterArea;',
            v5 ? 'vec2 pixelPos() { return vTextureCoord * inputSize.xy; }' : 'vec2 pixelPos() { return vTextureCoord * filterArea.xy; }',
            v5 ? 'vec2 texel() { return inputSize.zw; }' : 'vec2 texel() { return 1.0 / filterArea.xy; }'
        ];
        let body;
        if (name === 'crt_royale') {
            body = [
                'void main(void) {',
                '  vec4 c = texture2D(uSampler, vTextureCoord);',
                '  vec2 p = pixelPos();',
                '  float scan = mod(floor(p.y), 2.0) < 1.0 ? 1.0 : 0.82;',
                '  float m = mod(floor(p.x), 3.0);',
                '  vec3 mask = m < 1.0 ? vec3(1.05, 0.97, 0.97) : (m < 2.0 ? vec3(0.97, 1.05, 0.97) : vec3(0.97, 0.97, 1.05));',
                '  gl_FragColor = vec4(c.rgb * scan * mask * 1.06, c.a);',
                '}'
            ];
        } else {
            // xbrz_cas / fsr_cas / scalefx_cas / quality → CAS 계열 선명도 보정 (게임 해상도에서 적용)
            body = [
                'void main(void) {',
                '  vec2 t = texel();',
                '  vec4 c = texture2D(uSampler, vTextureCoord);',
                '  vec3 n = texture2D(uSampler, vTextureCoord + vec2(0.0, -t.y)).rgb;',
                '  vec3 s = texture2D(uSampler, vTextureCoord + vec2(0.0, t.y)).rgb;',
                '  vec3 e = texture2D(uSampler, vTextureCoord + vec2(t.x, 0.0)).rgb;',
                '  vec3 w = texture2D(uSampler, vTextureCoord + vec2(-t.x, 0.0)).rgb;',
                '  vec3 mn = min(c.rgb, min(min(n, s), min(e, w)));',
                '  vec3 mx = max(c.rgb, max(max(n, s), max(e, w)));',
                '  vec3 amp = clamp(min(mn, 1.0 - mx) / max(mx, 0.0001), 0.0, 1.0);',
                '  vec3 k = -sqrt(amp) * 0.18;',
                '  vec3 r = (c.rgb + k * (n + s + e + w)) / (1.0 + 4.0 * k);',
                '  gl_FragColor = vec4(clamp(r, 0.0, 1.0), c.a);',
                '}'
            ];
        }
        try { return new PIXI.Filter(null, head.concat(body).join('\n')); }
        catch (e) { console.error('[RocketBridge] filter', e); return null; }
    }
    function applyFilter() {
        if (typeof SceneManager === 'undefined' || !SceneManager._scene) return;
        const scene = SceneManager._scene;
        const want = bridge.filter || 'none';
        if (want !== 'none' && !(want in filterCache)) {
            filterCache[want] = makeFilter(want);
            if (filterCache[want]) filterCache[want].__rr = true;
        }
        const f = want === 'none' ? null : filterCache[want];
        const cur = scene.filters || [];
        // 게임/플러그인이 장면에 건 필터는 유지하고 RocketRPG 필터만 교체합니다 (장면이 filters를 다시 설정해도 복구).
        if (f ? (cur.length && cur[cur.length - 1] === f) : !cur.some(x => x && x.__rr)) return;
        const keep = cur.filter(x => x && !x.__rr);
        if (f) keep.push(f);
        scene.filters = keep.length ? keep : null;
    }

    // 2-5. 게임 자체 오류 화면 감지 → 호스트가 원본 실행(Game.exe) 모드로 대체 실행
    function reportFatal(kind, name, message, stack) {
        if (bridge.fatalSent) return;
        bridge.fatalSent = true;
        sendToHost('fatal', { kind: kind, name: String(name || ''), message: String(message || ''), stack: String(stack || '').slice(0, 2000) });
    }
    function hookErrors() {
        if (typeof Graphics === 'undefined') return false;
        if (Graphics.__rocketErr) return true;
        Graphics.__rocketErr = true;
        if (typeof Graphics.printError === 'function') {
            const origPrint = Graphics.printError;
            Graphics.printError = function(name, message, error) {
                reportFatal('error', name, message, error && error.stack);
                return origPrint.apply(this, arguments);
            };
        }
        if (typeof Graphics.printLoadingError === 'function') {
            const origLoad = Graphics.printLoadingError;
            Graphics.printLoadingError = function(url) {
                reportFatal('load', 'LoadingError', url, '');
                return origLoad.apply(this, arguments);
            };
        }
        return true;
    }

    // 3. 핫키 캡처 (WebView2 브라우저 기본 동작 방지 및 호스트 전달)
    window.addEventListener('keydown', function(e) {
        const isRocketKey = (e.key === 'F5' || e.key === 'F8' || e.key === 'F2' || e.key === 'F3' || e.key === 'Pause') ||
                            (e.ctrlKey && ['s', 'S', 'l', 'L', 'p', 'P', 'r', 'R', 'q', 'Q', 'm', 'M', 'v', 'V', '0', 'ArrowUp', 'ArrowDown'].indexOf(e.key) !== -1);
        if (isRocketKey) {
            e.preventDefault();
            e.stopPropagation();
        }

        // 길게 누를 때 오는 반복 입력은 보내지 않습니다 (켜기/끄기가 계속 뒤집히던 문제)
        if (!e.repeat && window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({
                type: 'hotkey',
                key: e.key,
                code: e.code,
                ctrl: e.ctrlKey,
                alt: e.altKey,
                shift: e.shiftKey
            });
        }
    }, true);

    // 4. 마우스 위치 추적
    window.addEventListener('mousemove', function(e) {
        bridge.mousePos.x = e.clientX;
        bridge.mousePos.y = e.clientY;
    }, true);

    // 5. SceneManager 훅 (배속 및 일시정지)
    function hookSceneManager() {
        if (!window.SceneManager) return false;
        if (typeof SceneManager.determineRepeatNumber !== 'function' && typeof SceneManager.updateMain !== 'function') return false;
        if (window.SceneManager.__rocketHooked) return true;
        window.SceneManager.__rocketHooked = true;

        if (typeof SceneManager.determineRepeatNumber === 'function') {
            // MZ 스타일
            const origRepeat = SceneManager.determineRepeatNumber;
            let halfStep = false;
            SceneManager.determineRepeatNumber = function(deltaTime) {
                if (bridge.paused) return 0;
                if (bridge.speed === 0.5) {
                    halfStep = !halfStep;
                    return halfStep ? 1 : 0;
                }
                const base = origRepeat.call(this, deltaTime);
                return Math.max(1, Math.round(base * bridge.speed));
            };
        } else if (typeof SceneManager.updateMain === 'function') {
            // MV 스타일
            const origUpdateMain = SceneManager.updateMain;
            let halfStepMV = false;
            SceneManager.updateMain = function() {
                if (bridge.paused) {
                    this.renderScene();
                    this.requestUpdate();
                    return;
                }
                if (bridge.speed === 0.5) {
                    halfStepMV = !halfStepMV;
                    if (!halfStepMV) {
                        this.renderScene();
                        this.requestUpdate();
                        return;
                    }
                }
                const spd = bridge.speed || 1.0;
                if (spd > 1.0) {
                    const extra = Math.min(8, Math.round(spd)) - 1;
                    for (let i = 0; i < extra; i++) {
                        if (this._scene && !this.isSceneChanging() && typeof this.updateScene === 'function') {
                            this.updateInputData();
                            this.changeScene();
                            this.updateScene();
                        }
                    }
                }
                origUpdateMain.call(this);
            };
        }
        return true;
    }

    const initInterval = setInterval(function() {
        const a = hookSceneManager();
        const b = hookFont();
        const c = hookMessage();
        const d = hookErrors();
        if (a && b && c && d) {
            clearInterval(initInterval);
        }
    }, 100);

    // 6. 텔레메트리 전송 루프 (~60ms)
    setInterval(function() {
        try {
            enforceFrozen();

            // GameState
            const sceneName = (typeof SceneManager !== 'undefined' && SceneManager._scene)
                ? SceneManager._scene.constructor.name : '';
            const mapId = (typeof $gameMap !== 'undefined' && $gameMap && $gameMap.mapId)
                ? $gameMap.mapId() : 0;
            const px = (typeof $gamePlayer !== 'undefined' && $gamePlayer) ? $gamePlayer.x : 0;
            const py = (typeof $gamePlayer !== 'undefined' && $gamePlayer) ? $gamePlayer.y : 0;
            const dx = (typeof $gameMap !== 'undefined' && $gameMap && $gameMap.displayX)
                ? $gameMap.displayX() : 0;
            const dy = (typeof $gameMap !== 'undefined' && $gameMap && $gameMap.displayY)
                ? $gameMap.displayY() : 0;
            const noclip = (typeof $gamePlayer !== 'undefined' && $gamePlayer)
                ? !!$gamePlayer._through : false;

            const stateKey = sceneName + '|' + mapId + '|' + px + '|' + py + '|' + dx + '|' + dy + '|' + noclip;
            if (stateKey !== bridge.lastStateKey) {
                bridge.lastStateKey = stateKey;
                sendToHost('gameState', {
                    scene: sceneName,
                    mapId: mapId,
                    playerX: px,
                    playerY: py,
                    displayX: dx,
                    displayY: dy,
                    noclip: noclip
                });
            }

            const canvas = (typeof Graphics !== 'undefined' && Graphics._canvas)
                ? Graphics._canvas : document.querySelector('canvas');

            // ESP는 drawEsp()가 requestAnimationFrame으로 페이지 안에서 직접 그림
            applyBrightness();
            applyFilter();
            pollMessageState();
            pollChoiceState();
            watchContextLoss();
            hookInputAuto();

            // 타일 인스펙터 데이터 수집 및 인게임 DOM 렌더링
            if (bridge.tileInspectorEnabled && canvas && typeof $gameMap !== 'undefined' && $gameMap && $gameMap.canvasToMapX && window.$dataMap) {
                ensureOverlay();
                const rect = canvas.getBoundingClientRect();
                const mx = bridge.mousePos.x;
                const my = bridge.mousePos.y;
                if (mx >= rect.left && mx <= rect.right && my >= rect.top && my <= rect.bottom) {
                    const gw = (typeof Graphics !== 'undefined' && Graphics.width) ? Graphics.width : (canvas.width || 816);
                    const gh = (typeof Graphics !== 'undefined' && Graphics.height) ? Graphics.height : (canvas.height || 624);
                    const canvasX = (typeof Graphics !== 'undefined' && Graphics.pageToCanvasX)
                        ? Graphics.pageToCanvasX(mx)
                        : (mx - rect.left) * (gw / rect.width);
                    const canvasY = (typeof Graphics !== 'undefined' && Graphics.pageToCanvasY)
                        ? Graphics.pageToCanvasY(my)
                        : (my - rect.top) * (gh / rect.height);
                    const mapX = $gameMap.canvasToMapX(canvasX);
                    const mapY = $gameMap.canvasToMapY(canvasY);

                    let passable = false;
                    const mapW = ($gameMap && typeof $gameMap.width === 'function') ? $gameMap.width() : 0;
                    const mapH = ($gameMap && typeof $gameMap.height === 'function') ? $gameMap.height() : 0;
                    if (mapX >= 0 && mapY >= 0 && mapX < mapW && mapY < mapH) {
                        const canExit = [2, 4, 6, 8].some(function(d) { return $gameMap.isPassable(mapX, mapY, d); });
                        const canEnter = [2, 4, 6, 8].some(function(d) {
                            const rev = 10 - d;
                            const nx = (d === 6) ? mapX - 1 : (d === 4) ? mapX + 1 : mapX;
                            const ny = (d === 2) ? mapY - 1 : (d === 8) ? mapY + 1 : mapY;
                            if (nx < 0 || ny < 0 || nx >= mapW || ny >= mapH) return false;
                            return $gameMap.isPassable(nx, ny, d) && $gameMap.isPassable(mapX, mapY, rev);
                        });
                        let eventBlocked = false;
                        if ($gameMap.eventsXy) {
                            const evList = $gameMap.eventsXy(mapX, mapY) || [];
                            eventBlocked = evList.some(function(e) {
                                return e && !e.isThrough() && typeof e.isNormalPriority === 'function' && e.isNormalPriority();
                            });
                        }
                        passable = (canExit || canEnter) && !eventBlocked;
                    }
                    const tileIds = $gameMap.allTiles ? $gameMap.allTiles(mapX, mapY) : [];
                    const evList = $gameMap.eventsXy ? $gameMap.eventsXy(mapX, mapY) : [];
                    const evNames = [];
                    for (let j = 0; j < evList.length; j++) {
                        const e = evList[j];
                        if (e && e.event && e.event() && e.event().name) evNames.push(e.event().name);
                        else if (e) evNames.push('EV' + e.eventId());
                    }
                    const evStr = evNames.length > 0 ? evNames.join(', ') : '없음';

                    if (tileHudDiv) {
                        tileHudDiv.textContent = '[타일 (' + mapX + ', ' + mapY + ') | 통과: ' + (passable ? 'O' : 'X') + ' | 이벤트: ' + evStr + ']';
                        tileHudDiv.style.display = 'block';
                    }

                    sendToHost('tileInfo', {
                        mapX: mapX,
                        mapY: mapY,
                        passable: passable,
                        tileIds: tileIds,
                        events: evStr,
                        screenX: mx,
                        screenY: my
                    });
                } else if (tileHudDiv && tileHudDiv.style.display !== 'none') {
                    tileHudDiv.style.display = 'none';
                }
            } else if (tileHudDiv && tileHudDiv.style.display !== 'none') {
                tileHudDiv.style.display = 'none';
            }
        } catch (e) { }
    }, 60);

    function sendDataInspector() {
        if (typeof $dataSystem === 'undefined' || !$dataSystem) return;
        const switches = [];
        if ($dataSystem.switches && typeof $gameSwitches !== 'undefined') {
            for (let i = 1; i < $dataSystem.switches.length; i++) {
                switches.push({
                    id: i,
                    name: $dataSystem.switches[i] || '',
                    val: !!$gameSwitches.value(i),
                    frozen: Object.prototype.hasOwnProperty.call(bridge.frozenSwitches, i)
                });
            }
        }
        const variables = [];
        if ($dataSystem.variables && typeof $gameVariables !== 'undefined') {
            for (let i = 1; i < $dataSystem.variables.length; i++) {
                const v = $gameVariables.value(i);
                variables.push({
                    id: i,
                    name: $dataSystem.variables[i] || '',
                    val: (v !== undefined && v !== null) ? String(v) : '0',
                    frozen: Object.prototype.hasOwnProperty.call(bridge.frozenVariables, i)
                });
            }
        }
        sendToHost('dataInspectorData', { switches: switches, variables: variables });
    }
})();
