#include "util.hpp"

namespace rr {

struct DetectCtx { std::wstring rtp_root; };
static DetectCtx g_ctx;

void detect_set_rtp_root(const wchar_t* root) { if (root) g_ctx.rtp_root = root; }

static bool has_file(const std::wstring& dir, const wchar_t* name) {
    return file_exists(join_path(dir, name));
}
static bool has_dir(const std::wstring& dir, const wchar_t* name) {
    return dir_exists(join_path(dir, name));
}

static void set_title_from_json(rp_game_info* gi, const std::string& json) {
    std::string t;
    if (json_find_string(json, "gameTitle", t)) strncpy_s(gi->title_utf8, t.c_str(), _TRUNCATE);
}

std::wstring exe_of(const std::wstring& dir) {
    if (file_exists(join_path(dir, L"RPG_RT.exe"))) return join_path(dir, L"RPG_RT.exe");
    if (file_exists(join_path(dir, L"Game.exe")))   return join_path(dir, L"Game.exe");
    if (file_exists(join_path(dir, L"Game_boxed.exe"))) return join_path(dir, L"Game_boxed.exe");
    WIN32_FIND_DATAW fd; HANDLE h = FindFirstFileW(join_path(dir, L"*.exe").c_str(), &fd);
    std::wstring r;
    if (h != INVALID_HANDLE_VALUE) { r = join_path(dir, fd.cFileName); FindClose(h); }
    return r;
}

static const void* rr_memmem(const void* hay, size_t hl, const char* nd, size_t nl) {
    if (nl == 0 || hl < nl) return nullptr;
    const uint8_t* h = (const uint8_t*)hay;
    for (size_t i = 0; i + nl <= hl; i++)
        if (h[i] == (uint8_t)nd[0] && memcmp(h + i, nd, nl) == 0) return h + i;
    return nullptr;
}

static int scan_tokens(const std::wstring& path, const char* const* tokens, size_t ntok) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) return -1;
    std::vector<uint8_t> buf(1 << 20);
    std::vector<uint8_t> carry;
    int found = -1;
    DWORD rd = 0;
    LARGE_INTEGER off{}; off.QuadPart = 0;
    OVERLAPPED ov{}; ov.Offset = 0; ov.OffsetHigh = 0;
    while (found < 0 && ReadFile(h, buf.data(), (DWORD)buf.size(), &rd, nullptr) && rd > 0) {
        std::vector<uint8_t> all;
        all.reserve(carry.size() + rd);
        all.insert(all.end(), carry.begin(), carry.end());
        all.insert(all.end(), buf.begin(), buf.begin() + rd);
        for (size_t t = 0; t < ntok && found < 0; t++) {
            if (rr_memmem(all.data(), all.size(), tokens[t], strlen(tokens[t]))) found = (int)t;
        }
        size_t keep = 31;
        carry.assign(all.end() - (all.size() > keep ? keep : all.size()), all.end());
    }
    CloseHandle(h);
    return found;
}

