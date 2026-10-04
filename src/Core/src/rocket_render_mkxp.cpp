// rocket_render_mkxp.cpp - RocketRenderMKXP Data-Oriented Native Rendering Backend for mkxp-z
#include "util.hpp"
#include "rocket_render_mkxp.h"
#include <d3d11.h>
#include <dxgi.h>
#include <vector>
#include <string>
#include <thread>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <chrono>
#include <tlhelp32.h>

namespace rr {

// Data-Oriented Runtime Struct for MKXP-Z native engine instance
struct MkxpRuntime {
    rp_mkxp_config       cfg;
    CRITICAL_SECTION     cs;
    PROCESS_INFORMATION  pi;
    HANDLE               job = nullptr;
    HWND                 game_hwnd = nullptr;
    HWND                 parent_hwnd = nullptr;
    std::atomic<bool>    running{false};
    std::atomic<bool>    stop_requested{false};
    std::atomic<uint64_t> total_frames{0};
    double               fps = 0.0;
    
    // D3D11 Shared Texture Pipeline
    ID3D11Device*        d3d_device = nullptr;
    ID3D11DeviceContext* d3d_context = nullptr;
    ID3D11Texture2D*     shared_texture = nullptr;
    HANDLE               shared_handle = nullptr;
    
    // CPU pixel buffer for zero-stall readback
    std::vector<uint8_t> pixel_cache;
    uint32_t             width = 0;
    uint32_t             height = 0;
    
    std::thread          monitor_thread;
    std::wstring         generated_json_path;
};

// Check if a file exists
static bool file_exists_w(const std::wstring& path) {
    DWORD attr = GetFileAttributesW(path.c_str());
    return (attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY));
}

extern "C" IMAGE_DOS_HEADER __ImageBase;

static std::wstring current_module_path() {
    wchar_t buf[MAX_PATH]{};
    GetModuleFileNameW((HMODULE)&__ImageBase, buf, MAX_PATH);
    return buf;
}

