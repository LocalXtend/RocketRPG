// 방 목록 (Durable Object 하나): 방 요약과 방 코드(6자리) → 방 id.
// 방(Room)이 만들어지거나 인원이 바뀔 때 요약을 보내고, 없어질 때 지웁니다.
import { DurableObject } from "cloudflare:workers";
import { LOBBY_STALE_MS, RoomSummary, json, randomCode } from "./protocol";

export interface Env {
  LOBBY: DurableObjectNamespace<import("./lobby").Lobby>;
  ROOM: DurableObjectNamespace<import("./room").Room>;
  // TURN 중계 (선택, wrangler secret). 없으면 STUN만 알려 주고, 직접 연결이 안 되는 참가자는 영상을 못 받습니다.
  // 고정 계정 TURN (ExpressTURN, Metered 등): 주소(쉼표로 여러 개)·아이디·비밀번호. 두 번째 서비스는 TURN2_*.
  TURN_URL?: string;
  TURN_USERNAME?: string;
  TURN_CREDENTIAL?: string;
  TURN2_URL?: string;
  TURN2_USERNAME?: string;
  TURN2_CREDENTIAL?: string;
  // Cloudflare Realtime TURN (결제 수단 등록이 필요함)
  TURN_KEY_ID?: string;
  TURN_KEY_API_TOKEN?: string;
}

export class Lobby extends DurableObject<Env> {
  async fetch(req: Request): Promise<Response> {
    const url = new URL(req.url);
    const path = url.pathname;

    if (req.method === "GET" && path === "/list") return json({ rooms: await this.list() });

    if (req.method === "POST" && path === "/reserve") {
      const { id } = await req.json<{ id: string }>();
      for (let i = 0; i < 20; i++) {
        const code = randomCode();
        if (!(await this.ctx.storage.get(`code:${code}`))) {
          await this.ctx.storage.put(`code:${code}`, id);
          return json({ code });
        }
      }
      return json({ error: "no_code" }, 500);
    }

    if (req.method === "POST" && path === "/update") {
      const s = await req.json<RoomSummary>();
      s.updated = Date.now();
      await this.ctx.storage.put(`room:${s.id}`, s);
      return json({ ok: true });
    }

    if (req.method === "POST" && path === "/remove") {
      const { id, code } = await req.json<{ id: string; code: string }>();
      await this.ctx.storage.delete([`room:${id}`, `code:${code}`]);
      return json({ ok: true });
    }

    if (req.method === "GET" && path.startsWith("/code/")) {
      const code = decodeURIComponent(path.slice("/code/".length)).toUpperCase();
      const id = await this.ctx.storage.get<string>(`code:${code}`);
      if (!id) return json({ error: "not_found" }, 404);
      const s = await this.ctx.storage.get<RoomSummary>(`room:${id}`);
      if (!s || Date.now() - s.updated > LOBBY_STALE_MS) return json({ error: "not_found" }, 404);
      return json({ id, room: s });
    }

    return json({ error: "not_found" }, 404);
  }

  async list(): Promise<RoomSummary[]> {
    const now = Date.now();
    const rooms = await this.ctx.storage.list<RoomSummary>({ prefix: "room:" });
    const result: RoomSummary[] = [];
    const stale: string[] = [];
    for (const [key, s] of rooms) {
      if (now - s.updated > LOBBY_STALE_MS) { stale.push(key, `code:${s.code}`); continue; }
      if (s.listed) result.push(s);
    }
    if (stale.length) await this.ctx.storage.delete(stale);
    result.sort((a, b) => b.updated - a.updated);
    return result;
  }
}
