// rmd_detect.cpp - folder classification tiers + exported API.
//
// Detection is a strictly ordered cascade (first hit wins):
//   1. NW.js family (MV/MZ)          - package.json / index.html / js markers,
//                                      with a stream-scan fallback for single
//                                      exe "boxed" deployments
//   2. Game.ini [Game] family        - XP / VX / VX Ace via Library + Scripts
//   3. RPG_RT.ini + *.ldb family     - 2000 / 2003 via upgrade dirs + exe meta
//   4. recursive data-extension sweep- last resort for stripped folders
//   5. bare RTP resource roots       - kind=RTP fingerprints
//   6. otherwise UNKNOWN
//
// All literal Win32 name lookups are case-insensitive by construction; every
// classification decision survives the measured traps in the sample corpus
// (renamed Data folder, dual RGSS dlls, archive+loose mixes, renamed exes).

#include "rmdetect.h"
#include "rmd_internal.hpp"

#include <cstring>
#include <cstdio>
#include <string>
#include <vector>

using namespace rmd;

namespace {

/* ---------------- small shared helpers ---------------- */

void ev_add(std::string& ev, const std::string& tok) {
    if (!ev.empty()) ev += ';';
    ev += tok;
}

std::string ascii_lower(const std::string& s) {
    std::string r(s);
    for (size_t i = 0; i < r.size(); i++)
        if (r[i] >= 'A' && r[i] <= 'Z') r[i] = (char)(r[i] - 'A' + 'a');
    return r;
}

bool contains(const std::string& hay, const char* needle) {
    return hay.find(needle) != std::string::npos;
}

wchar_t wlwr(wchar_t c) {
    return (c >= L'A' && c <= L'Z') ? (wchar_t)(c - L'A' + L'a') : c;
}

bool wi_eq(const std::wstring& a, const std::wstring& b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); i++)
        if (wlwr(a[i]) != wlwr(b[i])) return false;
    return true;
}

bool ends_with_ci(const std::wstring& s, const wchar_t* suffix) {
    size_t n = wcslen(suffix);
    if (s.size() < n) return false;
    return wi_eq(s.substr(s.size() - n), suffix);
}

struct DirEntry {
    std::wstring name;
    bool is_dir = false;
    uint64_t size = 0;
};