// Find mkxp-z executable from candidate paths
static std::wstring resolve_mkxp_executable(const rp_mkxp_config* cfg) {
    if (cfg && cfg->custom_exe[0]) {
        if (file_exists_w(cfg->custom_exe)) return cfg->custom_exe;
        return L"";
    }
    
    // 1. Check game directory
    if (cfg && cfg->game_dir[0]) {
        std::wstring gdir = cfg->game_dir;
        std::wstring cand1 = join_path(gdir, L"mkxp-z.exe");
        if (file_exists_w(cand1)) return cand1;
        std::wstring cand2 = join_path(gdir, L"mkxp.exe");
        if (file_exists_w(cand2)) return cand2;
        std::wstring cand3 = join_path(gdir, L"RocketRenderMKXP.exe");
        if (file_exists_w(cand3)) return cand3;
    }
    
    const wchar_t* exe_names[] = {
        L"mkxp-z.exe",
        L"RocketRenderMKXP.exe",
        L"mkxp.exe"
    };

    // 2. Check module directory and parent subdirectories (up to 7 levels up)
    std::wstring mod = current_module_path();
    size_t pos = mod.find_last_of(L"\\/");
    if (pos != std::wstring::npos) {
        std::wstring cur_dir = mod.substr(0, pos + 1);
        for (int depth = 0; depth < 7 && !cur_dir.empty(); depth++) {
            for (const auto* name : exe_names) {
                std::wstring cands[] = {
                    cur_dir + name,
                    cur_dir + L"runtimes\\mkxp-z\\" + name,
                    cur_dir + L"dist\\portable\\runtimes\\mkxp-z\\" + name
                };
                for (const auto& c : cands) {
                    if (file_exists_w(c)) return c;
                }
            }
            size_t p = cur_dir.find_last_of(L"\\/", cur_dir.length() > 1 ? cur_dir.length() - 2 : std::wstring::npos);
            if (p == std::wstring::npos) break;
            cur_dir = cur_dir.substr(0, p + 1);
        }
    }

    // 2b. Check process directory (main exe dir) and parents (up to 7 levels up)
    wchar_t proc_buf[MAX_PATH]{};
    if (GetModuleFileNameW(nullptr, proc_buf, MAX_PATH) > 0) {
        std::wstring proc_path(proc_buf);
        size_t ppos = proc_path.find_last_of(L"\\/");
        if (ppos != std::wstring::npos) {
            std::wstring cur_pdir = proc_path.substr(0, ppos + 1);
            for (int depth = 0; depth < 7 && !cur_pdir.empty(); depth++) {
                for (const auto* name : exe_names) {
                    std::wstring pcands[] = {
                        cur_pdir + name,
                        cur_pdir + L"runtimes\\mkxp-z\\" + name,
                        cur_pdir + L"dist\\portable\\runtimes\\mkxp-z\\" + name
                    };
                    for (const auto& c : pcands) {
                        if (file_exists_w(c)) return c;
                    }
                }
                size_t p = cur_pdir.find_last_of(L"\\/", cur_pdir.length() > 1 ? cur_pdir.length() - 2 : std::wstring::npos);
                if (p == std::wstring::npos) break;
                cur_pdir = cur_pdir.substr(0, p + 1);
            }
        }
    }
    
    // 3. Check build/dist/runtimes directories from CWD
    wchar_t cur_dir[MAX_PATH]{};
    GetCurrentDirectoryW(MAX_PATH, cur_dir);
    std::wstring root(cur_dir);
    std::wstring cands[] = {
        root + L"\\dist\\portable\\runtimes\\mkxp-z\\mkxp-z.exe",
        root + L"\\dist\\portable\\runtimes\\mkxp-z\\RocketRenderMKXP.exe",
        root + L"\\dist\\portable\\mkxp-z.exe",
        root + L"\\dist\\portable\\RocketRenderMKXP.exe",
        root + L"\\runtimes\\mkxp-z\\mkxp-z.exe",
        root + L"\\runtimes\\mkxp-z\\RocketRenderMKXP.exe",
        root + L"\\build\\core\\runtimes\\mkxp-z\\mkxp-z.exe",
        root + L"\\build\\core\\mkxp-z.exe",
        root + L"\\build\\core\\RocketRenderMKXP.exe"
    };
    for (const auto& c : cands) {
        if (file_exists_w(c)) return c;
    }
    
    // 4. Check AppData / LocalAppData
    wchar_t env_buf[MAX_PATH]{};
    if (GetEnvironmentVariableW(L"APPDATA", env_buf, MAX_PATH) > 0) {
        std::wstring cand = std::wstring(env_buf) + L"\\RocketRPG\\runtimes\\mkxp-z\\mkxp-z.exe";
        if (file_exists_w(cand)) return cand;
    }
    if (GetEnvironmentVariableW(L"LOCALAPPDATA", env_buf, MAX_PATH) > 0) {
        std::wstring cand = std::wstring(env_buf) + L"\\RocketRPG\\runtimes\\mkxp-z\\mkxp-z.exe";
        if (file_exists_w(cand)) return cand;
    }
    
    // 5. Check PATH environment variable
    wchar_t search_found[MAX_PATH]{};
    if (SearchPathW(nullptr, L"mkxp-z.exe", nullptr, MAX_PATH, search_found, nullptr) > 0) {
        return search_found;
    }
    if (SearchPathW(nullptr, L"mkxp.exe", nullptr, MAX_PATH, search_found, nullptr) > 0) {
        return search_found;
    }

    // 6. Direct native RGSS runner in game directory
    if (cfg && cfg->game_dir[0]) {
        std::wstring candGame = join_path(cfg->game_dir, L"Game.exe");
        if (file_exists_w(candGame)) return candGame;
    }
    
    return L"";
}

// Window enumeration callback to find mkxp-z window by PID
static BOOL CALLBACK find_mkxp_window_proc(HWND hwnd, LPARAM lp) {
    auto* rt = (MkxpRuntime*)lp;
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != rt->pi.dwProcessId) return TRUE;
    if (!IsWindowVisible(hwnd)) return TRUE;
    
    wchar_t cls[64]{};
    GetClassNameW(hwnd, cls, 64);
    if (wcscmp(cls, L"Shell_TrayWnd") == 0 || wcscmp(cls, L"IME") == 0) return TRUE;
    
    RECT r{};
    GetClientRect(hwnd, &r);
    if (r.right >= 100 && r.bottom >= 100) {
        rt->game_hwnd = hwnd;
        return FALSE; // found
    }
    return TRUE;
}

