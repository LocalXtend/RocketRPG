// RocketRPG 멀티 (MV/MZ 방장): 게임 캔버스를 RocketRPG와 함께 쓰는 메모리에 씁니다.
// RocketRPG가 방송하는 동안(머리 7번 칸 > 0)에만 PIXI가 화면을 그린 직후 한 장씩 복사합니다.
// 이렇게 하면 RocketRPG 창 위에 그린 채팅·핑 없이 게임 화면만 참가자에게 갑니다.
// 메모리 구성 (int32): 0 'RRFR', 1 번호(+1), 2 칸(0/1), 3 폭, 4 높이, 5 형식(0 RGBA, 1 BGRA), 6 칸 크기, 7 원하는 초당 장 수(0 = 멈춤)
(() => {
  if (window.__rrMultiFrames) return;
  window.__rrMultiFrames = true;
  const wv = window.chrome && window.chrome.webview;
  if (!wv) return;

  let buf = null, hdr = null, slotBytes = 0, slot = 0, busy = false, formatOption = true, lastAt = -1e9;

  wv.addEventListener("sharedbufferreceived", (e) => {
    const d = e.additionalData || {};
    if (d.kind !== "rr-frames") return;
    buf = e.getBuffer();
    hdr = new Int32Array(buf, 0, 8);
    slotBytes = d.slotBytes | 0;
    hdr[0] = 0x52524652;
    hdr[6] = slotBytes;
  });

  function publish(next, w, h, fmt) {
    slot = next;
    hdr[2] = slot; hdr[3] = w; hdr[4] = h; hdr[5] = fmt;
    hdr[1] = (hdr[1] + 1) | 0;
  }

  // 게임 안 동영상 (이벤트 '동영상 재생'): 캔버스 위에 따로 뜨는 <video>라 캔버스만 잡으면 참가자에게 보이지 않았음.
  // 재생 중이면 그 영상을 게임 화면 크기에 맞춰(비율 유지, 남는 곳은 검게) 그려 보냅니다.
  function playingMovie() {
    try {
      let v = null;
      if (window.Video && typeof Video.isPlaying === "function" && Video.isPlaying()) v = Video._element;                     // MZ
      else if (window.Graphics && typeof Graphics.isVideoPlaying === "function" && Graphics.isVideoPlaying()) v = Graphics._video;   // MV
      return v && v.readyState >= 2 && v.videoWidth > 0 ? v : null;
    } catch { return null; }
  }

  let movieCanvas = null, movieCtx = null;
  function movieFrame(video, w, h) {
    if (!movieCanvas) { movieCanvas = document.createElement("canvas"); movieCtx = movieCanvas.getContext("2d", { alpha: false }); }
    if (movieCanvas.width !== w) movieCanvas.width = w;
    if (movieCanvas.height !== h) movieCanvas.height = h;
    movieCtx.fillStyle = "#000";
    movieCtx.fillRect(0, 0, w, h);
    const s = Math.min(w / video.videoWidth, h / video.videoHeight);
    const dw = video.videoWidth * s, dh = video.videoHeight * s;
    movieCtx.drawImage(video, (w - dw) / 2, (h - dh) / 2, dw, dh);
    return new VideoFrame(movieCanvas, { timestamp: 0 });
  }

  function grab(canvas) {
    if (!hdr || hdr[7] <= 0 || busy || typeof VideoFrame !== "function") return;
    const now = performance.now();
    if (now - lastAt < 1000 / hdr[7] - 4) return;   // RocketRPG가 원하는 초당 장 수까지만
    lastAt = now;
    let frame;
    const movie = playingMovie();
    if (movie) { try { frame = movieFrame(movie, canvas.width, canvas.height); } catch { } }   // 못 그리면(다른 출처 영상 등) 게임 화면으로
    if (!frame) { try { frame = new VideoFrame(canvas, { timestamp: 0 }); } catch { return; } }   // WebGL 그림은 그린 직후에만 남아 있음
    const w = frame.displayWidth, h = frame.displayHeight;
    if (!w || !h || w * h * 4 > slotBytes) { frame.close(); return; }
    busy = true;
    const next = slot ^ 1;
    const dst = new Uint8Array(buf, 4096 + next * slotBytes, w * h * 4);
    const plain = () => {
      const f = frame.format || "";
      const fmt = /^RGB[AX]$/.test(f) ? 0 : /^BGR[AX]$/.test(f) ? 1 : -1;
      if (fmt < 0) return Promise.resolve();
      return frame.copyTo(dst).then(() => publish(next, w, h, fmt));
    };
    const job = formatOption
      ? frame.copyTo(dst, { format: "BGRA" }).then(() => publish(next, w, h, 1), () => { formatOption = false; return plain(); })   // RocketRPG가 그대로 쓰는 순서
      : plain();
    job.catch(() => { }).finally(() => { frame.close(); busy = false; });
  }

  // PIXI가 화면(캔버스)에 그린 직후 (MV: PIXI v4 WebGLRenderer/CanvasRenderer, MZ: PIXI v5 Renderer).
  // 다른 텍스처에 그리는 호출(두 번째 인자)은 건너뜁니다.
  function hookPixi() {
    const P = window.PIXI;
    if (!P) return false;
    for (const name of ["Renderer", "WebGLRenderer", "CanvasRenderer"]) {
      const C = P[name];
      if (!C || !C.prototype || typeof C.prototype.render !== "function" || C.prototype.__rrFrames) continue;
      const render = C.prototype.render;
      C.prototype.render = function (obj, target) {
        const r = render.apply(this, arguments);
        const toScreen = !target || (typeof target === "object" && !target.baseTexture && !target.renderTexture);
        if (toScreen && this.view && (!window.Graphics || !Graphics._canvas || this.view === Graphics._canvas)) grab(this.view);
        return r;
      };
      C.prototype.__rrFrames = true;
    }
    return true;
  }
  const timer = setInterval(() => { if (hookPixi()) clearInterval(timer); }, 200);
})();