// One enumeration of dir\*; callers filter instead of re-probing the disk.
void list_dir(const std::wstring& dir, std::vector<DirEntry>* out) {
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW(join_path(dir, L"*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        if (wcscmp(fd.cFileName, L".") == 0 || wcscmp(fd.cFileName, L"..") == 0) continue;
        DirEntry e;
        e.name = fd.cFileName;
        e.is_dir = (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
        e.size = ((uint64_t)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
        out->push_back(e);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
}

// VS_FIXEDFILEINFO version as "a.b.c.d"; empty on any failure.
bool exe_file_version(const std::wstring& path, char* out, size_t cap) {
    out[0] = '\0';
    DWORD handle = 0;
    DWORD sz = GetFileVersionInfoSizeW(path.c_str(), &handle);
    if (!sz) return false;
    std::vector<uint8_t> buf(sz);
    if (!GetFileVersionInfoW(path.c_str(), 0, sz, buf.data())) return false;
    VS_FIXEDFILEINFO* fi = nullptr;
    UINT len = 0;
    if (!VerQueryValueW(buf.data(), L"\\", (LPVOID*)&fi, &len) || !fi) return false;
    snprintf(out, cap, "%u.%u.%u.%u",
             (unsigned)HIWORD(fi->dwFileVersionMS), (unsigned)LOWORD(fi->dwFileVersionMS),
             (unsigned)HIWORD(fi->dwFileVersionLS), (unsigned)LOWORD(fi->dwFileVersionLS));
    return true;
}

// Keep only lowercase URL/filename-safe chars so detail stays machine-parsable.
std::string sanitize_token(const std::string& s) {
    std::string r;
    for (size_t i = 0; i < s.size(); i++) {
        char c = ascii_lower(std::string(1, s[i]))[0];
        if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')
            r += c;
    }
    return r;
}

/* ---------------- tier 1: NW.js family (MV / MZ) ---------------- */

bool detect_nwjs(const std::wstring& dir, RMD_RESULT* res, std::string& ev) {
    const std::wstring www = join_path(dir, L"www");
    const bool has_www = dir_exists(www);
    const bool pkg_root = file_exists(join_path(dir, L"package.json"));
    const bool pkg_www = has_www && file_exists(join_path(www, L"package.json"));
    const bool idx_root = file_exists(join_path(dir, L"index.html"));
    const bool idx_www = has_www && file_exists(join_path(www, L"index.html"));

    const std::wstring base = has_www ? www : dir;
    const std::wstring jsdir = join_path(base, L"js");
    const bool mz = file_exists(join_path(jsdir, L"rmmz_core.js"));
    const bool mv = file_exists(join_path(jsdir, L"rpg_core.js"));

    // Boxed sub-case: zero visible markers, yet a www folder sits next to a
    // huge exe -> assets are embedded; stream-scan the exe for js markers.
    if (!pkg_root && !pkg_www && !idx_root && !idx_www && !mz && !mv) {
        if (!has_www) return false;
        std::vector<DirEntry> entries;
        list_dir(dir, &entries);
        std::wstring big;
        uint64_t big_size = 0;
        for (const auto& e : entries) {
            if (e.is_dir || !ends_with_ci(e.name, L".exe")) continue;
            if (e.size >= 50ull * 1024 * 1024 && e.size > big_size) {
                big = join_path(dir, e.name);
                big_size = e.size;
            }
        }
        if (big.empty()) return false;
        static const char* toks[] = { "rmmz_core.js", "rpg_core.js" };
        int t = scan_tokens(big, toks, 2);
        if (t < 0) return false;
        res->engine = (t == 0) ? RMD_ENGINE_RMMZ : RMD_ENGINE_RMMV;
        res->layout = RMD_LAYOUT_BOXED;
        res->kind = RMD_KIND_GAME;
        copy_char(res->runtime_utf8, sizeof(res->runtime_utf8), "boxed");
        ev_add(ev, "nwjs");
        ev_add(ev, "boxed");
        ev_add(ev, std::string("js=") + toks[t]);
        char num[40];
        snprintf(num, sizeof(num), "exe_size=%llu", (unsigned long long)big_size);
        ev_add(ev, num);
        return true;
    }

    // MZ check first: hybrid deployments ship both marker sets side by side.
    std::string pkg;
    const bool have_pkg = pkg_root || pkg_www;
    if (have_pkg) read_file_text(pkg_root ? join_path(dir, L"package.json")
                                          : join_path(www, L"package.json"), pkg);
    // Both core sets present (e.g. a project converted between versions): the page that actually runs decides.
    // MV's index.html lists rpg_core.js directly; MZ's only loads main.js (which pulls in rmmz_*.js).
    bool mz_runs = mz;
    if (mz && mv && (idx_www || idx_root)) {
        std::string idx;
        read_file_text(idx_www ? join_path(www, L"index.html") : join_path(dir, L"index.html"), idx);
        if (idx.find("rpg_core.js") != std::string::npos && idx.find("rmmz_core.js") == std::string::npos) {
            mz_runs = false;
            ev_add(ev, "index=rpg_core.js");
        }
    }
    if (mz_runs) {
        res->engine = RMD_ENGINE_RMMZ;
        ev_add(ev, "js=rmmz_core.js");
    } else if (mv) {
        res->engine = RMD_ENGINE_RMMV;
        ev_add(ev, "js=rpg_core.js");
    } else if (have_pkg) {
        std::string name, main_entry;
        json_get_string(pkg, "name", name);
        json_get_string(pkg, "main", main_entry);
        const std::string name_l = ascii_lower(name);
        if (name_l == "rmmz-game") {
            res->engine = RMD_ENGINE_RMMZ;
            ev_add(ev, "name=rmmz-game");
        } else if (name_l == "kadokawa/rpgmv") {
            res->engine = RMD_ENGINE_RMMV;
            ev_add(ev, "name=kadokawa-rpgmv");
        } else if (main_entry.find("www/") != std::string::npos) {
            res->engine = RMD_ENGINE_RMMV;
            ev_add(ev, "main=www");
        } else {
            return false;
        }
    } else {
        return false;
    }

    ev_add(ev, pkg_root || pkg_www ? "pkg=yes" : "pkg=no");
    if (idx_root || idx_www) ev_add(ev, "html=yes");
    ev_add(ev, has_www ? "base=www" : "base=root");

    res->layout = has_www ? RMD_LAYOUT_NWJS_WWW : RMD_LAYOUT_NWJS_ROOT;
    res->kind = RMD_KIND_GAME;

    // Runtime flavor: modern NW.js ships nw.dll/node.dll, legacy builds carry
    // nw.pak/pdf.dll/ffmpegsumo.dll instead.
    const char* rt = nullptr;
    if (file_exists(join_path(dir, L"nw.dll")) || file_exists(join_path(dir, L"node.dll")))
        rt = "NW.js";
    else if (file_exists(join_path(dir, L"nw.pak")) || file_exists(join_path(dir, L"pdf.dll")) ||
             file_exists(join_path(dir, L"ffmpegsumo.dll")))
        rt = "NW.js(legacy)";
    if (rt) {
        copy_char(res->runtime_utf8, sizeof(res->runtime_utf8), rt);
        ev_add(ev, strcmp(rt, "NW.js") == 0 ? "rt=nw.js" : "rt=nw.js_legacy");
    }

    // Title: System.json gameTitle, then package.json window.title, then name.
    std::string t;
    std::string sj;
    if (read_file_text(join_path(base, L"data\\System.json"), sj))
        json_get_string(sj, "gameTitle", t);
    if (t.empty() && have_pkg) {
        json_get_string(pkg, "title", t);
        if (t.empty()) json_get_string(pkg, "name", t);
    }
    copy_char(res->title_utf8, sizeof(res->title_utf8), t);
    return true;
}

/* ---------------- tier 2: Game.ini family (XP / VX / VX Ace) ---------------- */

bool detect_game_ini(const std::wstring& dir, RMD_RESULT* res, std::string& ev) {
    const std::wstring ini = join_path(dir, L"Game.ini");
    if (!file_exists(ini)) return false;

    std::string lib, scripts, title_raw;
    ini_get(ini, "Game", "Library", lib);
    ini_get(ini, "Game", "Scripts", scripts);
    ini_get(ini, "Game", "Title", title_raw);

    // Generation from Library substring; RGSS3 must win over RGSS2/RGSS1
    // because values like RGSS300.dll share prefixes with older generations.
    const std::string lib_l = ascii_lower(lib);
    int32_t eng = RMD_ENGINE_UNKNOWN;
    if (contains(lib_l, "rgss3")) eng = RMD_ENGINE_RMVXACE;
    else if (contains(lib_l, "rgss20") || contains(lib_l, "rgss2") ||
             contains(lib_l, "202") || contains(lib_l, "200j")) eng = RMD_ENGINE_RMVX;
    else if (contains(lib_l, "rgss10") || contains(lib_l, "rgss1")) eng = RMD_ENGINE_RMXP;

    // Cross-check via Scripts extension (.rvdata2 before .rvdata: prefix!).
    std::string scripts_ext;
    if (!scripts.empty()) {
        size_t dot = scripts.find_last_of('.');
        if (dot != std::string::npos) {
            scripts_ext = ascii_lower(scripts.substr(dot));
            if (scripts_ext == ".rxdata") { if (eng == RMD_ENGINE_UNKNOWN) eng = RMD_ENGINE_RMXP; }
            else if (scripts_ext == ".rvdata2") { if (eng == RMD_ENGINE_UNKNOWN) eng = RMD_ENGINE_RMVXACE; }
            else if (scripts_ext == ".rvdata") { if (eng == RMD_ENGINE_UNKNOWN) eng = RMD_ENGINE_RMVX; }
            else scripts_ext.clear();
        }
    }

    // Matching-generation archive decides layout; anything else stays loose.
    const wchar_t* arc = nullptr;
    if (eng == RMD_ENGINE_RMXP && file_exists(join_path(dir, L"Game.rgssad"))) arc = L"Game.rgssad";
    else if (eng == RMD_ENGINE_RMVX && file_exists(join_path(dir, L"Game.rgss2a"))) arc = L"Game.rgss2a";
    else if (eng == RMD_ENGINE_RMVXACE && file_exists(join_path(dir, L"Game.rgss3a"))) arc = L"Game.rgss3a";

    res->engine = eng;
    res->layout = arc ? RMD_LAYOUT_ARCHIVE : RMD_LAYOUT_LOOSE;
    res->kind = RMD_KIND_GAME;
    copy_char(res->title_utf8, sizeof(res->title_utf8), ansi_to_utf8(title_raw.c_str()));
    copy_char(res->runtime_utf8, sizeof(res->runtime_utf8), ansi_to_utf8(lib.c_str()));

    ev_add(ev, "game_ini");
    if (!lib.empty()) {
        // Values may carry a path prefix (System\RGSS301.dll): report the bare
        // dll name plus an explicit path_prefix flag, never a raw path.
        size_t sep = lib.find_last_of("/\\");
        if (sep != std::string::npos) ev_add(ev, "path_prefix=yes");
        std::string base = sanitize_token(sep == std::string::npos ? lib : lib.substr(sep + 1));
        if (!base.empty()) ev_add(ev, "lib=" + base);
    }
    if (!scripts_ext.empty()) ev_add(ev, "scripts=" + scripts_ext);
    ev_add(ev, arc ? "archive=" + sanitize_token(wide_to_utf8(arc)) : "archive=loose");

    // Data-like directory evidence (name match is loose on purpose: some games
    // ship "Copy of Data" instead of Data).
    std::vector<DirEntry> entries;
    list_dir(dir, &entries);
    for (const auto& e : entries) {
        if (!e.is_dir) continue;
        std::string low = ascii_lower(wide_to_utf8(e.name.c_str()));
        if (low.find("data") != std::string::npos) { ev_add(ev, "data_dir"); break; }
    }
    return true;
}

/* ---------------- tier 3: RPG_RT.ini family (2000 / 2003) ---------------- */

bool detect_rpg_rt(const std::wstring& dir, RMD_RESULT* res, std::string& ev) {
    const std::wstring ini = join_path(dir, L"RPG_RT.ini");
    if (!file_exists(ini)) return false;

    std::vector<DirEntry> entries;
    list_dir(dir, &entries);

    // Any *.ldb opens this branch (standard name RPG_RT.ldb, renames accepted).
    bool have_ldb = false, have_lmt = false;
    for (const auto& e : entries) {
        if (e.is_dir) continue;
        if (!have_ldb && ends_with_ci(e.name, L".ldb")) have_ldb = true;
        if (!have_lmt && ends_with_ci(e.name, L".lmt")) have_lmt = true;
    }
    if (!have_ldb) return false;

    // Exe discovery: standard name, French-localized clone, then any *.exe
    // (one sample ships the engine renamed to Treaster.exe).
    std::wstring exe, exe_name;
    static const wchar_t* preferred[] = { L"RPG_RT.exe", L"Sauvegarde_RPG_RT.exe" };
    for (const wchar_t* p : preferred) {
        if (file_exists(join_path(dir, p))) { exe_name = p; break; }
    }
    if (exe_name.empty()) {
        for (const auto& e : entries) {
            if (!e.is_dir && ends_with_ci(e.name, L".exe")) { exe_name = e.name; break; }
        }
    }
    exe = exe_name.empty() ? std::wstring() : join_path(dir, exe_name);

    res->engine = RMD_ENGINE_RM2000;
    bool up = false;

    // 2003 upgrade marker directories (case-insensitive existence probes).
    static const wchar_t* upgrade_dirs[] = { L"System2", L"BattleCharSet", L"BattleWeapon", L"Frame", L"Battle2" };
    bool up_dir = false;
    for (const wchar_t* d : upgrade_dirs) {
        if (dir_exists(join_path(dir, d))) { up_dir = true; break; }
    }
    if (up_dir) up = true;

    uint64_t exe_size = 0;
    char ver[32] = "";
    if (!exe.empty()) {
        for (const auto& e : entries) {
            if (!e.is_dir && wi_eq(e.name, exe_name)) { exe_size = e.size; break; }
        }
        if (exe_size > 900000) up = true;   // bloated 2003-era engine binary
        if (exe_file_version(exe, ver, sizeof(ver))) {
            // 1.0.9.x / 1.0.6.x engines are RM2003; plain 1.0.x stays RM2000.
            if (strncmp(ver, "1.0.9.", 6) == 0 || strncmp(ver, "1.0.6.", 6) == 0) up = true;
        }
    }
    if (up) res->engine = RMD_ENGINE_RM2003;

    res->layout = RMD_LAYOUT_LOOSE;
    res->kind = RMD_KIND_GAME;
    std::string t;
    ini_get(ini, "RPG_RT", "GameTitle", t);
    copy_char(res->title_utf8, sizeof(res->title_utf8), ansi_to_utf8(t.c_str()));
    if (!exe_name.empty())
        copy_char(res->runtime_utf8, sizeof(res->runtime_utf8), wide_to_utf8(exe_name.c_str()));

    ev_add(ev, "rpg_rt_ini");
    ev_add(ev, "ldb");
    if (have_lmt) ev_add(ev, "lmt");
    if (up_dir) ev_add(ev, "system2");
    if (exe_size > 0) {
        char num[40];
        snprintf(num, sizeof(num), "exe_size=%llu", (unsigned long long)exe_size);
        ev_add(ev, num);
    }
    if (ver[0]) ev_add(ev, std::string("exe_ver=") + ver);
    return true;
}

/* ---------------- tier 4: recursive data-extension sweep ---------------- */

struct SweepCounts {
    int rxdata = 0, rvdata = 0, rvdata2 = 0, lmu = 0;
    bool lmt = false, ldb = false;
};

void sweep_walk(const std::wstring& dir, int depth, SweepCounts* c) {
    if (depth > 4) return;
    std::vector<DirEntry> entries;
    list_dir(dir, &entries);
    for (const auto& e : entries) {
        if (e.is_dir) {
            sweep_walk(join_path(dir, e.name), depth + 1, c);
            continue;
        }
        size_t dot = e.name.find_last_of(L'.');
        if (dot == std::wstring::npos) continue;
        const wchar_t* ext = e.name.c_str() + dot;
        if (wi_eq(ext, L".rxdata")) c->rxdata++;
        else if (wi_eq(ext, L".rvdata2")) c->rvdata2++;
        else if (wi_eq(ext, L".rvdata")) c->rvdata++;
        else if (wi_eq(ext, L".lmu")) c->lmu++;
        else if (wi_eq(ext, L".lmt")) c->lmt = true;
        else if (wi_eq(ext, L".ldb")) c->ldb = true;
    }
}

bool detect_sweep(const std::wstring& dir, RMD_RESULT* res, std::string& ev) {
    SweepCounts c;
    sweep_walk(dir, 1, &c);

    const char* ext = nullptr;
    int cnt = 0;
    if (c.rxdata >= 5) { res->engine = RMD_ENGINE_RMXP; ext = "rxdata"; cnt = c.rxdata; }
    else if (c.rvdata2 >= 5) { res->engine = RMD_ENGINE_RMVXACE; ext = "rvdata2"; cnt = c.rvdata2; }
    else if (c.rvdata >= 5 && c.rvdata2 < 5) { res->engine = RMD_ENGINE_RMVX; ext = "rvdata"; cnt = c.rvdata; }
    else if (c.lmu >= 10 && (c.lmt || c.ldb)) { res->engine = RMD_ENGINE_RM2000; ext = "lmu"; cnt = c.lmu; }
    else return false;

    res->layout = RMD_LAYOUT_LOOSE;
    res->kind = RMD_KIND_GAME;
    ev_add(ev, "sweep");
    char num[40];
    snprintf(num, sizeof(num), "%s=%d", ext, cnt);
    ev_add(ev, num);
    return true;
}

/* ---------------- tier 5: bare RTP resource roots ---------------- */

bool detect_rtp(const std::wstring& dir, RMD_RESULT* res, std::string& ev) {
    std::vector<DirEntry> entries;
    list_dir(dir, &entries);

    // Gate: a resource root has none of the game markers, and the only exe it
    // may carry is an uninstaller stub (unins000.exe; Game.ico is not an exe).
    if (file_exists(join_path(dir, L"package.json")) ||
        file_exists(join_path(dir, L"www\\package.json")) ||
        file_exists(join_path(dir, L"index.html")) ||
        file_exists(join_path(dir, L"www\\index.html")) ||
        dir_exists(join_path(dir, L"js")) || dir_exists(join_path(dir, L"www")) ||
        dir_exists(join_path(dir, L"Data")))
        return false;
    for (const auto& e : entries) {
        if (e.is_dir) continue;
        if (ends_with_ci(e.name, L".ldb") || ends_with_ci(e.name, L".lmu")) return false;
        if (ends_with_ci(e.name, L".exe") && !wi_eq(e.name, L"unins000.exe")) return false;
    }

    // Delphi-era sets: a dense cluster of classic asset folders.
    static const wchar_t* delphi_dirs[] = {
        L"Backdrop", L"Battle", L"CharSet", L"ChipSet", L"FaceSet", L"GameOver",
        L"Monster", L"Music", L"Panorama", L"Picture", L"Sound", L"System", L"Title",
    };
    int hits = 0;
    std::string names;
    for (const wchar_t* d : delphi_dirs) {
        bool found = false;
        for (const auto& e : entries)
            if (e.is_dir && wi_eq(e.name, d)) { found = true; break; }
        if (found) {
            hits++;
            if (!names.empty()) names += ',';
            names += sanitize_token(wide_to_utf8(d));
        }
    }
    if (hits >= 8) {
        static const wchar_t* up2003[] = { L"System2", L"BattleCharSet", L"Battle2", L"BattleWeapon", L"Frame" };
        bool up = false;
        for (const wchar_t* d : up2003) {
            for (const auto& e : entries)
                if (e.is_dir && wi_eq(e.name, d)) { up = true; break; }
            if (up) break;
        }
        res->engine = up ? RMD_ENGINE_RM2003 : RMD_ENGINE_RM2000;
        res->layout = RMD_LAYOUT_LOOSE;
        res->kind = RMD_KIND_RTP;
        char num[40];
        snprintf(num, sizeof(num), "rtp_dirs=%d", hits);
        ev_add(ev, num);
        ev_add(ev, "dirs=" + names);
        return true;
    }

    // RGSS-era sets: Audio+Graphics pair, discriminated by Graphics subfolders.
    // Order is strict: Ace markers, then VX, then XP.
    if (dir_exists(join_path(dir, L"Audio")) && dir_exists(join_path(dir, L"Graphics"))) {
        struct MarkerSet { RMD_ENGINE eng; const wchar_t* const* subs; size_t n; };
        static const wchar_t* ace_subs[] = { L"Battlebacks1", L"Titles1", L"SV_Actors" };
        static const wchar_t* vx_subs[] = { L"Parallaxes", L"Faces" };
        static const wchar_t* xp_subs[] = { L"Windowskins", L"Transitions", L"Panoramas" };
        const MarkerSet sets[] = {
            { RMD_ENGINE_RMVXACE, ace_subs, 3 },
            { RMD_ENGINE_RMVX, vx_subs, 2 },
            { RMD_ENGINE_RMXP, xp_subs, 3 },
        };
        for (const auto& s : sets) {
            for (size_t i = 0; i < s.n; i++) {
                if (dir_exists(join_path(join_path(dir, L"Graphics"), s.subs[i]))) {
                    res->engine = s.eng;
                    res->layout = RMD_LAYOUT_LOOSE;
                    res->kind = RMD_KIND_RTP;
                    ev_add(ev, "rtp_rgss");
                    ev_add(ev, "marker=" + sanitize_token(wide_to_utf8(s.subs[i])));
                    return true;
                }
            }
        }
    }
    return false;
}

} // namespace

/* ---------------- exported API ---------------- */

RMD_EXPORT int32_t rmd_detect(const wchar_t* dir_utf16, RMD_RESULT* out) {
    if (!dir_utf16 || !out) return RMD_ERR_INVALID;
    if (out->size != sizeof(RMD_RESULT)) return RMD_ERR_INVALID;
    try {
        if (!dir_exists(dir_utf16)) return RMD_ERR_NOTDIR;

        // Deterministic baseline on every path before any tier runs.
        out->engine = RMD_ENGINE_UNKNOWN;
        out->layout = RMD_LAYOUT_UNKNOWN;
        out->kind = RMD_KIND_UNKNOWN;
        out->title_utf8[0] = '\0';
        out->runtime_utf8[0] = '\0';
        out->detail_utf8[0] = '\0';
        copy_wide(out->dir, sizeof(out->dir) / sizeof(out->dir[0]), dir_utf16);

        const std::wstring dir(dir_utf16);
        std::string ev;
        if (!detect_nwjs(dir, out, ev) &&
            !detect_game_ini(dir, out, ev) &&
            !detect_rpg_rt(dir, out, ev) &&
            !detect_sweep(dir, out, ev) &&
            !detect_rtp(dir, out, ev)) {
            ev = "no_signature";   // tier 6: nothing recognizable
        }
        copy_char(out->detail_utf8, sizeof(out->detail_utf8), ev);
        return RMD_OK;
    } catch (...) {
        // No exception ever crosses the C boundary.
        return RMD_ERR_INVALID;
    }
}

RMD_EXPORT const char* rmd_engine_name(int32_t engine) {
    switch (engine) {
    case RMD_ENGINE_RM2000:  return "RPG Maker 2000";
    case RMD_ENGINE_RM2003:  return "RPG Maker 2003";
    case RMD_ENGINE_RMXP:    return "RPG Maker XP";
    case RMD_ENGINE_RMVX:    return "RPG Maker VX";
    case RMD_ENGINE_RMVXACE: return "RPG Maker VX Ace";
    case RMD_ENGINE_RMMV:    return "RPG Maker MV";
    case RMD_ENGINE_RMMZ:    return "RPG Maker MZ";
    default:                 return "Unknown";
    }
}

RMD_EXPORT const char* rmd_layout_name(int32_t layout) {
    switch (layout) {
    case RMD_LAYOUT_LOOSE:     return "loose";
    case RMD_LAYOUT_ARCHIVE:   return "archive";
    case RMD_LAYOUT_NWJS_WWW:  return "nwjs-www";
    case RMD_LAYOUT_NWJS_ROOT: return "nwjs-flat";
    case RMD_LAYOUT_BOXED:     return "boxed";
    default:                   return "unknown";
    }
}

RMD_EXPORT const char* rmd_kind_name(int32_t kind) {
    switch (kind) {
    case RMD_KIND_GAME: return "game";
    case RMD_KIND_RTP:  return "rtp";
    default:            return "unknown";
    }
}