// Background thread monitoring the mkxp-z process and window
static void mkxp_monitor_loop(MkxpRuntime* rt) {
    auto t_prev = std::chrono::steady_clock::now();
    uint64_t last_frame = 0;
    
    while (!rt->stop_requested.load(std::memory_order_relaxed)) {
        if (rt->pi.hProcess) {
            DWORD exit_code = 0;
            if (GetExitCodeProcess(rt->pi.hProcess, &exit_code) && exit_code != STILL_ACTIVE) {
                rt->running.store(false, std::memory_order_release);
                break;
            }
            
            if (!rt->game_hwnd || !IsWindow(rt->game_hwnd)) {
                EnumWindows(find_mkxp_window_proc, (LPARAM)rt);
                if (rt->game_hwnd && IsWindow(rt->game_hwnd) && rt->parent_hwnd && IsWindow(rt->parent_hwnd)) {
                    rpg_mkxp_host_window(rt, rt->parent_hwnd, 0, 0, (int32_t)rt->width, (int32_t)rt->height);
                }
            }
        }
        
        // Frame capture and FPS tracking
        if (rt->game_hwnd && IsWindow(rt->game_hwnd)) {
            // Only perform frame capture if NOT hosted (hosted mode renders directly to native host HWND via OpenGL)
            if (!rt->parent_hwnd) {
                EnterCriticalSection(&rt->cs);
                size_t need = size_t(rt->width) * rt->height * 4;
                if (rt->pixel_cache.size() >= need) {
                    HDC hdc = GetDC(rt->game_hwnd);
                    if (hdc) {
                        HDC mem_dc = CreateCompatibleDC(hdc);
                        BITMAPINFO bi{};
                        bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
                        bi.bmiHeader.biWidth = (LONG)rt->width;
                        bi.bmiHeader.biHeight = -(LONG)rt->height; // top-down
                        bi.bmiHeader.biPlanes = 1;
                        bi.bmiHeader.biBitCount = 32;
                        bi.bmiHeader.biCompression = BI_RGB;
                        void* pBits = nullptr;
                        HBITMAP dib = CreateDIBSection(mem_dc, &bi, DIB_RGB_COLORS, &pBits, nullptr, 0);
                        if (dib && pBits) {
                            HGDIOBJ old_bmp = SelectObject(mem_dc, dib);
                            if (BitBlt(mem_dc, 0, 0, (int)rt->width, (int)rt->height, hdc, 0, 0, SRCCOPY)) {
                                memcpy(rt->pixel_cache.data(), pBits, need);
                                rt->total_frames.fetch_add(1, std::memory_order_relaxed);
                            }
                            SelectObject(mem_dc, old_bmp);
                            DeleteObject(dib);
                        }
                        DeleteDC(mem_dc);
                        ReleaseDC(rt->game_hwnd, hdc);
                    }
                }
                LeaveCriticalSection(&rt->cs);
            } else {
                rt->total_frames.fetch_add(1, std::memory_order_relaxed);
            }
        }
        
        // Compute FPS
        auto t_now = std::chrono::steady_clock::now();
        double elapsed = std::chrono::duration<double>(t_now - t_prev).count();
        if (elapsed >= 0.5) {
            uint64_t curr_frame = rt->total_frames.load(std::memory_order_relaxed);
            EnterCriticalSection(&rt->cs);
            rt->fps = double(curr_frame - last_frame) / elapsed;
            LeaveCriticalSection(&rt->cs);
            last_frame = curr_frame;
            t_prev = t_now;
        }
        
        Sleep(16);
    }
}

// Initialize D3D11 shared texture device
static bool init_d3d11_shared_surface(MkxpRuntime* rt, uint32_t w, uint32_t h) {
    if (rt->d3d_device) return true;
    
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
                                   nullptr, 0, D3D11_SDK_VERSION,
                                   &rt->d3d_device, nullptr, &rt->d3d_context);
    if (FAILED(hr)) {
        hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, flags,
                               nullptr, 0, D3D11_SDK_VERSION,
                               &rt->d3d_device, nullptr, &rt->d3d_context);
    }
    if (FAILED(hr) || !rt->d3d_device) return false;
    
    D3D11_TEXTURE2D_DESC td{};
    td.Width = w;
    td.Height = h;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    td.MiscFlags = D3D11_RESOURCE_MISC_SHARED;
    
    hr = rt->d3d_device->CreateTexture2D(&td, nullptr, &rt->shared_texture);
    if (FAILED(hr) || !rt->shared_texture) return false;
    
    IDXGIResource* res = nullptr;
    hr = rt->shared_texture->QueryInterface(__uuidof(IDXGIResource), (void**)&res);
    if (SUCCEEDED(hr) && res) {
        res->GetSharedHandle(&rt->shared_handle);
        res->Release();
    }
    
    return rt->shared_handle != nullptr;
}

} // namespace rr

// ---------- C-ABI Implementations ----------

MKXP_EXPORT int32_t rpg_mkxp_is_available(void) {
    rp_mkxp_config dummy{};
    std::wstring exe = rr::resolve_mkxp_executable(&dummy);
    return !exe.empty() ? 1 : 0;
}

