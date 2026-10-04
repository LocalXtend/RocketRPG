// 방 하나 (Durable Object). 참가자 WebSocket을 받아 인원·설정을 관리하고 메시지를 중계합니다.
// WebSocket Hibernation을 써서 조용할 때는 메모리에서 내려가도 연결이 유지됩니다.
// 상태는 저장소("info")와 각 소켓의 첨부(attachment)에만 두고, 메모리 값은 캐시일 뿐입니다.
import { DurableObject } from "cloudflare:workers";
import type { Env } from "./lobby";
import {
  DEFAULT_SETTINGS, FIXED_CHAT_MS, HOST_GRACE_MS, LOBBY_REFRESH_MS, MAX_CHAT, MAX_MESSAGE_BYTES, MAX_NAME, MAX_PLAYERS, MAX_TITLE,
  MIN_PLAYERS, PROTOCOL, RoomSettings, RoomSummary, clampInt, cleanText, hashPassword, json, randomHex,
} from "./protocol";

interface RoomInfo {
  id: string;
  code: string;
  pwSalt: string;
  pwHash: string;          // "" = 비밀번호 없음
  hostKey: string;         // 방장 증명 (방장에게만 알려 줌, 넘기면 새로 만듦)
  settings: RoomSettings;
  game: string;
  hostAwayUntil: number;   // 0 = 방장 연결됨
  bans: string[];          // clientId
  muted: string[];         // clientId
  colors?: Record<string, number>;   // clientId → 핑·마커 색 번호 (다시 들어와도 같은 색)
  fixedAt?: Record<string, number>;  // clientId → 마지막 고정 채팅 시각 (나갔다 들어와도 90초 유지)
  createdAt: number;
  closed: boolean;
}

interface Member {
  pending: boolean;        // hello 전
  id: string;              // 방 안에서 보이는 id (clientId는 숨김)
  clientId: string;
  name: string;
  host: boolean;
  joined: number;
  color: number;
}

type Msg = Record<string, unknown> & { t?: string };

export class Room extends DurableObject<Env> {
  info: RoomInfo | null = null;
  lastChat = new Map<string, number>();
  lastPing = new Map<string, number>();

  async load(): Promise<RoomInfo | null> {
    if (!this.info) this.info = (await this.ctx.storage.get<RoomInfo>("info")) ?? null;
    return this.info;
  }

  async save() {
    if (this.info) await this.ctx.storage.put("info", this.info);
  }

  /** 직접 연결용 STUN만 제공합니다. 미디어 중계는 사용하지 않습니다. */
  async iceServers(): Promise<unknown[]> {
    return [{ urls: "stun:stun.cloudflare.com:3478" }, { urls: "stun:stun.l.google.com:19302" }];
  }

  // ── HTTP (Worker가 부름) ──

  async fetch(req: Request): Promise<Response> {
    const url = new URL(req.url);
    if (req.method === "POST" && url.pathname === "/init") return this.init(await req.json());
    if (url.pathname === "/ws") {
      if (req.headers.get("Upgrade") !== "websocket") return json({ error: "expected_websocket" }, 426);
      const info = await this.load();
      if (!info || info.closed) return json({ error: "no_room" }, 404);
      const pair = new WebSocketPair();
      const [client, server] = [pair[0], pair[1]];
      this.ctx.acceptWebSocket(server);
      server.serializeAttachment({ pending: true } as Partial<Member>);
      return new Response(null, { status: 101, webSocket: client });
    }
    return json({ error: "not_found" }, 404);
  }

  async init(body: { id: string; code: string; title: string; password: string; max: number; listed: boolean }): Promise<Response> {
    if (await this.load()) return json({ error: "exists" }, 409);
    const pwSalt = randomHex(8);
    const password = typeof body.password === "string" ? body.password : "";
    this.info = {
      id: body.id,
      code: body.code,
      pwSalt,
      pwHash: password ? await hashPassword(password, pwSalt) : "",
      hostKey: randomHex(24),
      settings: {
        ...DEFAULT_SETTINGS,
        title: cleanText(body.title, MAX_TITLE) || "RocketRPG 방",
        max: clampInt(body.max, MIN_PLAYERS, MAX_PLAYERS, DEFAULT_SETTINGS.max),
        listed: body.listed !== false,
      },
      game: "",
      // 방장이 아직 접속 전: 이 시간 안에 들어오지 않으면 해체
      hostAwayUntil: Date.now() + HOST_GRACE_MS,
      bans: [],
      muted: [],
      createdAt: Date.now(),
      closed: false,
    };
    await this.save();
    await this.ctx.storage.setAlarm(this.info.hostAwayUntil);
    await this.updateLobby();
    return json({ hostKey: this.info.hostKey });
  }

