// RocketRPG 멀티 엑스트라 모드 (RPG Maker MV/MZ): 참가자마다 방장 캐릭터와 같은 모습의 캐릭터를 하나씩 맵에 둡니다.
//  - 참가자 키는 방장 RocketRPG가 이 페이지로 넘겨 줍니다. 키 배치는 방장 게임과 같습니다 (게임의 Input.keyMapper:
//    방향 = 움직이기, 'shift' = 달리기, 'ok' = 말 걸기. 플러그인이 바꾼 키 배치도 따라감).
//  - 대화가 떠 있으면 'ok' 키로 방장처럼 대사를 넘깁니다 (선택지는 방장이 고름, RocketRPG 브릿지의 guestConfirm).
//  - 참가자 캐릭터는 방장 화면(카메라) 밖으로 나갈 수 없습니다. 방장이 움직여 화면 밖으로 밀리면 방장 곁으로 옮깁니다.
//  - 참가자도 앞의 이벤트에 말을 걸거나 밟아 이벤트를 시작할 수 있습니다 (이벤트 안의 '플레이어'는 방장 캐릭터).
//  - 참가자 캐릭터는 게임 데이터($gameMap 등)에 넣지 않습니다. 저장 파일에 섞이지 않고, RocketRPG 없이 불러와도 문제가 없습니다.
//  - 방장이 움직일 수 없을 때(이벤트, 메시지, 이동 경로 강제, 탈것 승하차 등)는 참가자도 움직일 수 없습니다.
//  - 방장 캐릭터가 숨겨져 있으면(타이틀 맵 등) 참가자 캐릭터와 이름표도 숨깁니다.
//  - 달리기는 방장이 달릴 수 있을 때만 (맵의 달리기 금지, 게임이 막은 달리기 포함). 기본 속도는 방장 속도를 따릅니다.
//  - 방장이 순간이동하면(다른 맵이든 같은 맵이든) 함께 옮겨집니다. 방장은 모두를 곁으로 부를 수 있습니다(summon).
// 명령: chrome.webview 메시지 { type: 'extra', op: 'mode'|'guests'|'key'|'held'|'summon', ... }
(function () {
    if (window.__rocketExtra) return;
    const ext = window.__rocketExtra = { on: false, guests: new Map(), installed: false, command: (m) => command(m) };
    const LEASE_MS = 1500;   // 참가자 키 소식이 이만큼 없으면 키를 모두 뗀 것으로

    // 윈도우 가상 키 → 게임 버튼 이름: 게임의 Input.keyMapper (없으면 MV/MZ 기본 배치)
    const DEFAULT_KEYS = { 13: 'ok', 16: 'shift', 32: 'ok', 37: 'left', 38: 'up', 39: 'right', 40: 'down', 90: 'ok', 98: 'down', 100: 'left', 102: 'right', 104: 'up' };
    const DIR_OF = { down: 2, left: 4, right: 6, up: 8 };
    function button(vk) {
        if (vk === 0xA0 || vk === 0xA1) vk = 0x10;
        const m = (typeof Input !== 'undefined' && Input.keyMapper) ? Input.keyMapper : DEFAULT_KEYS;
        return m[vk];
    }
    const dirOf = vk => DIR_OF[button(vk)];
    const isOk = vk => button(vk) === 'ok';
    const isDash = vk => button(vk) === 'shift';
    ext.keys = { dirOf, isOk, isDash };   // 자동 시험용

    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', function (event) {
            const m = event.data;
            if (!m || m.type !== 'extra') return;
            try { command(m); } catch (e) { console.error('[RocketExtra]', e); }
        });
    }

    function command(m) {
        switch (m.op) {
            case 'mode':
                ext.on = !!m.on;
                if (!ext.on) ext.guests.clear();
                break;
            case 'guests': {
                const want = new Map((m.list || []).map(g => [String(g.id), g]));
                for (const id of [...ext.guests.keys()]) if (!want.has(id)) ext.guests.delete(id);
                for (const [id, g] of want) {
                    const cur = ext.guests.get(id);
                    if (cur) { cur.name = String(g.name || ''); cur.color = String(g.color || '#ffffff'); continue; }
                    ext.guests.set(id, { id, name: String(g.name || ''), color: String(g.color || '#ffffff'), char: null, order: [], dash: false, at: performance.now() });
                }
                break;
            }
            case 'key': {
                const g = ext.guests.get(String(m.id));
                if (!g) return;
                g.at = performance.now();
                const vk = Number(m.k);
                setKey(g, vk, !!m.d);
                if (!m.d || !isOk(vk)) break;
                // 대화 중: 방장처럼 대사 넘김 / 아니면 앞의 이벤트에 말 걸기
                if (typeof $gameMessage !== 'undefined' && $gameMessage && $gameMessage.isBusy()) {
                    if (window.__rocketBridge && window.__rocketBridge.guestConfirm) window.__rocketBridge.guestConfirm();
                } else if (g.char) g.char.rrAction();
                break;
            }
            case 'summon':   // 방장: 모두 내 곁으로
                ext.summon = true;
                break;
            case 'held': {
                const g = ext.guests.get(String(m.id));
                if (!g) return;
                g.at = performance.now();
                const keys = new Set((m.keys || []).map(Number));
                const dirs = [...keys].map(dirOf).filter(Boolean);
                g.order = g.order.filter(d => dirs.includes(d));
                for (const d of dirs) if (!g.order.includes(d)) g.order.push(d);
                g.dash = [...keys].some(isDash);
                break;
            }
        }
    }

    function setKey(g, vk, down) {
        const d = dirOf(vk);
        if (d) {
            g.order = g.order.filter(x => x !== d);
            if (down) g.order.push(d);   // 나중에 누른 방향이 먼저
        }
        if (isDash(vk)) g.dash = down;
    }

    // 게임 스크립트(rpg_objects/rmmz_objects)가 다 읽힌 뒤 설치
    (function wait() {
        if (window.Game_Character && window.Spriteset_Map && window.Sprite_Character && window.Scene_Map && window.Bitmap) install();
        else setTimeout(wait, 50);
    })();

    function install() {
        if (ext.installed) return;
        ext.installed = true;

        function Game_RocketGuest() { this.initialize.apply(this, arguments); }
        Game_RocketGuest.prototype = Object.create(Game_Character.prototype);
        Game_RocketGuest.prototype.constructor = Game_RocketGuest;
        window.Game_RocketGuest = Game_RocketGuest;

        Game_RocketGuest.prototype.initialize = function (guest) {
            Game_Character.prototype.initialize.call(this);
            this._rrGuest = guest;
            this._rrMapId = 0;
            this._rrWasMoving = false;
            this.setThrough(false);
        };

        // 방장 화면 안인지 (맵이 이어지는 경우도 맞게)
        Game_RocketGuest.prototype.rrInView = function (x, y) {
            const sx = $gameMap.adjustX(x), sy = $gameMap.adjustY(y);
            return sx >= 0 && sy >= 0 && sx <= $gameMap.screenTileX() - 1 && sy <= $gameMap.screenTileY() - 1;
        };

        // 방장이 지금 움직일 수 있을 때만 (게임의 판정 canMove: 이벤트·메시지·이동 경로 강제·탈것 승하차, 플러그인이 바꾼 것 포함)
        Game_RocketGuest.prototype.rrCanAct = function () {
            if (!ext.on || !(SceneManager._scene instanceof Scene_Map) || $gameMap.isEventRunning() || $gameMessage.isBusy()) return false;
            if ($gamePlayer.isTransferring() || (SceneManager.isSceneChanging && SceneManager.isSceneChanging())) return false;
            try { if ($gamePlayer.canMove && !$gamePlayer.canMove()) return false; } catch (e) { return false; }
            return hostVisible();
        };

        // 방장 캐릭터가 보이는지 (그림 없음·투명이면 타이틀 맵이나 연출 중: 참가자도 숨김)
        function hostVisible() {
            return !!$gamePlayer && !$gamePlayer.isTransparent() && $gamePlayer.characterName() !== '';
        }

        // 방장이 지금 달릴 수 있는지: 맵의 달리기 금지·탈것, 그리고 게임이 막은 달리기(isDashButtonPressed를 바꾼 플러그인)를
        // Shift를 누른 것처럼 물어봐서 확인합니다. 0.5초마다 한 번만.
        let dashAt = -1e9, dashOk = false;
        function hostCanDash() {
            const now = performance.now();
            if (now - dashAt < 500) return dashOk;
            dashAt = now;
            dashOk = false;
            if ($gameMap.isDashDisabled && $gameMap.isDashDisabled()) return dashOk;
            if ($gamePlayer.isInVehicle && $gamePlayer.isInVehicle()) return dashOk;
            const press = Input.isPressed, always = ConfigManager.alwaysDash;
            try {
                Input.isPressed = function (k) { return k === 'shift' ? true : press.apply(this, arguments); };
                ConfigManager.alwaysDash = false;
                dashOk = !!$gamePlayer.isDashButtonPressed();
            } catch (e) { dashOk = false; }
            finally { Input.isPressed = press; ConfigManager.alwaysDash = always; }
            return dashOk;
        }

        Game_RocketGuest.prototype.update = function () {
            const g = this._rrGuest;
            // 모습은 방장 캐릭터와 같게 (방장이 바뀌면 따라 바뀜)
            if (this.characterName() !== $gamePlayer.characterName() || this.characterIndex() !== $gamePlayer.characterIndex())
                this.setImage($gamePlayer.characterName(), $gamePlayer.characterIndex());
            this.setTransparent(!hostVisible());
            // 맵이 바뀌면 방장 곁으로
            if (this._rrMapId !== $gameMap.mapId()) { this._rrMapId = $gameMap.mapId(); this.locate($gamePlayer.x, $gamePlayer.y); this.setDirection($gamePlayer.direction()); }
            if (performance.now() - g.at > LEASE_MS) { g.order = []; g.dash = false; }
            if (!this.isMoving()) {
                if (this._rrWasMoving) this.rrStartHere();
                // 방장이 움직여 화면 밖으로 밀려남 → 방장 자리로
                if (!this.rrInView(this.x, this.y)) this.locate($gamePlayer.x, $gamePlayer.y);
                else if (this.rrCanAct() && g.order.length) this.rrMove(g.order[g.order.length - 1]);
            }
            this._rrWasMoving = this.isMoving();
            // 방장 기본 속도 + (방장이 달릴 수 있고 참가자가 Shift를 누르면) 1
            this.setMoveSpeed($gamePlayer.moveSpeed() + (g.dash && this.rrCanAct() && hostCanDash() ? 1 : 0));
            Game_Character.prototype.update.call(this);
        };

        Game_RocketGuest.prototype.rrMove = function (d) {
            const x2 = $gameMap.roundXWithDirection(this.x, d), y2 = $gameMap.roundYWithDirection(this.y, d);
            if (!this.rrInView(x2, y2)) { this.setDirection(d); return; }   // 화면 밖으로는 못 감
            this.moveStraight(d);   // 막혀 있으면 방향만 바꾸고 앞의 이벤트를 건드림 (checkEventTriggerTouchFront)
        };

        function startable(ev, triggers, normal) {
            return ev && !ev._erased && ev.isTriggerIn(triggers) && ev.isNormalPriority() === normal && ev.page && ev.page() && ev.list && ev.list().length > 1;
        }
        // who: 말을 건 참가자 캐릭터 (이벤트가 방장 대신 그 캐릭터를 돌아봄)
        function startAt(x, y, triggers, normal, who) {
            if ($gameMap.isEventRunning()) return false;
            let started = false;
            for (const ev of $gameMap.eventsXy(x, y)) {
                if (!startable(ev, triggers, normal)) continue;
                ev.start();
                if (who && ev._locked && ev.turnTowardCharacter) ev.turnTowardCharacter(who);
                started = true;
            }
            return started;
        }

        // 막혀서 못 간 앞자리의 이벤트 (닿으면 시작하는 이벤트)
        Game_RocketGuest.prototype.checkEventTriggerTouchFront = function (d) {
            if (!this.rrCanAct()) return;
            startAt($gameMap.roundXWithDirection(this.x, d), $gameMap.roundYWithDirection(this.y, d), [1, 2], true, this);
        };

        // 걸어 들어간 자리 (캐릭터 아래/위에 있는 이벤트)
        Game_RocketGuest.prototype.rrStartHere = function () {
            if (this.rrCanAct()) startAt(this.x, this.y, [1, 2], false, this);
        };

        // 확인 키: 선 자리 → 앞 (카운터 너머까지)
        Game_RocketGuest.prototype.rrAction = function () {
            if (!this.rrCanAct() || this.isMoving()) return;
            if (startAt(this.x, this.y, [0], false, this)) return;
            const d = this.direction();
            let x2 = $gameMap.roundXWithDirection(this.x, d), y2 = $gameMap.roundYWithDirection(this.y, d);
            if (startAt(x2, y2, [0, 1, 2], true, this)) return;
            if ($gameMap.isCounter && $gameMap.isCounter(x2, y2)) {
                x2 = $gameMap.roundXWithDirection(x2, d); y2 = $gameMap.roundYWithDirection(y2, d);
                startAt(x2, y2, [0, 1, 2], true, this);
            }
        };

        // 참가자 캐릭터 갱신과 그림: 장면 갱신(SceneManager.updateScene)이 끝날 때마다 (방장 캐릭터가 움직인 뒤).
        // Scene_Map.updateMain / Spriteset_Map.update에 끼우면 그 함수를 통째로 바꾸는 플러그인(VisuMZ 등)이 있어 빠졌습니다.
        // updateScene도 다른 플러그인이 바꿀 수 있으므로 0.5초마다 우리 것이 빠졌으면 다시 끼웁니다.
        function updateGuests(scene) {
            if (!(scene instanceof Scene_Map) || !scene._spriteset) return;
            const started = scene.isStarted ? scene.isStarted() : SceneManager._sceneStarted;
            if (ext.on && started && window.$dataMap && $gameMap && $gameMap.mapId() > 0) {
                const jump = hostTeleported() || ext.summon;
                ext.summon = false;
                for (const g of ext.guests.values()) {
                    if (!g.char) g.char = new Game_RocketGuest(g);
                    if (jump) g.char.rrToHost();
                    g.char.update();
                }
            }
            rrSyncSprites(scene._spriteset);
        }
        // 방장이 순간이동했는지: 다른 맵이거나, 점프가 아닌데 한 번에 2칸 넘게 움직임 (같은 맵 안의 장소 이동)
        let last = null;
        function hostTeleported() {
            const p = $gamePlayer, now = { map: $gameMap.mapId(), x: p.x, y: p.y };
            const prev = last;
            last = now;
            if (!prev) return false;
            if (prev.map !== now.map) return true;
            if (p.isJumping && p.isJumping()) return false;
            const dx = $gameMap.deltaX ? Math.abs($gameMap.deltaX(now.x, prev.x)) : Math.abs(now.x - prev.x);
            const dy = $gameMap.deltaY ? Math.abs($gameMap.deltaY(now.y, prev.y)) : Math.abs(now.y - prev.y);
            return dx + dy > 1;
        }

        // 방장 자리로 (순간이동을 따라가거나 방장이 불렀을 때)
        Game_RocketGuest.prototype.rrToHost = function () {
            this.locate($gamePlayer.x, $gamePlayer.y);
            this.setDirection($gamePlayer.direction());
            this._rrMapId = $gameMap.mapId();
        };

        function chainHas(fn) {
            for (let i = 0; fn && i < 40; i++) { if (fn.__rrOwner === 'extra') return true; fn = fn.__rrWraps; }
            return false;
        }
        function hookUpdateScene() {
            const cur = SceneManager.updateScene;
            if (typeof cur !== 'function' || chainHas(cur)) return;
            const w = function () {
                const r = cur.apply(this, arguments);
                try { if (SceneManager._scene) updateGuests(SceneManager._scene); }
                catch (e) { console.error('[RocketExtra] update', e); }
                return r;
            };
            w.__rrOwner = 'extra';
            w.__rrWraps = cur;
            SceneManager.updateScene = w;
        }
        hookUpdateScene();
        setInterval(hookUpdateScene, 500);
        ext.debug = () => ({ hooked: chainHas(SceneManager.updateScene) });
        ext.hostCanDash = hostCanDash;

        function rrSyncSprites(set) {
            if (!set._tilemap || !set._characterSprites) return;
            const sprites = set._rrGuestSprites || (set._rrGuestSprites = new Map());
            const live = ext.on ? ext.guests : new Map();
            for (const [id, s] of sprites) {
                const g = live.get(id);
                if (g && g.char === s._character) continue;
                set._tilemap.removeChild(s);
                const i = set._characterSprites.indexOf(s);
                if (i >= 0) set._characterSprites.splice(i, 1);
                sprites.delete(id);
            }
            for (const [id, g] of live) {
                if (!g.char || sprites.has(id)) continue;
                const s = new Sprite_Character(g.char);
                s._rrLabel = new Sprite(new Bitmap(160, 26));
                s._rrLabel.anchor.x = 0.5;
                s._rrLabel.anchor.y = 1;
                s.addChild(s._rrLabel);
                // 방장 캐릭터 그림 바로 앞에 (방장 그림이 맨 끝이라고 여기는 플러그인이 있음)
                const pi = set._characterSprites.findIndex(x => x._character === $gamePlayer);
                if (pi >= 0) set._characterSprites.splice(pi, 0, s); else set._characterSprites.unshift(s);
                set._tilemap.addChild(s);
                sprites.set(id, s);
            }
            for (const [id, s] of sprites) {
                const g = live.get(id), label = s._rrLabel;
                const text = g.name;
                if (label._rrText !== text || label._rrColor !== g.color) {
                    label._rrText = text;
                    label._rrColor = g.color;
                    label.bitmap.clear();
                    label.bitmap.fontSize = 16;
                    label.bitmap.textColor = g.color;
                    label.bitmap.outlineColor = 'rgba(0,0,0,0.85)';
                    label.bitmap.outlineWidth = 4;
                    label.bitmap.drawText(text, 0, 0, 160, 26, 'center');
                }
                const h = s.bitmap && s.bitmap.height && s.patternHeight ? s.patternHeight() : 48;   // 그림을 아직 못 읽었으면 기본 높이
                label.y = -(h > 0 ? h : 48) - 2;
                label.visible = !g.char.isTransparent() && hostVisible();
            }
        }
    }
})();