MKXP_EXPORT int32_t rpg_mkxp_is_available_game(const wchar_t* game_dir) {
    if (!game_dir || !game_dir[0]) return 0;
    DWORD attr = GetFileAttributesW(game_dir);
    if (attr == INVALID_FILE_ATTRIBUTES || !(attr & FILE_ATTRIBUTE_DIRECTORY)) {
        return 0;
    }
    rp_mkxp_config cfg{};
    wcsncpy_s(cfg.game_dir, game_dir, _TRUNCATE);
    std::wstring exe = rr::resolve_mkxp_executable(&cfg);
    return (!exe.empty() && rr::file_exists_w(exe)) ? 1 : 0;
}

MKXP_EXPORT int32_t rpg_mkxp_generate_config(const rp_mkxp_config* cfg, char* out_json, int32_t cap) {
    if (!cfg || !out_json || cap <= 0) return RP_ERR_INVALID;
    
    // Convert wide path to UTF-8
    char folder_utf8[1024]{};
    if (cfg->game_dir[0]) {
        WideCharToMultiByte(CP_UTF8, 0, cfg->game_dir, -1, folder_utf8, sizeof(folder_utf8) - 1, nullptr, nullptr);
        // Replace backslashes with forward slashes for JSON safety
        for (char* p = folder_utf8; *p; p++) {
            if (*p == '\\') *p = '/';
        }
    }
    
    // Detect Game.ini for Scripts and Title
    std::wstring scripts_w;
    std::wstring title_w;
    if (cfg->game_dir[0]) {
        std::wstring ini_path = rr::join_path(cfg->game_dir, L"Game.ini");
        wchar_t buf[512]{};
        if (GetPrivateProfileStringW(L"Game", L"Scripts", L"", buf, 512, ini_path.c_str()) > 0) {
            scripts_w = buf;
        }
        if (GetPrivateProfileStringW(L"Game", L"Title", L"", buf, 512, ini_path.c_str()) > 0) {
            title_w = buf;
        }
        if (title_w.empty()) {
            std::wstring gd = cfg->game_dir;
            size_t p = gd.find_last_of(L"\\/");
            title_w = (p != std::wstring::npos) ? gd.substr(p + 1) : gd;
        }
        if (scripts_w.empty() || !rr::file_exists_w(rr::join_path(cfg->game_dir, scripts_w))) {
            if (rr::file_exists_w(rr::join_path(cfg->game_dir, L"Data\\Scripts.rxdata")))
                scripts_w = L"Data/Scripts.rxdata";
            else if (rr::file_exists_w(rr::join_path(cfg->game_dir, L"Data\\Scripts.rvdata2")))
                scripts_w = L"Data/Scripts.rvdata2";
            else if (rr::file_exists_w(rr::join_path(cfg->game_dir, L"Data\\Scripts.rvdata")))
                scripts_w = L"Data/Scripts.rvdata";
            else {
                scripts_w = (cfg->rgss_version == 3) ? L"Data/Scripts.rvdata2"
                          : (cfg->rgss_version == 2) ? L"Data/Scripts.rvdata"
                          : L"Data/Scripts.rxdata";
            }
        }
        // Ensure Game.ini has Scripts and Library so mkxp-z / Game.exe never fail
        WritePrivateProfileStringW(L"Game", L"Scripts", scripts_w.c_str(), ini_path.c_str());
        wchar_t lib_buf[128]{};
        if (GetPrivateProfileStringW(L"Game", L"Library", L"", lib_buf, 128, ini_path.c_str()) == 0) {
            const wchar_t* dlib = (cfg->rgss_version == 3) ? L"RGSS301.dll"
                                : (cfg->rgss_version == 2) ? L"RGSS202E.dll"
                                : L"RGSS104E.dll";
            WritePrivateProfileStringW(L"Game", L"Library", dlib, ini_path.c_str());
        }
        if (!title_w.empty()) {
            WritePrivateProfileStringW(L"Game", L"Title", title_w.c_str(), ini_path.c_str());
        }
    } else {
        scripts_w = L"Data/Scripts.rxdata";
        title_w = L"Game";
    }

    char scripts_utf8[512]{};
    char title_utf8[512]{};
    WideCharToMultiByte(CP_UTF8, 0, scripts_w.c_str(), -1, scripts_utf8, sizeof(scripts_utf8) - 1, nullptr, nullptr);
    WideCharToMultiByte(CP_UTF8, 0, title_w.c_str(), -1, title_utf8, sizeof(title_utf8) - 1, nullptr, nullptr);
    for (char* p = scripts_utf8; *p; p++) { if (*p == '\\') *p = '/'; }

    // Resolve runtime exe dir for RTP and SoundFont paths
    std::wstring exe = rr::resolve_mkxp_executable(cfg);
    std::wstring exe_dir;
    size_t last_slash = exe.find_last_of(L"\\/");
    if (last_slash != std::wstring::npos) {
        exe_dir = exe.substr(0, last_slash);
    }
    char rtp_utf8[1024]{};
    if (!exe_dir.empty()) {
        WideCharToMultiByte(CP_UTF8, 0, exe_dir.c_str(), -1, rtp_utf8, sizeof(rtp_utf8) - 1, nullptr, nullptr);
        for (char* p = rtp_utf8; *p; p++) { if (*p == '\\') *p = '/'; }
    }

    char sf_utf8[1024]{};
    if (!exe_dir.empty()) {
        std::wstring sf = exe_dir + L"\\..\\soundfonts\\GeneralUser_GS.sf2";
        if (rr::file_exists_w(sf)) {
            WideCharToMultiByte(CP_UTF8, 0, sf.c_str(), -1, sf_utf8, sizeof(sf_utf8) - 1, nullptr, nullptr);
            for (char* p = sf_utf8; *p; p++) { if (*p == '\\') *p = '/'; }
        }
    }

    std::string sf_line = "";
    if (sf_utf8[0]) {
        sf_line = std::string("  \"midiSoundFont\": \"") + sf_utf8 + "\",\n";
    }

    int n = snprintf(out_json, (size_t)cap,
        "{\n"
        "  \"gameFolder\": \"%s\",\n"
        "  \"iniFileName\": \"Game.ini\",\n"
        "  \"RTP\": [\n"
        "    \"%s\"\n"
        "  ],\n"
        "%s"
        "  \"defScreenW\": %u,\n"
        "  \"defScreenH\": %u,\n"
        "  \"windowTitle\": \"%s\",\n"
        "  \"fixedAspectRatio\": %s,\n"
        "  \"vsync\": %s,\n"
        "  \"winResizable\": %s,\n"
        "  \"rgssVersion\": %d,\n"
        "  \"fontSub\": [\n"
        "    \"Arial>Malgun Gothic\",\n"
        "    \"Times New Roman>Malgun Gothic\",\n"
        "    \"나눔고딕>Malgun Gothic\",\n"
        "    \"NanumGothic>Malgun Gothic\",\n"
        "    \"돋움>Malgun Gothic\",\n"
        "    \"돋움체>Malgun Gothic\",\n"
        "    \"Dotum>Malgun Gothic\",\n"
        "    \"DotumChe>Malgun Gothic\",\n"
        "    \"굴림>Malgun Gothic\",\n"
        "    \"굴림체>Malgun Gothic\",\n"
        "    \"Gulim>Malgun Gothic\",\n"
        "    \"GulimChe>Malgun Gothic\",\n"
        "    \"바탕>Malgun Gothic\",\n"
        "    \"바탕체>Malgun Gothic\",\n"
        "    \"Batang>Malgun Gothic\",\n"
        "    \"BatangChe>Malgun Gothic\",\n"
        "    \"MS Gothic>Malgun Gothic\",\n"
        "    \"MS Mincho>Malgun Gothic\",\n"
        "    \"MS PGothic>Malgun Gothic\",\n"
        "    \"MS PMincho>Malgun Gothic\"\n"
        "  ]\n"
        "}\n",
        folder_utf8[0] ? folder_utf8 : ".",
        rtp_utf8[0] ? rtp_utf8 : ".",
        sf_line.c_str(),
        cfg->screen_width > 0 ? cfg->screen_width : 640,
        cfg->screen_height > 0 ? cfg->screen_height : 480,
        title_utf8[0] ? title_utf8 : "Game",
        cfg->fixed_aspect_ratio ? "true" : "false",
        cfg->vsync ? "true" : "false",
        cfg->win_resizable ? "true" : "false",
        cfg->rgss_version > 0 ? cfg->rgss_version : 1
    );
    
    return (n > 0 && n < cap) ? RP_OK : RP_ERR_IO;
}

