// rocket_render_easyrpg.h - RocketRenderEasyRPG Data-Oriented Native Rendering Engine for EasyRPG Player
// Encoding: UTF-8. Single source of truth for EasyRPG Player native runtime integration.
#pragma once
#include <stdint.h>
#include <stddef.h>

#if defined(_WIN32)
  #define EASYRPG_EXPORT extern "C" __declspec(dllexport)
#else
  #define EASYRPG_EXPORT extern "C"
#endif

#pragma pack(push, 8)

// EasyRPG Data-Oriented Configuration Struct (POD)
struct rp_easyrpg_config {
    uint32_t size;               // sizeof(rp_easyrpg_config)
    int32_t  engine_type;        // 1 = RM2000, 2 = RM2003
    uint32_t screen_width;       // 320 for 2000/2003
    uint32_t screen_height;      // 240 for 2000/2003
    uint32_t target_fps;         // 60 for 2000/2003
    uint8_t  fullscreen;         // 0 = windowed, 1 = fullscreen
    uint8_t  fixed_aspect_ratio; // 1 = preserve aspect ratio
    uint8_t  smooth_scaling;     // 1 = linear filter
    uint8_t  vsync;              // 0 = disabled, 1 = enabled
    uint8_t  win_resizable;      // 1 = resizable
    uint8_t  preload_bridge;     // unused (kept for struct layout; classic injection removed)
    uint8_t  use_shared_surface; // 1 = D3D11 shared texture surface
    uint8_t  reserved[5];        // padding
    wchar_t  game_dir[1024];     // game root directory
    wchar_t  custom_exe[1024];   // optional explicit EasyRPG Player executable
    wchar_t  rtp_path[1024];     // optional RTP directory
};

// EasyRPG Frame Data (POD)
struct rp_easyrpg_frame {
    uint32_t size;               // sizeof(rp_easyrpg_frame)
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

// EasyRPG Native Host Stats (POD)
struct rp_easyrpg_stats {
    uint32_t size;
    uint32_t pid;
    void*    hwnd;
    uint64_t total_rendered_frames;
    double   current_fps;
    int32_t  is_running;
    int32_t  is_hosted;
};

#pragma pack(pop)

// ---------- EasyRPG Native Engine C-ABI Exports ----------
EASYRPG_EXPORT int32_t rpg_easyrpg_is_available(void);
EASYRPG_EXPORT int32_t rpg_easyrpg_is_available_game(const wchar_t* game_dir);
EASYRPG_EXPORT int32_t rpg_easyrpg_create(const rp_easyrpg_config* cfg, void** out_instance);
EASYRPG_EXPORT int32_t rpg_easyrpg_start(void* instance);
EASYRPG_EXPORT int32_t rpg_easyrpg_stop(void* instance);
EASYRPG_EXPORT int32_t rpg_easyrpg_is_running(void* instance, int32_t* out_running);
EASYRPG_EXPORT int32_t rpg_easyrpg_get_frame(void* instance, rp_easyrpg_frame* out_frame);
EASYRPG_EXPORT int32_t rpg_easyrpg_read_pixels(void* instance, void* dst_bgra8, uint32_t cap_bytes);
EASYRPG_EXPORT int32_t rpg_easyrpg_get_hwnd(void* instance, void** out_hwnd);
EASYRPG_EXPORT int32_t rpg_easyrpg_get_pid(void* instance, uint32_t* out_pid);
EASYRPG_EXPORT int32_t rpg_easyrpg_host_window(void* instance, void* parent_hwnd, int32_t x, int32_t y, int32_t w, int32_t h);
EASYRPG_EXPORT int32_t rpg_easyrpg_resize(void* instance, int32_t w, int32_t h);
EASYRPG_EXPORT int32_t rpg_easyrpg_send_input(void* instance, uint32_t msg, uintptr_t wparam, intptr_t lparam);
EASYRPG_EXPORT int32_t rpg_easyrpg_get_stats(void* instance, rp_easyrpg_stats* out_stats);
EASYRPG_EXPORT void    rpg_easyrpg_destroy(void* instance);
