// RocketRPG 멀티 서버 (Cloudflare Worker)
//   GET  /api/rooms              공개 방 목록
//   POST /api/rooms              방 만들기 → { id, code, hostKey }
//   GET  /api/code/:code         방 코드로 방 찾기
//   GET  /api/rooms/:id/ws       방 WebSocket (첫 메시지 hello)
//   GET  /join/:code             디스코드 '참가' 단추용: RocketRPG를 열어 그 방에 입장
import { Lobby, Env } from "./lobby";
import { Room } from "./room";
import { MAX_PLAYERS, MIN_PLAYERS, PROTOCOL, clampInt, json, randomHex } from "./protocol";

export { Lobby, Room };

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    const url = new URL(req.url);
    const path = url.pathname;
    const lobby = env.LOBBY.get(env.LOBBY.idFromName("lobby"));

    if (path === "/api/rooms" && req.method === "GET") {
      return lobby.fetch("https://lobby/list");
    }

    if (path === "/api/rooms" && req.method === "POST") {
      let body: { title?: string; password?: string; max?: number; listed?: boolean; proto?: number };
      try { body = await req.json(); } catch { return json({ error: "bad_request" }, 400); }
      if (body.proto !== PROTOCOL) return json({ error: "version", message: "RocketRPG 버전이 서버와 맞지 않습니다. 업데이트해 주세요." }, 400);
      const id = randomHex(8);
      const reserved = await lobby.fetch("https://lobby/reserve", { method: "POST", body: JSON.stringify({ id }) });
      if (!reserved.ok) return json({ error: "server" }, 500);
      const { code } = await reserved.json<{ code: string }>();
      const room = env.ROOM.get(env.ROOM.idFromName(id));
      const init = await room.fetch("https://room/init", {
        method: "POST",
        body: JSON.stringify({
          id, code, title: body.title ?? "", password: body.password ?? "",
          max: clampInt(body.max, MIN_PLAYERS, MAX_PLAYERS, 4), listed: body.listed !== false,
        }),
      });
      if (!init.ok) return json({ error: "server" }, 500);
      const { hostKey } = await init.json<{ hostKey: string }>();
      return json({ id, code, hostKey });
    }

    let m = path.match(/^\/api\/code\/([A-Za-z0-9]{4,12})$/);
    if (m && req.method === "GET") return lobby.fetch(`https://lobby/code/${m[1].toUpperCase()}`);

    m = path.match(/^\/api\/rooms\/([0-9a-f]{16})\/ws$/);
    if (m) {
      const room = env.ROOM.get(env.ROOM.idFromName(m[1]));
      return room.fetch(new Request("https://room/ws", req));
    }

    m = path.match(/^\/join\/([A-Za-z0-9]{4,12})$/);
    if (m) return joinPage(m[1].toUpperCase());

    if (path === "/" || path === "/api") return json({ name: "rocketrpg-multi", protocol: PROTOCOL });
    return json({ error: "not_found" }, 404);
  },
} satisfies ExportedHandler<Env>;

function joinPage(code: string): Response {
  const link = `rocketrpg://join/${code}`;
  const html = `<!doctype html><html lang="ko"><head><meta charset="utf-8"><title>RocketRPG 방 참가</title>
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>body{font-family:system-ui,sans-serif;background:#f0f0f0;color:#1e1e1e;display:flex;min-height:100vh;align-items:center;justify-content:center;margin:0}
main{background:#fff;border:1px solid #bbb;padding:24px 28px;max-width:420px}a.btn{display:inline-block;margin-top:12px;padding:6px 14px;border:1px solid #888;background:#e8e8e8;color:#000;text-decoration:none}</style></head>
<body><main><h2>RocketRPG 방 참가</h2><p>방 코드 <b>${code}</b></p>
<p>RocketRPG가 열리지 않으면 아래 단추를 누르거나, RocketRPG의 멀티 &gt; 방 입장하기에서 코드를 입력하세요.</p>
<a class="btn" href="${link}">RocketRPG로 열기</a>
<p><a href="https://github.com/LocalXtend/RocketRPG-Release/releases/latest">RocketRPG 받기</a></p></main>
<script>location.href=${JSON.stringify(link)};</script></body></html>`;
  return new Response(html, { headers: { "content-type": "text/html; charset=utf-8" } });
}