MKXP_EXPORT int32_t rpg_mkxp_create(const rp_mkxp_config* cfg, void** out_instance) {
    if (!cfg || !out_instance) return RP_ERR_INVALID;
    
    auto* rt = new rr::MkxpRuntime();
    rt->cfg = *cfg;
    InitializeCriticalSection(&rt->cs);
    rt->width = cfg->screen_width > 0 ? cfg->screen_width : 640;
    rt->height = cfg->screen_height > 0 ? cfg->screen_height : 480;
    
    // Allocate CPU pixel cache
    rt->pixel_cache.resize(size_t(rt->width) * rt->height * 4, 0);
    
    *out_instance = rt;
    return RP_OK;
}

MKXP_EXPORT int32_t rpg_mkxp_start(void* instance) {
    if (!instance) return RP_ERR_INVALID;
    auto* rt = (rr::MkxpRuntime*)instance;
    
    if (rt->running.load(std::memory_order_relaxed)) return RP_ERR_STATE;
    
    // Resolve mkxp-z executable
    std::wstring exe = rr::resolve_mkxp_executable(&rt->cfg);
    if (exe.empty()) {
        return RP_ERR_NOTFOUND;
    }
    
    // Initialize D3D11 shared surface if requested
    if (rt->cfg.use_shared_surface) {
        rr::init_d3d11_shared_surface(rt, rt->width, rt->height);
    }
    
    if (rt->cfg.game_dir[0]) {
        std::wstring exe_dir;
        size_t last_slash = exe.find_last_of(L"\\/");
        if (last_slash != std::wstring::npos) {
            exe_dir = exe.substr(0, last_slash);
        }

        // Ensure game's Fonts directory has Arial.ttf and malgun.ttf
        std::wstring fonts_dir = rr::join_path(rt->cfg.game_dir, L"Fonts");
        CreateDirectoryW(fonts_dir.c_str(), nullptr);

        std::wstring target_arial = fonts_dir + L"\\Arial.ttf";
        std::wstring target_malgun = fonts_dir + L"\\malgun.ttf";

        std::wstring malgun_src = L"C:\\Windows\\Fonts\\malgun.ttf";
        if (!exe_dir.empty() && rr::file_exists_w(exe_dir + L"\\Fonts\\malgun.ttf")) {
            malgun_src = exe_dir + L"\\Fonts\\malgun.ttf";
        }

        if (!rr::file_exists_w(target_malgun) && rr::file_exists_w(malgun_src)) {
            CopyFileW(malgun_src.c_str(), target_malgun.c_str(), FALSE);
        }
        // Ensure Arial.ttf is present with full Korean Hangul glyphs (copied from malgun)
        if (!rr::file_exists_w(target_arial) && rr::file_exists_w(malgun_src)) {
            CopyFileW(malgun_src.c_str(), target_arial.c_str(), FALSE);
        }

        // Write mkxp.json to the game directory ONLY IF it does not already exist
        std::wstring json_path = rr::join_path(rt->cfg.game_dir, L"mkxp.json");
        if (!rr::file_exists_w(json_path)) {
            char json_buf[4096]{};
            if (rpg_mkxp_generate_config(&rt->cfg, json_buf, sizeof(json_buf)) == RP_OK) {
                rr::write_file_text(json_path, json_buf);
                rt->generated_json_path = json_path;
            }
        }

        // Also write to exe directory if different
        if (!exe_dir.empty() && _wcsicmp(exe_dir.c_str(), rt->cfg.game_dir) != 0) {
            std::wstring exe_json_path = rr::join_path(exe_dir, L"mkxp.json");
            if (!rr::file_exists_w(exe_json_path)) {
                char json_buf[4096]{};
                if (rpg_mkxp_generate_config(&rt->cfg, json_buf, sizeof(json_buf)) == RP_OK) {
                    rr::write_file_text(exe_json_path, json_buf);
                }
            }
            std::wstring src_ini = rr::join_path(rt->cfg.game_dir, L"Game.ini");
            if (rr::file_exists_w(src_ini)) {
                CopyFileW(src_ini.c_str(), rr::join_path(exe_dir, L"Game.ini").c_str(), FALSE);
            }
        }
        
        // Create Job object for clean process tree termination
        rt->job = CreateJobObjectW(nullptr, nullptr);
        if (rt->job) {
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION li{};
            li.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            SetInformationJobObject(rt->job, JobObjectExtendedLimitInformation, &li, sizeof(li));
        }
        
        STARTUPINFOW si{};
        si.cb = sizeof(si);
        std::wstring cmd = L"\"" + exe + L"\"";
        std::vector<wchar_t> cmd_buf(cmd.begin(), cmd.end());
        cmd_buf.push_back(0);
        
        BOOL ok = CreateProcessW(exe.c_str(), cmd_buf.data(), nullptr, nullptr, FALSE,
                                 CREATE_SUSPENDED, nullptr, rt->cfg.game_dir, &si, &rt->pi);
        if (!ok || !rt->pi.hProcess) {
            if (rt->job) { CloseHandle(rt->job); rt->job = nullptr; }
            return RP_ERR_IO;
        }
        if (rt->job) AssignProcessToJobObject(rt->job, rt->pi.hProcess);
        ResumeThread(rt->pi.hThread);
    } else {
        return RP_ERR_INVALID;
    }
    
    rt->stop_requested.store(false, std::memory_order_release);
    rt->running.store(true, std::memory_order_release);
    rt->monitor_thread = std::thread(rr::mkxp_monitor_loop, rt);
    
    return RP_OK;
}

