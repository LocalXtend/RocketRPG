// rocket_multi.h - 멀티: 방장 화면(게임 영역)과 게임 소리 캡처
// Encoding: UTF-8.
#pragma once
#include <stdint.h>

#if defined(_WIN32)
  #define MULTI_EXPORT extern "C" __declspec(dllexport)
#else
  #define MULTI_EXPORT extern "C"
#endif

// ---------- screen: Windows.Graphics.Capture of a top-level window, cropped to a child window ----------
// top_hwnd: the RocketRPG main window (WGC only captures top-level windows).
MULTI_EXPORT int32_t rpg_cap_open(void* top_hwnd, void** out_cap);
// Crops the latest frame to target_hwnd's client area (a child of top_hwnd), scales it to fit max_w x max_h
// (never up) and writes top-down BGRA (alpha 255, stride = width*4) to dst.
// The GPU readback never blocks: a copy is queued and picked up by a later call, so poll every few ms.
// allow_new = 0 drops new frames instead of copying them (frame-rate limit); *out_took = 1 when this call took one.
// RP_OK: a new frame was written (*out_w/*out_h set). RP_ERR_STATE: nothing ready yet.
MULTI_EXPORT int32_t rpg_cap_read(void* cap, void* target_hwnd, int32_t max_w, int32_t max_h, int32_t allow_new,
                                  void* dst, uint32_t cap_bytes, int32_t* out_w, int32_t* out_h, int32_t* out_took);
MULTI_EXPORT void    rpg_cap_close(void* cap);

// ---------- sound: process loopback (the process and its children), 48 kHz stereo s16 ----------
MULTI_EXPORT int32_t rpg_loopback_open(uint32_t pid, void** out_lb);
// Copies up to max_frames interleaved stereo frames; returns the frame count (0 when nothing is buffered).
MULTI_EXPORT int32_t rpg_loopback_read(void* lb, int16_t* dst, int32_t max_frames);
MULTI_EXPORT void    rpg_loopback_close(void* lb);

// ---------- overlay: chat (flowing text) and pings drawn on top of the whole window (DirectComposition) ----------
// Part of the window's own composition, so window-capture tools (Discord window share) see it.
MULTI_EXPORT int32_t rpg_overlay_open(void* hwnd, void** out_overlay);
// Game area in window client pixels; everything is clipped to it.
MULTI_EXPORT void    rpg_overlay_area(void* overlay, int32_t x, int32_t y, int32_t w, int32_t h);
// Text flowing right to left (longer text moves faster so every line takes the same time).
MULTI_EXPORT void    rpg_overlay_chat(void* overlay, const wchar_t* text, uint32_t color, int32_t fixed_lane);
// Ping marker at nx, ny (0..1 of the area). fresh = 1 creates it (with an appear animation), 0 moves it.
MULTI_EXPORT void    rpg_overlay_ping(void* overlay, int32_t id, float nx, float ny, int32_t fresh, uint32_t color);
MULTI_EXPORT void    rpg_overlay_remove(void* overlay, int32_t id);   // fades the ping out
MULTI_EXPORT void    rpg_overlay_clear(void* overlay, int32_t what);  // 1 chat, 2 pings, 3 both
MULTI_EXPORT void    rpg_overlay_tick(void* overlay);                  // drops finished items (call every few hundred ms)
MULTI_EXPORT void    rpg_overlay_close(void* overlay);
