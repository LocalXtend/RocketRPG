// rmdetect.h - RPGMakerVersionDetect public API
//
// Standalone detection library: classifies a game folder as one of the seven
// RPG Maker engines (2000 / 2003 / XP / VX / VX Ace / MV / MZ) or an RTP-only
// resource folder, purely from local file-system signatures measured against
// RocketRPG's own sample collection.
//
// ABI contract:
//   - Flat C exports only. No CRT allocation crosses the boundary; callers
//     supply the output struct and never free anything returned here.
//   - All returned char* are static strings owned by the DLL.
//   - RMD_RESULT is versioned via its first field (size). Zero-init the whole
//     struct and set size = sizeof(RMD_RESULT) before calling rmd_detect().
//
// Thread safety: rmd_detect() is stateless and safe to call concurrently.

#ifndef RMD_DETECT_H
#define RMD_DETECT_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Export decoration */
#if defined(RMD_BUILD)
#  define RMD_EXPORT __declspec(dllexport)
#else
#  define RMD_EXPORT __declspec(dllimport)
#endif

/* Detected engine. Order is fixed; values are stable ABI. */
enum RMD_ENGINE {
    RMD_ENGINE_UNKNOWN = 0,
    RMD_ENGINE_RM2000  = 1,  /* RPG Maker 2000            (Delphi RPG_RT.exe)   */
    RMD_ENGINE_RM2003  = 2,  /* RPG Maker 2003            (Delphi RPG_RT.exe)   */
    RMD_ENGINE_RMXP    = 3,  /* RPG Maker XP              (RGSS1, .rxdata)      */
    RMD_ENGINE_RMVX    = 4,  /* RPG Maker VX              (RGSS2, .rvdata)      */
    RMD_ENGINE_RMVXACE = 5,  /* RPG Maker VX Ace          (RGSS3, .rvdata2)     */
    RMD_ENGINE_RMMV    = 6,  /* RPG Maker MV              (NW.js, rpg_*.js)     */
    RMD_ENGINE_RMMZ    = 7,  /* RPG Maker MZ              (NW.js, rmmz_*.js)    */
};

/* Data layout observed inside the folder. */
enum RMD_LAYOUT {
    RMD_LAYOUT_UNKNOWN   = 0,
    RMD_LAYOUT_LOOSE     = 1, /* assets unpacked next to the exe                 */
    RMD_LAYOUT_ARCHIVE   = 2, /* Game.rgssad / .rgss2a / .rgss3a archive         */
    RMD_LAYOUT_NWJS_WWW  = 3, /* NW.js deployment with www\ subfolder (MV/MZ)    */
    RMD_LAYOUT_NWJS_ROOT = 4, /* flattened NW.js deployment (MV/MZ, no www)      */
    RMD_LAYOUT_BOXED     = 5, /* single-file boxed exe (assets embedded in exe)  */
};

/* What kind of folder this is. */
enum RMD_KIND {
    RMD_KIND_UNKNOWN = 0,
    RMD_KIND_GAME    = 1,    /* playable game folder                            */
    RMD_KIND_RTP     = 2,    /* bare RTP resource root (installed RTP folder)    */
};

/* Status codes returned by rmd_detect(). */
enum RMD_STATUS {
    RMD_OK          =  0,    /* analysis completed; inspect engine/kind        */
    RMD_ERR_INVALID = -1,    /* null argument                                  */
    RMD_ERR_NOTDIR  = -2,    /* path does not exist or is not a directory      */
};

typedef struct RMD_RESULT {
    uint32_t size;             /* must be sizeof(RMD_RESULT), set by caller   */
    int32_t  engine;           /* enum RMD_ENGINE                             */
    int32_t  layout;           /* enum RMD_LAYOUT                             */
    int32_t  kind;             /* enum RMD_KIND                               */
    wchar_t  dir[260];         /* echo of the analyzed folder path            */
    char     title_utf8[192];  /* best-effort game title ("" when unknown)    */
    char     runtime_utf8[96]; /* runtime evidence: RGSS dll name/path,
                                   "NW.js", "NW.js(legacy)", "RPG_RT.exe" ...  */
    char     detail_utf8[1024];/* semicolon-separated ASCII evidence tokens
                                   explaining WHY the verdict was reached      */
} RMD_RESULT;

/* Library ABI revision. Bumps on any RMD_RESULT layout change. */
RMD_EXPORT int32_t rmd_abi(void);

/* Human-readable names for engine/layout/kind enums. Never NULL. */
RMD_EXPORT const char* rmd_engine_name(int32_t engine);
RMD_EXPORT const char* rmd_layout_name(int32_t layout);
RMD_EXPORT const char* rmd_kind_name(int32_t kind);

/*
 * Classify one folder.
 *
 * dir_utf16 : absolute or relative folder path (UTF-16, Win32 native).
 * out       : caller-provided result struct with out->size pre-filled.
 *
 * Returns RMD_OK even when nothing was recognized (engine stays UNKNOWN);
 * negative codes above indicate caller errors only.
 *
 * Detection tiers (first hit wins):
 *   1. NW.js / package.json family -> MV or MZ (www, flat, or boxed)
 *   2. Game.ini [Game] family      -> XP / VX / VX Ace (Library= + Scripts=)
 *   3. RPG_RT.ini + *.ldb family   -> 2000 / 2003
 *   4. recursive data-extension sweep (.rxdata/.rvdata/.rvdata2)
 *   5. bare RTP asset-folder fingerprints -> kind=RTP + engine guess
 *   6. otherwise UNKNOWN
 */
RMD_EXPORT int32_t rmd_detect(const wchar_t* dir_utf16, RMD_RESULT* out);

#ifdef __cplusplus
}
#endif

#endif /* RMD_DETECT_H */