MKXP_EXPORT int32_t rpg_mkxp_stop(void* instance) {
    if (!instance) return RP_ERR_INVALID;
    auto* rt = (rr::MkxpRuntime*)instance;
    
    rt->stop_requested.store(true, std::memory_order_release);
    if (rt->monitor_thread.joinable()) {
        rt->monitor_thread.join();
    }
    
    if (rt->pi.hProcess) {
        TerminateProcess(rt->pi.hProcess, 0);
        WaitForSingleObject(rt->pi.hProcess, 1000);
        CloseHandle(rt->pi.hProcess);
        CloseHandle(rt->pi.hThread);
        ZeroMemory(&rt->pi, sizeof(rt->pi));
    }
    
    if (rt->job) {
        CloseHandle(rt->job);
        rt->job = nullptr;
    }
    
    rt->game_hwnd = nullptr;
    rt->parent_hwnd = nullptr;
    rt->running.store(false, std::memory_order_release);
    return RP_OK;
}

MKXP_EXPORT int32_t rpg_mkxp_is_running(void* instance, int32_t* out_running) {
    if (!instance || !out_running) return RP_ERR_INVALID;
    auto* rt = (rr::MkxpRuntime*)instance;
    *out_running = rt->running.load(std::memory_order_relaxed) ? 1 : 0;
    return RP_OK;
}