  // ── WebSocket ──

  async webSocketMessage(ws: WebSocket, raw: string | ArrayBuffer) {
    if (typeof raw !== "string" || raw.length > MAX_MESSAGE_BYTES) return;
    let msg: Msg;
    try { msg = JSON.parse(raw); } catch { return; }
    const info = await this.load();
    if (!info || info.closed) { this.send(ws, { t: "closed", reason: "dissolved" }); ws.close(1000, "closed"); return; }
    const me = ws.deserializeAttachment() as Member;
    if (me.pending) { await this.hello(ws, info, msg); return; }

    switch (msg.t) {
      case "rename": {
        const name = cleanText(msg.name, MAX_NAME);
        if (!name) return;
        me.name = name;
        ws.serializeAttachment(me);
        this.broadcastMembers();
        return;
      }
      case "chat": {
        if (info.muted.includes(me.clientId)) { this.error(ws, "muted", "채팅이 금지되었습니다."); return; }
        const text = cleanText(msg.text, MAX_CHAT);
        if (!text) return;
        const now = Date.now();
        if (now - (this.lastChat.get(me.id) ?? 0) < 700) { this.error(ws, "slow", "채팅을 너무 빨리 보냈습니다."); return; }
        const fixed = msg.fixed === true;
        if (fixed) {
          const wait = FIXED_CHAT_MS - (now - (info.fixedAt?.[me.clientId] ?? 0));
          if (wait > 0) { this.error(ws, "slow", `고정 채팅은 ${Math.ceil(wait / 1000)}초 뒤에 보낼 수 있습니다.`); return; }
          (info.fixedAt ??= {})[me.clientId] = now;
          await this.save();
        }
        const color = clampInt(msg.color, 0, 4, 0);
        this.lastChat.set(me.id, now);
        this.broadcast({ t: "chat", from: me.id, name: me.name, text, color, fixed, ts: now });
        return;
      }
      case "ping": {
        if (info.muted.includes(me.clientId)) return;
        const now = Date.now();
        if (now - (this.lastPing.get(me.id) ?? 0) < 1000) return;
        this.lastPing.set(me.id, now);
        // 위치(게임 화면 비율)와 찍을 때의 맵·카메라(게임 픽셀)만 전달
        const num = (v: unknown, lo: number, hi: number) => {
          const n = typeof v === "number" && Number.isFinite(v) ? v : 0;
          return Math.min(hi, Math.max(lo, n));
        };
        this.broadcast({
          t: "ping", from: me.id, ts: now,
          x: num(msg.x, 0, 1), y: num(msg.y, 0, 1),
          map: clampInt(msg.map, 0, 1_000_000, 0), cx: num(msg.cx, -1e7, 1e7), cy: num(msg.cy, -1e7, 1e7),
        });
        return;
      }
      case "signal": {
        // WebRTC 연결 정보: 지정한 한 사람에게만
        const target = this.findMember(String(msg.to ?? ""));
        if (target && target.m.host !== me.host) this.send(target.ws, { t: "signal", from: me.id, data: msg.data });
        return;
      }
      case "relay": {
        // 그 밖의 방 안 메시지 (노트, 카메라 등). ch = "input"은 조종 권한이 있을 때 방장에게만.
        const ch = String(msg.ch ?? "");
        if (ch === "input") {
          if (!info.settings.control || me.host) return;
          const host = this.members().find(m => m.m.host);
          if (host) this.send(host.ws, { t: "relay", ch, from: me.id, data: msg.data });
          return;
        }
        const to = msg.to ? this.findMember(String(msg.to)) : null;
        const out = { t: "relay", ch, from: me.id, data: msg.data };
        if (to) this.send(to.ws, out);
        else this.broadcast(out, ws);
        return;
      }
      case "leave":
        if (!me.host) ws.close(1000, "leave");
        return;
    }

    // ── 방장만 ──
    if (!me.host) { this.error(ws, "not_host", "방장만 할 수 있습니다."); return; }
    switch (msg.t) {
      case "settings": await this.applySettings(info, msg); return;
      case "game": {
        info.game = cleanText(msg.title, 80);
        await this.save();
        this.broadcastRoom();
        await this.updateLobby();
        return;
      }
      case "kick": {
        const target = this.findMember(String(msg.id ?? ""));
        if (!target || target.m.host) return;
        if (!info.bans.includes(target.m.clientId)) info.bans.push(target.m.clientId);
        await this.save();
        this.send(target.ws, { t: "kicked" });
        target.ws.close(4001, "kicked");
        return;
      }
      case "mute": {
        const target = this.findMember(String(msg.id ?? ""));
        if (!target || target.m.host) return;
        const on = msg.on !== false;
        info.muted = info.muted.filter(c => c !== target.m.clientId);
        if (on) info.muted.push(target.m.clientId);
        await this.save();
        this.broadcastMembers();
        return;
      }
      case "transfer": {
        const target = this.findMember(String(msg.id ?? ""));
        if (!target || target.m.host) return;
        me.host = false;
        ws.serializeAttachment(me);
        target.m.host = true;
        target.ws.serializeAttachment(target.m);
        info.hostKey = randomHex(24);
        info.game = "";
        await this.save();
        this.send(target.ws, { t: "host_key", hostKey: info.hostKey });
        this.broadcastMembers();
        this.broadcastRoom();
        await this.updateLobby();
        return;
      }
      case "dissolve": await this.dissolve("dissolved"); return;
    }
  }

