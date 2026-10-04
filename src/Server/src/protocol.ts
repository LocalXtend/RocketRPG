// RocketRPG 멀티 서버 ↔ 클라이언트 공통 약속.
// 모든 메시지는 JSON 한 개 = WebSocket 텍스트 한 프레임, 종류는 "t".

/** 클라이언트(RocketRPG)와 서버가 맞아야 하는 약속 번호. 바뀌면 옛 클라이언트는 입장할 수 없다고 안내. */
export const PROTOCOL = 1;

export const MIN_PLAYERS = 2;
export const MAX_PLAYERS = 8;
export const HOST_GRACE_MS = 60_000;          // 방장 연결이 끊긴 뒤 방을 유지하는 시간
export const LOBBY_REFRESH_MS = 5 * 60_000;   // 방이 살아 있음을 목록에 알리는 주기
export const LOBBY_STALE_MS = 12 * 60_000;    // 이 시간 넘게 소식이 없으면 목록에서 뺌
export const MAX_TITLE = 40;
export const MAX_NAME = 20;
export const MAX_CHAT = 120;
export const FIXED_CHAT_MS = 90_000;          // 고정 채팅(한 줄을 6초 차지)은 한 사람이 90초에 한 번
export const MAX_MESSAGE_BYTES = 64 * 1024;   // 노트 이미지 등은 따로 (나중 단계)

/** 목록에 보이는 방 정보 */
export interface RoomSummary {
  id: string;
  code: string;
  title: string;
  locked: boolean;      // 비밀번호 있음
  listed: boolean;      // 공개 목록에 표시
  count: number;        // 지금 인원 (방장 포함)
  max: number;
  game: string;         // 방장이 하는 게임 제목 ("" = 대기 중)
  hostAway: boolean;
  updated: number;      // ms
}

/** 방 설정 (방장이 바꿈) */
export interface RoomSettings {
  title: string;
  max: number;
  listed: boolean;
  control: boolean;       // 참가자 조종 권한 (키보드)
  notesEditable: boolean; // 공유 노트 수정 허용
  fps: number;            // 30 | 60
  quality: string;        // "low" | "normal" | "high"
  streamer: boolean;      // 방장이 스트리머 모드: 참가자 화면에서도 방 제목·코드를 숨김
}

export const DEFAULT_SETTINGS: RoomSettings = {
  title: "",
  max: 4,
  listed: true,
  control: false,
  notesEditable: true,
  fps: 30,
  quality: "normal",
  streamer: false,
};

export function cleanText(s: unknown, max: number): string {
  if (typeof s !== "string") return "";
  // 제어 문자 제거, 공백 정리
  let t = s.replace(/[\u0000-\u001f\u007f]/g, " ").replace(/\s+/g, " ").trim();
  if ([...t].length > max) t = [...t].slice(0, max).join("");
  return t;
}

export function clampInt(v: unknown, min: number, max: number, fallback: number): number {
  const n = typeof v === "number" && Number.isFinite(v) ? Math.round(v) : fallback;
  return Math.min(max, Math.max(min, n));
}

const CODE_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 헷갈리는 I,O,0,1 제외

export function randomCode(len = 6): string {
  const bytes = new Uint8Array(len);
  crypto.getRandomValues(bytes);
  return [...bytes].map(b => CODE_ALPHABET[b % CODE_ALPHABET.length]).join("");
}

export function randomHex(bytes: number): string {
  const b = new Uint8Array(bytes);
  crypto.getRandomValues(b);
  return [...b].map(x => x.toString(16).padStart(2, "0")).join("");
}

export async function hashPassword(password: string, salt: string): Promise<string> {
  const data = new TextEncoder().encode(salt + "\u0000" + password);
  const digest = await crypto.subtle.digest("SHA-256", data);
  return [...new Uint8Array(digest)].map(x => x.toString(16).padStart(2, "0")).join("");
}

export function json(data: unknown, status = 200): Response {
  return new Response(JSON.stringify(data), {
    status,
    headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" },
  });
}