MKXP_EXPORT int32_t rpg_mkxp_get_frame(void* instance, rp_mkxp_frame* out_frame) {
    if (!instance || !out_frame) return RP_ERR_INVALID;
    auto* rt = (rr::MkxpRuntime*)instance;
    
    ZeroMemory(out_frame, sizeof(*out_frame));
    EnterCriticalSection(&rt->cs);
    out_frame->size = sizeof(rp_mkxp_frame);
    out_frame->width = rt->width;
    out_frame->height = rt->height;
    out_frame->stride_bytes = rt->width * 4;
    out_frame->shared_nt_handle = rt->shared_handle;
    out_frame->fps = rt->fps;
    out_frame->pixel_data = rt->pixel_cache.empty() ? nullptr : rt->pixel_cache.data();
    out_frame->pixel_data_bytes = (uint32_t)rt->pixel_cache.size();
    out_frame->is_native_surface = (rt->parent_hwnd && rt->game_hwnd) ? 1 : 0;
    LeaveCriticalSection(&rt->cs);
    
    out_frame->frame_index = rt->total_frames.load(std::memory_order_relaxed);
    return RP_OK;
}

MKXP_EXPORT int32_t rpg_mkxp_read_pixels(void* instance, void* dst_bgra8, uint32_t cap_bytes) {
    if (!instance || !dst_bgra8) return RP_ERR_INVALID;
    auto* rt = (rr::MkxpRuntime*)instance;
    
    EnterCriticalSection(&rt->cs);
    size_t need = size_t(rt->width) * rt->height * 4;
    if (cap_bytes < need || rt->pixel_cache.size() < need) {
        LeaveCriticalSection(&rt->cs);
        return RP_ERR_INVALID;
    }
    if (rt->parent_hwnd && rt->game_hwnd && IsWindow(rt->game_hwnd)) {
        HDC hdc = GetDC(rt->game_hwnd);
        if (hdc) {
            HDC mem_dc = CreateCompatibleDC(hdc);
            BITMAPINFO bi{};
            bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            bi.bmiHeader.biWidth = (LONG)rt->width;
            bi.bmiHeader.biHeight = -(LONG)rt->height;
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = BI_RGB;
            void* pBits = nullptr;
            HBITMAP dib = CreateDIBSection(mem_dc, &bi, DIB_RGB_COLORS, &pBits, nullptr, 0);
            if (dib && pBits) {
                HGDIOBJ old_bmp = SelectObject(mem_dc, dib);
                if (BitBlt(mem_dc, 0, 0, (int)rt->width, (int)rt->height, hdc, 0, 0, SRCCOPY)) {
                    memcpy(rt->pixel_cache.data(), pBits, need);
                }
                SelectObject(mem_dc, old_bmp);
                DeleteObject(dib);
            }
            DeleteDC(mem_dc);
            ReleaseDC(rt->game_hwnd, hdc);
        }
    }
    memcpy(dst_bgra8, rt->pixel_cache.data(), need);
    LeaveCriticalSection(&rt->cs);
    
    return RP_OK;
}