  async hello(ws: WebSocket, info: RoomInfo, msg: Msg) {
    if (msg.t !== "hello") return;
    if (msg.proto !== PROTOCOL) {
      this.error(ws, "version", "RocketRPG 버전이 달라 입장할 수 없습니다. 최신 버전으로 업데이트해 주세요.");
      ws.close(4000, "version");
      return;
    }
    const clientId = cleanText(msg.clientId, 64);
    const name = cleanText(msg.name, MAX_NAME) || "user";
    const isHost = typeof msg.hostKey === "string" && msg.hostKey === info.hostKey;

    if (!isHost) {
      if (info.bans.includes(clientId)) { this.error(ws, "banned", "이 방에서 내보내져 다시 들어갈 수 없습니다."); ws.close(4001, "banned"); return; }
      if (info.pwHash && (await hashPassword(String(msg.password ?? ""), info.pwSalt)) !== info.pwHash) {
        this.error(ws, "password", "비밀번호가 맞지 않습니다.");
        ws.close(4002, "password");
        return;
      }
      if (this.members().length >= info.settings.max) { this.error(ws, "full", "방이 가득 찼습니다."); ws.close(4003, "full"); return; }
    } else {
      // 같은 방장이 다시 접속: 이전 연결은 정리
      for (const o of this.members()) if (o.m.host) { o.m.host = false; o.ws.serializeAttachment(o.m); o.ws.close(4004, "replaced"); }
    }

    // 핑·마커 색: 이 사람이 전에 쓰던 색이 비어 있으면 그대로, 아니면 아무도 안 쓰는 색
    const usedColors = new Set(this.members().map(x => x.m.color));
    const before = info.colors?.[clientId];
    const color = before !== undefined && !usedColors.has(before) ? before
      : Array.from({ length: MAX_PLAYERS }, (_, i) => i).find(i => !usedColors.has(i)) ?? 0;
    if (before !== color) {
      info.colors ??= {};
      info.colors[clientId] = color;
      const keys = Object.keys(info.colors);
      if (keys.length > 64) delete info.colors[keys[0]];   // 오래 열린 방에서 끝없이 늘지 않게
      await this.save();
    }
    const me: Member = { pending: false, id: randomHex(4), clientId, name, host: isHost, joined: Date.now(), color };
    ws.serializeAttachment(me);

    if (isHost && info.hostAwayUntil) {
      info.hostAwayUntil = 0;
      await this.save();
      await this.ctx.storage.setAlarm(Date.now() + LOBBY_REFRESH_MS);
      this.broadcast({ t: "host_back" }, ws);
    }

    this.send(ws, {
      t: "welcome", you: { id: me.id, host: me.host, color }, room: this.publicRoom(info), members: this.memberList(info),
      ice: await this.iceServers(),
      fixedWait: Math.max(0, FIXED_CHAT_MS - (Date.now() - (info.fixedAt?.[clientId] ?? 0))),
    });
    this.broadcastMembers(ws);
    await this.updateLobby();
  }

  async webSocketClose(ws: WebSocket) { await this.onGone(ws); }
  async webSocketError(ws: WebSocket) { await this.onGone(ws); }

  async onGone(ws: WebSocket) {
    const me = ws.deserializeAttachment() as Member | null;
    const info = await this.load();
    if (!me || me.pending || !info || info.closed) return;
    this.lastChat.delete(me.id);
    this.lastPing.delete(me.id);
    if (me.host) {
      // 방장이 끊김: 1분 기다렸다가 해체 (그 안에 돌아오면 복구)
      info.hostAwayUntil = Date.now() + HOST_GRACE_MS;
      await this.save();
      await this.ctx.storage.setAlarm(info.hostAwayUntil);
      this.broadcast({ t: "host_away", until: info.hostAwayUntil });
    }
    this.broadcastMembers();
    await this.updateLobby();
  }