static rp_engine classify_dir(const std::wstring& dir, rp_game_info* gi) {
    ZeroMemory(gi, sizeof(rp_game_info));
    gi->size = sizeof(rp_game_info);
    wcsncpy_s(gi->dir, sizeof(gi->dir) / sizeof(wchar_t), dir.c_str(), _TRUNCATE);

    std::string pkg;
    if (read_file_text(join_path(dir, L"package.json"), pkg)) {
        std::string main_entry;
        json_find_string(pkg, "main", main_entry);
        bool www = main_entry.find("www/") != std::string::npos || has_dir(dir, L"www");
        std::wstring jsbase = www ? join_path(dir, L"www\\js") : join_path(dir, L"js");
        bool mz = has_file(jsbase, L"rmmz_core.js");
        bool mv = has_file(jsbase, L"rpg_core.js");
        // Both core sets present (project converted between versions): the page that actually runs decides.
        // MV's index.html lists rpg_core.js directly; MZ's only loads main.js.
        if (mz && mv) {
            std::string idx;
            std::wstring idx_path = join_path(www ? join_path(dir, L"www") : dir, L"index.html");
            if (read_file_text(idx_path, idx) &&
                idx.find("rpg_core.js") != std::string::npos && idx.find("rmmz_core.js") == std::string::npos)
                mz = false;
        }
        if (mz || mv) {
            gi->layout = www ? RPG_LAYOUT_NWJS_WWW : RPG_LAYOUT_NWJS_ROOT;
            gi->engine = mz ? RPG_ENGINE_RMMZ : RPG_ENGINE_RMMV;
            strcpy_s(gi->exe_name, "Game.exe");
            std::wstring sys = www ? join_path(dir, L"www\\data\\System.json")
                                   : join_path(dir, L"data\\System.json");
            std::string sj;
            if (read_file_text(sys, sj)) set_title_from_json(gi, sj);
            if (gi->title_utf8[0] == 0) {
                std::string pt;
                if (json_find_string(pkg, "title", pt)) strncpy_s(gi->title_utf8, pt.c_str(), _TRUNCATE);
            }
            return (rp_engine)gi->engine;
        }
    }
    if (has_dir(dir, L"www") && (has_file(dir, L"Game_boxed.exe") || has_file(dir, L"Game.exe"))) {
        std::string sj;
        bool have_sys = read_file_text(join_path(dir, L"www\\data\\System.json"), sj);
        bool boxed = has_file(dir, L"Game_boxed.exe") && !have_sys;
        if (have_sys || boxed) {
            long long actors = 0; json_find_int(sj, "actors", actors);
            (void)actors;
            set_title_from_json(gi, sj);
            gi->layout = RPG_LAYOUT_NWJS_WWW;
            if (!boxed)
                gi->engine = file_exists(join_path(dir, L"www\\js\\rmmz_core.js")) ? RPG_ENGINE_RMMZ : RPG_ENGINE_RMMV;
            else {
                static const char* toks[] = {"rmmz_core.js", "rpg_core.js"};
                int t = scan_tokens(join_path(dir, L"Game_boxed.exe"), toks, 2);
                gi->engine = t == 0 ? RPG_ENGINE_RMMZ : RPG_ENGINE_RMMV;
                strcpy_s(gi->exe_name, "Game_boxed.exe");
                return (rp_engine)gi->engine;
            }
            strcpy_s(gi->exe_name, has_file(dir, L"Game_boxed.exe") ? "Game_boxed.exe" : "Game.exe");
            return (rp_engine)gi->engine;
        }
    }

    std::wstring ini = join_path(dir, L"Game.ini");
    if (file_exists(ini)) {
        std::string lib, title;
        ini_get(ini, "Game", "Library", lib);
        ini_get(ini, "Game", "Title", title);
        std::string title_u8 = ansi_to_utf8(title.c_str());
        strncpy_s(gi->title_utf8, title_u8.c_str(), _TRUNCATE);
        strcpy_s(gi->exe_name, "Game.exe");
        strncpy_s(gi->runtime_dll, lib.c_str(), _TRUNCATE);
        if (lib.find("RGSS1") != std::string::npos) gi->engine = RPG_ENGINE_RMXP;
        else if (lib.find("RGSS2") != std::string::npos ||
                 lib.find("200J") != std::string::npos ||
                 lib.find("202") != std::string::npos) gi->engine = RPG_ENGINE_RMVX;
        else if (lib.find("RGSS3") != std::string::npos) gi->engine = RPG_ENGINE_RMVXACE;
        else gi->engine = RPG_ENGINE_UNKNOWN;

        if (gi->engine == RPG_ENGINE_RMXP && has_file(dir, L"Game.rgssad"))  gi->layout = RPG_LAYOUT_ARCHIVE;
        else if (gi->engine == RPG_ENGINE_RMVX && has_file(dir, L"Game.rgss2a")) gi->layout = RPG_LAYOUT_ARCHIVE;
        else if (gi->engine == RPG_ENGINE_RMVXACE && has_file(dir, L"Game.rgss3a")) gi->layout = RPG_LAYOUT_ARCHIVE;
        else gi->layout = RPG_LAYOUT_LOOSE;
        std::string rtp1; ini_get(ini, "Game", "RTP1", rtp1);
        std::string rtpp; ini_get(ini, "Game", "RTP", rtpp);
        gi->has_rtp = (!rtp1.empty() || !rtpp.empty()) ? 1 : 0;
        return (rp_engine)gi->engine;
    }

    std::wstring rini = join_path(dir, L"RPG_RT.ini");
    std::wstring ldb = join_path(dir, L"RPG_RT.ldb");
    if (file_exists(rini) && file_exists(ldb)) {
        gi->engine = RPG_ENGINE_RM2000;
        if (has_dir(dir, L"System2") || has_dir(dir, L"BattleCharSet") || has_dir(dir, L"system2"))
            gi->engine = RPG_ENGINE_RM2003;
        else {
            std::wstring exe = exe_of(dir);
            if (file_exists(exe)) {
                DWORD fh = 0, sz = GetFileVersionInfoSizeW(exe.c_str(), &fh);
                if (sz) {
                    std::vector<uint8_t> buf(sz);
                    if (GetFileVersionInfoW(exe.c_str(), 0, sz, buf.data())) {
                        VS_FIXEDFILEINFO* fi = nullptr; UINT l = 0;
                        if (VerQueryValueW(buf.data(), L"\\", (LPVOID*)&fi, &l) && fi) {
                            char fvs[64]; snprintf(fvs, sizeof(fvs), "%u.%u.%u.%u",
                                HIWORD(fi->dwFileVersionMS), LOWORD(fi->dwFileVersionMS),
                                HIWORD(fi->dwFileVersionLS), LOWORD(fi->dwFileVersionLS));
                            if (strstr(fvs, ".9.1") || strstr(fvs, "1.0.6")) gi->engine = RPG_ENGINE_RM2003;
                        }
                    }
                }
            }
        }
        std::wstring exep = exe_of(dir);
        size_t last_slash = exep.find_last_of(L"\\/");
        std::wstring exe_name_only = (last_slash != std::wstring::npos) ? exep.substr(last_slash + 1) : exep;
        if (exe_name_only.empty()) exe_name_only = L"RPG_RT.exe";
        std::string exe_u8 = wide_to_utf8(exe_name_only.c_str());
        strncpy_s(gi->exe_name, sizeof(gi->exe_name), exe_u8.c_str(), _TRUNCATE);
        std::string t;
        ini_get(rini, "RPG_RT", "GameTitle", t);
        std::string tu8 = ansi_to_utf8(t.c_str());
        strncpy_s(gi->title_utf8, tu8.c_str(), _TRUNCATE);
        std::string fp; ini_get(rini, "RPG_RT", "FullPackageFlag", fp);
        gi->full_package = (fp == "1") ? 1 : 0;
        gi->has_rtp = gi->full_package;
        gi->layout = RPG_LAYOUT_LOOSE;
        return (rp_engine)gi->engine;
    }
    return RPG_ENGINE_UNKNOWN;
}

} // namespace rr