MKXP_EXPORT int32_t rpg_mkxp_get_hwnd(void* instance, void** out_hwnd) {
    if (!instance || !out_hwnd) return RP_ERR_INVALID;
    auto* rt = (rr::MkxpRuntime*)instance;
    *out_hwnd = (void*)rt->game_hwnd;
    return RP_OK;
}

MKXP_EXPORT int32_t rpg_mkxp_get_pid(void* instance, uint32_t* out_pid) {
    if (!instance || !out_pid) return RP_ERR_INVALID;
    auto* rt = (rr::MkxpRuntime*)instance;
    *out_pid = rt->pi.dwProcessId;
    return RP_OK;
}

MKXP_EXPORT int32_t rpg_mkxp_host_window(void* instance, void* parent_hwnd, int32_t x, int32_t y, int32_t w, int32_t h) {
    if (!instance || !parent_hwnd) return RP_ERR_INVALID;
    auto* rt = (rr::MkxpRuntime*)instance;
    
    HWND parent = (HWND)parent_hwnd;
    rt->parent_hwnd = parent;
    
    if (rt->game_hwnd && IsWindow(rt->game_hwnd)) {
        // Strip top-level borders and styles to host seamlessly
        LONG_PTR style = GetWindowLongPtrW(rt->game_hwnd, GWL_STYLE);
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        style |= WS_CHILD | WS_VISIBLE;
        SetWindowLongPtrW(rt->game_hwnd, GWL_STYLE, style);
        
        SetParent(rt->game_hwnd, parent);
        SetWindowPos(rt->game_hwnd, HWND_TOP, x, y, w, h,
                     SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        return RP_OK;
    }
    
    return RP_OK;
}

MKXP_EXPORT int32_t rpg_mkxp_resize(void* instance, int32_t w, int32_t h) {
    if (!instance || w <= 0 || h <= 0) return RP_ERR_INVALID;
    auto* rt = (rr::MkxpRuntime*)instance;
    
    EnterCriticalSection(&rt->cs);
    rt->width = (uint32_t)w;
    rt->height = (uint32_t)h;
    if (rt->pixel_cache.size() < size_t(w) * h * 4) {
        rt->pixel_cache.resize(size_t(w) * h * 4, 0);
    }
    LeaveCriticalSection(&rt->cs);
    
    if (rt->game_hwnd && IsWindow(rt->game_hwnd)) {
        SetWindowPos(rt->game_hwnd, nullptr, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
    
    return RP_OK;
}

MKXP_EXPORT int32_t rpg_mkxp_send_input(void* instance, uint32_t msg, uintptr_t wparam, intptr_t lparam) {
    if (!instance) return RP_ERR_INVALID;
    auto* rt = (rr::MkxpRuntime*)instance;
    if (rt->game_hwnd && IsWindow(rt->game_hwnd)) {
        PostMessageW(rt->game_hwnd, msg, (WPARAM)wparam, (LPARAM)lparam);
        return RP_OK;
    }
    return RP_ERR_NOTFOUND;
}

MKXP_EXPORT int32_t rpg_mkxp_get_stats(void* instance, rp_mkxp_stats* out_stats) {
    if (!instance || !out_stats) return RP_ERR_INVALID;
    auto* rt = (rr::MkxpRuntime*)instance;
    
    ZeroMemory(out_stats, sizeof(*out_stats));
    out_stats->size = sizeof(rp_mkxp_stats);
    out_stats->pid = rt->pi.dwProcessId;
    out_stats->hwnd = (void*)rt->game_hwnd;
    out_stats->total_rendered_frames = rt->total_frames.load(std::memory_order_relaxed);
    out_stats->is_running = rt->running.load(std::memory_order_relaxed) ? 1 : 0;
    out_stats->is_hosted = (rt->parent_hwnd && rt->game_hwnd) ? 1 : 0;
    
    EnterCriticalSection(&rt->cs);
    out_stats->current_fps = rt->fps;
    LeaveCriticalSection(&rt->cs);
    
    return RP_OK;
}

MKXP_EXPORT void rpg_mkxp_destroy(void* instance) {
    if (!instance) return;
    auto* rt = (rr::MkxpRuntime*)instance;
    
    rpg_mkxp_stop(rt);
    
    EnterCriticalSection(&rt->cs);
    if (rt->shared_texture) { rt->shared_texture->Release(); rt->shared_texture = nullptr; }
    if (rt->d3d_context)    { rt->d3d_context->Release(); rt->d3d_context = nullptr; }
    if (rt->d3d_device)     { rt->d3d_device->Release(); rt->d3d_device = nullptr; }
    LeaveCriticalSection(&rt->cs);
    
    DeleteCriticalSection(&rt->cs);
    delete rt;
}