  async alarm() {
    const info = await this.load();
    if (!info || info.closed) return;
    const now = Date.now();
    if (info.hostAwayUntil && now >= info.hostAwayUntil) { await this.dissolve("host_left"); return; }
    await this.updateLobby();
    await this.ctx.storage.setAlarm(Math.min(info.hostAwayUntil || Infinity, now + LOBBY_REFRESH_MS));
  }

  // ── 방 설정 ──

  async applySettings(info: RoomInfo, msg: Msg) {
    const s = info.settings;
    if (msg.title !== undefined) s.title = cleanText(msg.title, MAX_TITLE) || s.title;
    if (msg.max !== undefined) s.max = clampInt(msg.max, Math.max(MIN_PLAYERS, this.members().length), MAX_PLAYERS, s.max);
    if (msg.listed !== undefined) s.listed = msg.listed === true;
    if (msg.control !== undefined) s.control = msg.control === true;
    if (msg.notesEditable !== undefined) s.notesEditable = msg.notesEditable === true;
    if (msg.fps !== undefined) s.fps = msg.fps === 60 ? 60 : 30;
    if (msg.streamer !== undefined) s.streamer = msg.streamer === true;
    if (msg.quality !== undefined) s.quality = ["low", "normal", "high"].includes(String(msg.quality)) ? String(msg.quality) : s.quality;
    if (msg.password !== undefined) {
      const pw = typeof msg.password === "string" ? msg.password : "";
      info.pwHash = pw ? await hashPassword(pw, info.pwSalt) : "";
    }
    await this.save();
    this.broadcastRoom();
    await this.updateLobby();
  }

  async dissolve(reason: string) {
    const info = await this.load();
    if (!info || info.closed) return;
    info.closed = true;
    await this.save();
    for (const ws of this.ctx.getWebSockets()) {
      this.send(ws, { t: "closed", reason });
      try { ws.close(1000, reason); } catch { /* 이미 닫힘 */ }
    }
    await this.lobby().fetch("https://lobby/remove", { method: "POST", body: JSON.stringify({ id: info.id, code: info.code }) });
    await this.ctx.storage.deleteAlarm();
    await this.ctx.storage.deleteAll();
    this.info = null;
  }

  // ── 도우미 ──

  lobby() { return this.env.LOBBY.get(this.env.LOBBY.idFromName("lobby")); }

  async updateLobby() {
    const info = this.info;
    if (!info || info.closed) return;
    const s: RoomSummary = {
      id: info.id, code: info.code, title: info.settings.title, locked: !!info.pwHash, listed: info.settings.listed,
      count: this.members().length, max: info.settings.max, game: info.game, hostAway: !!info.hostAwayUntil, updated: Date.now(),
    };
    await this.lobby().fetch("https://lobby/update", { method: "POST", body: JSON.stringify(s) });
  }

  members(): { ws: WebSocket; m: Member }[] {
    const out: { ws: WebSocket; m: Member }[] = [];
    for (const ws of this.ctx.getWebSockets()) {
      const m = ws.deserializeAttachment() as Member | null;
      if (m && !m.pending && ws.readyState === WebSocket.OPEN) out.push({ ws, m });
    }
    return out;
  }

  findMember(id: string) { return this.members().find(x => x.m.id === id) ?? null; }

  memberList(info: RoomInfo) {
    return this.members()
      .sort((a, b) => a.m.joined - b.m.joined)
      .map(({ m }) => ({ id: m.id, name: m.name, host: m.host, muted: info.muted.includes(m.clientId), color: m.color ?? 0 }));
  }

  publicRoom(info: RoomInfo) {
    const host = this.members().find(x => x.m.host);
    return {
      id: info.id, code: info.code, locked: !!info.pwHash, settings: info.settings, game: info.game,
      hostId: host?.m.id ?? "", hostAway: !!info.hostAwayUntil,
    };
  }

  send(ws: WebSocket, data: unknown) {
    try { ws.send(JSON.stringify(data)); } catch { /* 닫히는 중 */ }
  }

  error(ws: WebSocket, code: string, message: string) { this.send(ws, { t: "error", code, message }); }

  broadcast(data: unknown, except?: WebSocket) {
    const text = JSON.stringify(data);
    for (const { ws } of this.members()) if (ws !== except) { try { ws.send(text); } catch { /* */ } }
  }

  broadcastMembers(except?: WebSocket) {
    if (!this.info) return;
    this.broadcast({ t: "members", members: this.memberList(this.info) }, except);
  }

  broadcastRoom() {
    if (!this.info) return;
    this.broadcast({ t: "room", room: this.publicRoom(this.info) });
  }
}
