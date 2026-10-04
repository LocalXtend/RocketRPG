// RocketRPG 멀티 엑스트라 모드 (RPG Maker MV/MZ): 참가자마다 방장 캐릭터와 같은 모습의 캐릭터를 하나씩 맵에 둡니다.
//  - 참가자 키(방향키/숫자판 2468, Shift 달리기, Z/Enter/Space 말 걸기)는 방장 RocketRPG가 이 페이지로 넘겨 줍니다.
//  - 참가자 캐릭터는 방장 화면(카메라) 밖으로 나갈 수 없습니다. 방장이 움직여 화면 밖으로 밀리면 방장 곁으로 옮깁니다.
//  - 참가자도 앞의 이벤트에 말을 걸거나 밟아 이벤트를 시작할 수 있습니다 (이벤트 안의 '플레이어'는 방장 캐릭터).
//  - 참가자 캐릭터는 게임 데이터($gameMap 등)에 넣지 않습니다. 저장 파일에 섞이지 않고, RocketRPG 없이 불러와도 문제가 없습니다.
// 명령: chrome.webview 메시지 { type: 'extra', op: 'mode'|'guests'|'key'|'held', ... }
(function () {
    if (window.__rocketExtra) return;
    const ext = window.__rocketExtra = { on: false, guests: new Map(), installed: false, command: (m) => command(m) };
    const LEASE_MS = 1500;   // 참가자 키 소식이 이만큼 없으면 키를 모두 뗀 것으로

    // 윈도우 가상 키 → 방향(2 아래, 4 왼쪽, 6 오른쪽, 8 위) / 확인 / 달리기
    const DIR = { 0x25: 4, 0x26: 8, 0x27: 6, 0x28: 2, 0x62: 2, 0x64: 4, 0x66: 6, 0x68: 8 };
    const OK = new Set([0x0D, 0x20, 0x5A]);
    const DASH = new Set([0x10, 0xA0, 0xA1]);

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
                setKey(g, Number(m.k), !!m.d);
                if (m.d && OK.has(Number(m.k)) && g.char) g.char.rrAction();
                break;
            }
            case 'held': {
                const g = ext.guests.get(String(m.id));
                if (!g) return;
                g.at = performance.now();
                const keys = new Set((m.keys || []).map(Number));
                g.order = g.order.filter(d => [...keys].some(k => DIR[k] === d));
                for (const k of keys) if (DIR[k] && !g.order.includes(DIR[k])) g.order.push(DIR[k]);
                g.dash = [...keys].some(k => DASH.has(k));
                break;
            }
        }
    }

    function setKey(g, vk, down) {
        const d = DIR[vk];
        if (d) {
            g.order = g.order.filter(x => x !== d);
            if (down) g.order.push(d);   // 나중에 누른 방향이 먼저
        }
        if (DASH.has(vk)) g.dash = down;
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

        Game_RocketGuest.prototype.rrCanAct = function () {
            return ext.on && SceneManager._scene instanceof Scene_Map && !$gameMap.isEventRunning() && !$gameMessage.isBusy() &&
                !$gamePlayer.isTransferring() && !(SceneManager.isSceneChanging && SceneManager.isSceneChanging());
        };

        Game_RocketGuest.prototype.update = function () {
            const g = this._rrGuest;
            // 모습은 방장 캐릭터와 같게 (방장이 바뀌면 따라 바뀜)
            if (this.characterName() !== $gamePlayer.characterName() || this.characterIndex() !== $gamePlayer.characterIndex())
                this.setImage($gamePlayer.characterName(), $gamePlayer.characterIndex());
            this.setTransparent($gamePlayer.isTransparent());
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
            this.setMoveSpeed(g.dash && this.rrCanAct() ? 5 : 4);
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
                for (const g of ext.guests.values()) {
                    if (!g.char) g.char = new Game_RocketGuest(g);
                    g.char.update();
                }
            }
            rrSyncSprites(scene._spriteset);
        }
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
                set._characterSprites.push(s);
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
                label.visible = !g.char.isTransparent();
            }
        }
    }
})();