using namespace rr;

RPG_EXPORT rstatus rpg_scan_games(const wchar_t* search_root, wchar_t*** out_dirs, int32_t* out_count) {
    if (!out_dirs || !out_count) RPG_FAIL(RP_ERR_INVALID, "null out");
    *out_dirs = nullptr; *out_count = 0;
    if (!dir_exists(search_root)) RPG_FAIL(RP_ERR_NOTFOUND, "search root missing");
    std::vector<std::wstring> found;
    std::function<void(const std::wstring&, int)> walk = [&](const std::wstring& d, int depth) {
        WIN32_FIND_DATAW fd;
        HANDLE h = FindFirstFileW(join_path(d, L"*").c_str(), &fd);
        if (h == INVALID_HANDLE_VALUE) return;
        do {
            if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
            if (wcscmp(fd.cFileName, L".") == 0 || wcscmp(fd.cFileName, L"..") == 0) continue;
            std::wstring sub = join_path(d, fd.cFileName);
            rp_game_info probe{};
            probe.size = sizeof(probe);
            rp_engine e = rr::classify_dir(sub, &probe);
            if (e != RPG_ENGINE_UNKNOWN) found.push_back(sub);
            else if (depth < 1) walk(sub, depth + 1);
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    };
    walk(search_root, 0);
    if (found.empty()) RPG_OK();
    wchar_t** arr = (wchar_t**)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, found.size() * sizeof(wchar_t*));
    for (size_t i = 0; i < found.size(); i++) {
        arr[i] = (wchar_t*)HeapAlloc(GetProcessHeap(), 0, (found[i].size() + 1) * sizeof(wchar_t));
        wcscpy_s(arr[i], found[i].size() + 1, found[i].c_str());
    }
    *out_dirs = arr; *out_count = (int32_t)found.size();
    RPG_OK();
}

