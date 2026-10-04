// rocketrpg.h - RocketRPG Core public C-ABI (single source of truth)
// Encoding: all char* are UTF-8 unless noted. Unicode-safe on Win32 internally.
#pragma once
#include <stdint.h>

#if defined(_WIN32)
  #define RPG_EXPORT extern "C" __declspec(dllexport)
#else
  #define RPG_EXPORT extern "C"
#endif

typedef int32_t rstatus;
#define RP_OK              0
#define RP_ERR_INVALID    (-1)
#define RP_ERR_NOTFOUND   (-2)
#define RP_ERR_IO         (-3)
#define RP_ERR_UNSUPPORTED (-4)
#define RP_ERR_STATE      (-5)

enum rp_engine {
  RPG_ENGINE_UNKNOWN = 0,
  RPG_ENGINE_RM2000  = 1,
  RPG_ENGINE_RM2003  = 2,
  RPG_ENGINE_RMXP    = 3,
  RPG_ENGINE_RMVX    = 4,
  RPG_ENGINE_RMVXACE = 5,
  RPG_ENGINE_RMMV    = 6,
  RPG_ENGINE_RMMZ    = 7
};

enum rp_layout { RPG_LAYOUT_LOOSE = 0, RPG_LAYOUT_ARCHIVE = 1, RPG_LAYOUT_NWJS_WWW = 2, RPG_LAYOUT_NWJS_ROOT = 3 };

struct rp_game_info {
  uint32_t size;            // sizeof(rp_game_info)
  wchar_t  dir[1024];       // game root (wide for UI convenience)
  char     title_utf8[256];
  char     title_ascii_safe[256];
  int32_t  engine;          // rp_engine
  int32_t  layout;          // rp_layout
  char     runtime_dll[128];// RGSS*.dll / empty
  char     exe_name[128];
  uint8_t  has_rtp;         // RTP resolvable from local RTP dir or FullPackage
  uint8_t  full_package;    // RPG_RT.ini FullPackageFlag=1
};

// ---------- lifecycle ----------
RPG_EXPORT rstatus rpg_core_init(const wchar_t* portable_root /*nullable*/);
RPG_EXPORT void    rpg_core_shutdown(void);
RPG_EXPORT const char* rpg_last_error(void);
RPG_EXPORT const char* rpg_version(void);
// Native crash reports (faults inside native modules only) -> crash_dir\crash-native-*.txt
RPG_EXPORT rstatus rpg_install_crash_handler(const wchar_t* crash_dir);

// ---------- game discovery ----------
RPG_EXPORT rstatus rpg_scan_games(const wchar_t* search_root, wchar_t*** out_dirs, int32_t* out_count);
RPG_EXPORT void    rpg_free_strings(wchar_t** arr, int32_t count);
RPG_EXPORT rstatus rpg_detect_game(const wchar_t* game_dir, rp_game_info* out_info);
RPG_EXPORT rstatus rpg_validate_game(const wchar_t* game_dir, char* out_report_utf8, int32_t report_cap);

// ---------- game volume (native runtime process: mkxp-z / EasyRPG Player) ----------
RPG_EXPORT rstatus rpg_set_volume_for_pid(uint32_t pid, double percent); // 0..100 (WASAPI caps at 100)

// ---------- mkxp-z native rendering engine (RocketRenderMKXP) ----------
#include "rocket_render_mkxp.h"

// ---------- EasyRPG Player native rendering engine (RocketRenderEasyRPG) ----------
#include "rocket_render_easyrpg.h"

// ---------- multi: host screen / game sound capture ----------
#include "rocket_multi.h"
