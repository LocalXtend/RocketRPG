// rocket_render_mkxp.h - RocketRenderMKXP Data-Oriented Native Rendering Engine for mkxp-z
// Encoding: UTF-8. Single source of truth for mkxp-z native runtime integration.
#pragma once
#include <stdint.h>
#include <stddef.h>

#if defined(_WIN32)
  #define MKXP_EXPORT extern "C" __declspec(dllexport)
#else
  #define MKXP_EXPORT extern "C"
#endif

#pragma pack(push, 8)

// MKXP Data-Oriented Configuration Struct (POD)
struct rp_mkxp_config {
    uint32_t size;               // sizeof(rp_mkxp_config)
    int32_t  rgss_version;       // 1 = XP, 2 = VX, 3 = VX Ace
    uint32_t screen_width;       // 640 for XP, 544 for VX/Ace
    uint32_t screen_height;      // 480 for XP, 416 for VX/Ace
    uint32_t target_fps;         // 40 for XP, 60 for VX/Ace
    uint8_t  fullscreen;         // 0 = windowed, 1 = fullscreen
    uint8_t  fixed_aspect_ratio; // 1 = preserve aspect ratio
    uint8_t  smooth_scaling;     // 1 = linear filter
    uint8_t  vsync;              // 0 = disabled, 1 = enabled
    uint8_t  win_resizable;      // 1 = resizable
    uint8_t  preload_bridge;     // unused (kept for struct layout; classic injection removed)
    uint8_t  use_shared_surface; // 1 = D3D11 shared texture surface
    uint8_t  reserved[5];        // padding
    wchar_t  game_dir[1024];     // game root directory
    wchar_t  custom_exe[1024];   // optional explicit mkxp-z executable
    wchar_t  rtp_path[1024];     // optional RTP directory
};

// MKXP Frame Data (POD)
struct rp_mkxp_frame {
    uint32_t size;               // sizeof(rp_mkxp_frame)
    uint32_t width;
    uint32_t height;
    uint32_t stride_bytes;
    uint64_t frame_index;
    double   fps;
    void*    shared_nt_handle;   // NT Handle to D3D11 shared texture
    void*    pixel_data;         // Direct CPU BGRA8 buffer pointer if available
    uint32_t pixel_data_bytes;
    uint32_t is_native_surface;  // 1 if rendered via native host surface
};

// MKXP Native Host Stats (POD)
struct rp_mkxp_stats {
    uint32_t size;
    uint32_t pid;
    void*    hwnd;
    uint64_t total_rendered_frames;
    double   current_fps;
    int32_t  is_running;
    int32_t  is_hosted;
};

#pragma pack(pop)

// ---------- MKXP Native Engine C-ABI Exports ----------
MKXP_EXPORT int32_t rpg_mkxp_is_available(void);
MKXP_EXPORT int32_t rpg_mkxp_is_available_game(const wchar_t* game_dir);
MKXP_EXPORT int32_t rpg_mkxp_generate_config(const rp_mkxp_config* cfg, char* out_json, int32_t cap);
MKXP_EXPORT int32_t rpg_mkxp_create(const rp_mkxp_config* cfg, void** out_instance);
MKXP_EXPORT int32_t rpg_mkxp_start(void* instance);
MKXP_EXPORT int32_t rpg_mkxp_stop(void* instance);
MKXP_EXPORT int32_t rpg_mkxp_is_running(void* instance, int32_t* out_running);
MKXP_EXPORT int32_t rpg_mkxp_get_frame(void* instance, rp_mkxp_frame* out_frame);
MKXP_EXPORT int32_t rpg_mkxp_read_pixels(void* instance, void* dst_bgra8, uint32_t cap_bytes);
MKXP_EXPORT int32_t rpg_mkxp_get_hwnd(void* instance, void** out_hwnd);
MKXP_EXPORT int32_t rpg_mkxp_get_pid(void* instance, uint32_t* out_pid);
MKXP_EXPORT int32_t rpg_mkxp_host_window(void* instance, void* parent_hwnd, int32_t x, int32_t y, int32_t w, int32_t h);
MKXP_EXPORT int32_t rpg_mkxp_resize(void* instance, int32_t w, int32_t h);
MKXP_EXPORT int32_t rpg_mkxp_send_input(void* instance, uint32_t msg, uintptr_t wparam, intptr_t lparam);
MKXP_EXPORT int32_t rpg_mkxp_get_stats(void* instance, rp_mkxp_stats* out_stats);
MKXP_EXPORT void    rpg_mkxp_destroy(void* instance);