RPG_EXPORT void rpg_free_strings(wchar_t** arr, int32_t count) {
    if (!arr) return;
    for (int i = 0; i < count; i++) if (arr[i]) HeapFree(GetProcessHeap(), 0, arr[i]);
    HeapFree(GetProcessHeap(), 0, arr);
}

RPG_EXPORT rstatus rpg_detect_game(const wchar_t* game_dir, rp_game_info* out_info) {
    if (!game_dir || !out_info) RPG_FAIL(RP_ERR_INVALID, "null args");
    if (!rr::dir_exists(game_dir)) RPG_FAIL(RP_ERR_NOTFOUND, "game dir missing");
    rp_engine e = rr::classify_dir(game_dir, out_info);
    if (e == RPG_ENGINE_UNKNOWN) RPG_FAIL(RP_ERR_UNSUPPORTED, "unrecognized game layout");
    RPG_OK();
}

RPG_EXPORT rstatus rpg_validate_game(const wchar_t* game_dir, char* out_report_utf8, int32_t cap) {
    rp_game_info gi{};
    rstatus st = rpg_detect_game(game_dir, &gi);
    std::string rep;
    rep += "engine=" + std::to_string(st == RP_OK ? gi.engine : 0);
    rep += " layout=" + std::to_string(gi.layout);
    rep += " title=" + json_escape(gi.title_utf8);
    int score = 0;
    switch (gi.engine) {
    case RPG_ENGINE_RM2000: case RPG_ENGINE_RM2003:
        score += file_exists(join_path(game_dir, L"RPG_RT.ldb")) ? 40 : 0;
        score += file_exists(join_path(game_dir, L"RPG_RT.lmt")) ? 30 : 0;
        score += rr::exe_of(game_dir).empty() ? 0 : 30;
        break;
    case RPG_ENGINE_RMXP: case RPG_ENGINE_RMVX: case RPG_ENGINE_RMVXACE:
        score += file_exists(join_path(game_dir, L"Game.ini")) ? 30 : 0;
        score += gi.runtime_dll[0] ? 40 : 0;
        score += (gi.layout == RPG_LAYOUT_ARCHIVE || dir_exists(join_path(game_dir, L"Data"))) ? 30 : 0;
        break;
    case RPG_ENGINE_RMMV: case RPG_ENGINE_RMMZ: {
        bool www = gi.layout == RPG_LAYOUT_NWJS_WWW;
        score += file_exists(join_path(game_dir, L"package.json")) ? 50 : 0;
        score += file_exists(www ? join_path(game_dir, L"www\\data\\System.json")
                                 : join_path(game_dir, L"data\\System.json")) ? 50 : 0;
        break; }
    default: break;
    }
    rep += " integrity=" + std::to_string(score);
    if (out_report_utf8 && cap > 0) strncpy_s(out_report_utf8, cap, rep.c_str(), _TRUNCATE);
    if (st != RP_OK) { set_last_error(st, rep); return st; }
    RPG_OK();
}
