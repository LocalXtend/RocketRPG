// rocket_render_easyrpg.cpp - RocketRenderEasyRPG Data-Oriented Native Rendering Backend for EasyRPG Player
#include "util.hpp"
#include "rocket_render_easyrpg.h"
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

// Data-Oriented Runtime Struct for EasyRPG Player native engine instance
struct EasyRpgRuntime {
    rp_easyrpg_config    cfg;
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

// Find EasyRPG Player executable from candidate paths
static std::wstring resolve_easyrpg_executable(const rp_easyrpg_config* cfg) {
    if (cfg && cfg->custom_exe[0]) {
        if (file_exists_w(cfg->custom_exe)) return cfg->custom_exe;
        return L"";
    }

    const wchar_t* exe_names[] = {
        L"EasyRPG.exe",
        L"easyrpg-player.exe",
        L"Player.exe",
        L"RocketRenderEasyRPG.exe"
    };

    // 1. Check game directory
    if (cfg && cfg->game_dir[0]) {
        std::wstring gdir = cfg->game_dir;
        for (const auto* name : exe_names) {
            std::wstring cand = join_path(gdir, name);
            if (file_exists_w(cand)) return cand;
        }
    }

    // 2. Check module directory and parent subdirectories (up to 7 levels up)
    std::wstring mod = current_module_path();
    size_t pos = mod.find_last_of(L"\\/");
    if (pos != std::wstring::npos) {
        std::wstring cur_dir = mod.substr(0, pos + 1);
        for (int depth = 0; depth < 7 && !cur_dir.empty(); depth++) {
            for (const auto* name : exe_names) {
                std::wstring cands[] = {
                    cur_dir + name,
                    cur_dir + L"runtimes\\easyrpg\\" + name,
                    cur_dir + L"dist\\portable\\runtimes\\easyrpg\\" + name
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
                        cur_pdir + L"runtimes\\easyrpg\\" + name,
                        cur_pdir + L"dist\\portable\\runtimes\\easyrpg\\" + name
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
    std::wstring search_prefixes[] = {
        root + L"\\dist\\portable\\runtimes\\easyrpg\\",
        root + L"\\dist\\portable\\",
        root + L"\\runtimes\\easyrpg\\",
        root + L"\\build\\core\\runtimes\\easyrpg\\",
        root + L"\\build\\core\\"
    };

    for (const auto& prefix : search_prefixes) {
        for (const auto* name : exe_names) {
            std::wstring cand = prefix + name;
            if (file_exists_w(cand)) return cand;
        }
    }

    // 4. Check AppData / LocalAppData
    wchar_t env_buf[MAX_PATH]{};
    if (GetEnvironmentVariableW(L"APPDATA", env_buf, MAX_PATH) > 0) {
        for (const auto* name : exe_names) {
            std::wstring cand = std::wstring(env_buf) + L"\\RocketRPG\\runtimes\\easyrpg\\" + name;
            if (file_exists_w(cand)) return cand;
        }
    }
    if (GetEnvironmentVariableW(L"LOCALAPPDATA", env_buf, MAX_PATH) > 0) {
        for (const auto* name : exe_names) {
            std::wstring cand = std::wstring(env_buf) + L"\\RocketRPG\\runtimes\\easyrpg\\" + name;
            if (file_exists_w(cand)) return cand;
        }
    }

    // 5. Check PATH environment variable
    for (const auto* name : exe_names) {
        wchar_t search_found[MAX_PATH]{};
        if (SearchPathW(nullptr, name, nullptr, MAX_PATH, search_found, nullptr) > 0) {
            return search_found;
        }
    }

    // 6. Direct native RPG_RT runner in game directory (legacy fallback)
    if (cfg && cfg->game_dir[0]) {
        std::wstring candRpgRt = join_path(cfg->game_dir, L"RPG_RT.exe");
        if (file_exists_w(candRpgRt)) return candRpgRt;
    }

    return L"";
}

struct EnumHwndContext {
    DWORD pid;
    HWND  hwnd;
};

static BOOL CALLBACK EnumWindowsCallback(HWND hwnd, LPARAM lParam) {
    auto* ctx = reinterpret_cast<EnumHwndContext*>(lParam);
    DWORD processId = 0;
    GetWindowThreadProcessId(hwnd, &processId);
    if (processId == ctx->pid && IsWindowVisible(hwnd)) {
        wchar_t cls[64]{};
        GetClassNameW(hwnd, cls, 64);
        if (wcscmp(cls, L"Shell_TrayWnd") == 0 || wcscmp(cls, L"IME") == 0) return TRUE;

        RECT r{};
        GetClientRect(hwnd, &r);
        if (r.right >= 50 && r.bottom >= 50) {
            ctx->hwnd = hwnd;
            return FALSE;
        }
    }
    return TRUE;
}

static HWND find_process_window(DWORD pid) {
    EnumHwndContext ctx{ pid, nullptr };
    EnumWindows(EnumWindowsCallback, reinterpret_cast<LPARAM>(&ctx));
    return ctx.hwnd;
}

static void monitor_easyrpg_process(EasyRpgRuntime* rt) {
    if (!rt) return;

    auto t_prev = std::chrono::steady_clock::now();
    uint64_t last_frame = 0;

    while (!rt->stop_requested.load(std::memory_order_relaxed)) {
        if (rt->pi.hProcess) {
            DWORD exitCode = 0;
            if (GetExitCodeProcess(rt->pi.hProcess, &exitCode) && exitCode != STILL_ACTIVE) {
                rt->running.store(false, std::memory_order_release);
                break;
            }

            if (!rt->game_hwnd || !IsWindow(rt->game_hwnd)) {
                HWND detectedHwnd = find_process_window(rt->pi.dwProcessId);
                if (detectedHwnd) {
                    EnterCriticalSection(&rt->cs);
                    rt->game_hwnd = detectedHwnd;
                    if (rt->parent_hwnd && IsWindow(rt->parent_hwnd)) {
                        SetParent(detectedHwnd, rt->parent_hwnd);
                        LONG_PTR style = GetWindowLongPtrW(detectedHwnd, GWL_STYLE);
                        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
                        style |= WS_CHILD;
                        SetWindowLongPtrW(detectedHwnd, GWL_STYLE, style);

                        RECT rc{};
                        GetClientRect(rt->parent_hwnd, &rc);
                        SetWindowPos(detectedHwnd, nullptr, 0, 0, rc.right - rc.left, rc.bottom - rc.top,
                                     SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
                    }
                    LeaveCriticalSection(&rt->cs);
                }
            }
        }

        // Frame capture and FPS tracking. 임베드(parent_hwnd)되어 있으면 게임 창이 직접 그리므로 캡처하지 않습니다
        // (예전에는 16ms마다 GDI로 게임 창을 복사해 CPU/GPU 동기화로 게임이 끊겼음).
        if (rt->parent_hwnd) {
            rt->total_frames.fetch_add(1, std::memory_order_relaxed);
        } else if (rt->game_hwnd && IsWindow(rt->game_hwnd)) {
            EnterCriticalSection(&rt->cs);
            size_t need = size_t(rt->width) * rt->height * 4;
            if (rt->pixel_cache.size() >= need && rt->width > 0 && rt->height > 0) {
                HDC hdc = GetDC(rt->game_hwnd);
                if (hdc) {
                    HDC mem_dc = CreateCompatibleDC(hdc);
                    BITMAPINFO bi{};
                    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
                    bi.bmiHeader.biWidth = rt->width;
                    bi.bmiHeader.biHeight = -(int32_t)rt->height; // top-down
                    bi.bmiHeader.biPlanes = 1;
                    bi.bmiHeader.biBitCount = 32;
                    bi.bmiHeader.biCompression = BI_RGB;
                    void* dib_bits = nullptr;
                    HBITMAP dib = CreateDIBSection(hdc, &bi, DIB_RGB_COLORS, &dib_bits, nullptr, 0);
                    if (dib) {
                        HGDIOBJ old = SelectObject(mem_dc, dib);
                        BitBlt(mem_dc, 0, 0, rt->width, rt->height, hdc, 0, 0, SRCCOPY);
                        if (dib_bits) {
                            memcpy(rt->pixel_cache.data(), dib_bits, need);
                            rt->total_frames.fetch_add(1, std::memory_order_relaxed);
                        }
                        SelectObject(mem_dc, old);
                        DeleteObject(dib);
                    }
                    DeleteDC(mem_dc);
                    ReleaseDC(rt->game_hwnd, hdc);
                }
            }
            LeaveCriticalSection(&rt->cs);
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

        std::this_thread::sleep_for(std::chrono::milliseconds(16));
    }
    rt->running.store(false, std::memory_order_release);
}

} // namespace rr

using namespace rr;

EASYRPG_EXPORT int32_t rpg_easyrpg_is_available(void) {
    rp_easyrpg_config cfg{};
    std::wstring exe = resolve_easyrpg_executable(&cfg);
    return (!exe.empty() && file_exists_w(exe)) ? 1 : 0;
}

EASYRPG_EXPORT int32_t rpg_easyrpg_is_available_game(const wchar_t* game_dir) {
    if (!game_dir || !game_dir[0]) return 0;
    DWORD attr = GetFileAttributesW(game_dir);
    if (attr == INVALID_FILE_ATTRIBUTES || !(attr & FILE_ATTRIBUTE_DIRECTORY)) {
        return 0;
    }
    rp_easyrpg_config cfg{};
    wcsncpy_s(cfg.game_dir, game_dir, _countof(cfg.game_dir) - 1);
    std::wstring exe = resolve_easyrpg_executable(&cfg);
    return (!exe.empty() && file_exists_w(exe)) ? 1 : 0;
}

EASYRPG_EXPORT int32_t rpg_easyrpg_create(const rp_easyrpg_config* cfg, void** out_instance) {
    if (!cfg || !out_instance) return RP_ERR_INVALID;

    auto* rt = new EasyRpgRuntime();
    rt->cfg = *cfg;
    rt->width = cfg->screen_width > 0 ? cfg->screen_width : 320;
    rt->height = cfg->screen_height > 0 ? cfg->screen_height : 240;
    rt->pixel_cache.resize(size_t(rt->width) * rt->height * 4, 0);

    InitializeCriticalSection(&rt->cs);
    memset(&rt->pi, 0, sizeof(rt->pi));

    *out_instance = rt;
    return RP_OK;
}

EASYRPG_EXPORT int32_t rpg_easyrpg_start(void* instance) {
    if (!instance) return RP_ERR_INVALID;
    auto* rt = static_cast<EasyRpgRuntime*>(instance);

    std::wstring exe = resolve_easyrpg_executable(&rt->cfg);
    if (exe.empty() || !file_exists_w(exe)) {
        return RP_ERR_NOTFOUND;
    }

    std::wstring gdir = rt->cfg.game_dir;
    const wchar_t* base = wcsrchr(exe.c_str(), L'\\');
    base = base ? base + 1 : exe.c_str();
    std::wstring cmdline;
    if (_wcsicmp(base, L"RPG_RT.exe") == 0) {
        cmdline = L"\"" + exe + L"\" \"TestPlay\" \"ShowTitle\" \"Window\"";
    } else {
        cmdline = L"\"" + exe + L"\" --project-path \"" + gdir + L"\"";
        if (rt->cfg.fullscreen) {
            cmdline += L" --fullscreen";
        } else {
            cmdline += L" --window";
        }
        if (!rt->cfg.fixed_aspect_ratio) {
            cmdline += L" --stretch";
        } else {
            cmdline += L" --no-stretch";
        }
        if (rt->cfg.smooth_scaling) {
            cmdline += L" --scaling bilinear";
        } else {
            cmdline += L" --scaling nearest";
        }
        if (rt->cfg.vsync) {
            cmdline += L" --vsync";
        } else {
            cmdline += L" --no-vsync";
        }
        // 화면 프레임 상한 (RR_EASYRPG_FPS: 0 = 무제한). 게임 진행은 항상 60 FPS 고정이라 속도는 바뀌지 않습니다.
        wchar_t fps_env[16]{};
        if (GetEnvironmentVariableW(L"RR_EASYRPG_FPS", fps_env, 16) > 0 && fps_env[0]) {
            int fps = _wtoi(fps_env);
            if (fps <= 0) cmdline += L" --no-fps-limit";
            else cmdline += L" --fps-limit " + std::to_wstring(fps);
        }
        // 임베드된 창은 RocketRPG 메뉴를 누를 때마다 포커스를 잃습니다. 기본값(포커스 잃으면 일시 정지)은
        // 게임 루프를 통째로 멈춰 밝기/필터/속도 명령이 적용되지 않으므로 끕니다. 일시 정지는 RocketRPG가 따로 제공합니다.
        cmdline += L" --no-pause-focus-lost";

        // Auto-detect bundled SoundFont
        std::wstring mod = current_module_path();
        size_t ppos = mod.find_last_of(L"\\/");
        if (ppos != std::wstring::npos) {
            std::wstring cur_dir = mod.substr(0, ppos + 1);
            std::wstring sf_cands[] = {
                cur_dir + L"runtimes\\soundfonts\\GeneralUser_GS.sf2",
                cur_dir + L"..\\runtimes\\soundfonts\\GeneralUser_GS.sf2",
                cur_dir + L"..\\..\\runtimes\\soundfonts\\GeneralUser_GS.sf2",
                cur_dir + L"dist\\portable\\runtimes\\soundfonts\\GeneralUser_GS.sf2"
            };
            for (const auto& sf : sf_cands) {
                if (file_exists_w(sf)) {
                    cmdline += L" --soundfont \"" + sf + L"\"";
                    break;
                }
            }
        }

        // In-game font chosen in the launcher (RR_EASYRPG_FONT / RR_EASYRPG_FONT_SIZE). Without one, EasyRPG keeps
        // the game's own look (built-in bitmap fonts, which include Hangul).
        wchar_t font_env[1024]{};
        wchar_t size_env[16]{};
        if (GetEnvironmentVariableW(L"RR_EASYRPG_FONT", font_env, 1024) > 0 && file_exists_w(font_env)) {
            std::wstring font = font_env;
            std::wstring fsize = L"12";
            if (GetEnvironmentVariableW(L"RR_EASYRPG_FONT_SIZE", size_env, 16) > 0 && size_env[0]) fsize = size_env;
            cmdline += L" --font1 \"" + font + L"\" --font1-size " + fsize + L" --font2 \"" + font + L"\" --font2-size " + fsize;
        }
        // Text encoding detected by RocketRPG (EasyRPG's own detector never proposes Korean CP949).
        wchar_t enc_env[32]{};
        if (GetEnvironmentVariableW(L"RR_EASYRPG_ENCODING", enc_env, 32) > 0 && enc_env[0]) {
            cmdline += L" --encoding " + std::wstring(enc_env);
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

    std::vector<wchar_t> cmdBuf(cmdline.begin(), cmdline.end());
    cmdBuf.push_back(L'\0');

    BOOL ok = CreateProcessW(
        exe.c_str(),
        cmdBuf.data(),
        nullptr,
        nullptr,
        FALSE,
        CREATE_SUSPENDED,
        nullptr,
        gdir.empty() ? nullptr : gdir.c_str(),
        &si,
        &rt->pi
    );

    if (!ok || !rt->pi.hProcess) {
        if (rt->job) {
            CloseHandle(rt->job);
            rt->job = nullptr;
        }
        return RP_ERR_IO;
    }

    if (rt->job) {
        AssignProcessToJobObject(rt->job, rt->pi.hProcess);
    }
    ResumeThread(rt->pi.hThread);

    // (예전에는 클래식 모드용 코어 DLL을 Player에 주입했지만, Player가 자체 브리지(rocket_bridge)를 가지므로 하지 않습니다.)

    rt->running.store(true, std::memory_order_release);
    rt->stop_requested.store(false, std::memory_order_release);
    rt->monitor_thread = std::thread(monitor_easyrpg_process, rt);

    return RP_OK;
}

EASYRPG_EXPORT int32_t rpg_easyrpg_stop(void* instance) {
    if (!instance) return RP_ERR_INVALID;
    auto* rt = static_cast<EasyRpgRuntime*>(instance);

    rt->stop_requested.store(true, std::memory_order_release);
    rt->running.store(false, std::memory_order_release);

    if (rt->monitor_thread.joinable()) {
        rt->monitor_thread.join();
    }

    if (rt->pi.hProcess) {
        TerminateProcess(rt->pi.hProcess, 0);
        WaitForSingleObject(rt->pi.hProcess, 500);
        CloseHandle(rt->pi.hProcess);
        CloseHandle(rt->pi.hThread);
        memset(&rt->pi, 0, sizeof(rt->pi));
    }

    if (rt->job) {
        CloseHandle(rt->job);
        rt->job = nullptr;
    }

    return RP_OK;
}

EASYRPG_EXPORT int32_t rpg_easyrpg_is_running(void* instance, int32_t* out_running) {
    if (!instance || !out_running) return RP_ERR_INVALID;
    auto* rt = static_cast<EasyRpgRuntime*>(instance);
    *out_running = rt->running.load() ? 1 : 0;
    return RP_OK;
}

EASYRPG_EXPORT int32_t rpg_easyrpg_get_frame(void* instance, rp_easyrpg_frame* out_frame) {
    if (!instance || !out_frame) return RP_ERR_INVALID;
    auto* rt = static_cast<EasyRpgRuntime*>(instance);

    EnterCriticalSection(&rt->cs);
    out_frame->size = sizeof(rp_easyrpg_frame);
    out_frame->width = rt->width > 0 ? rt->width : 320;
    out_frame->height = rt->height > 0 ? rt->height : 240;
    out_frame->stride_bytes = out_frame->width * 4;
    out_frame->frame_index = rt->total_frames.load();
    out_frame->fps = rt->fps > 0 ? rt->fps : 60.0;
    out_frame->shared_nt_handle = rt->shared_handle;
    out_frame->pixel_data = rt->pixel_cache.empty() ? nullptr : rt->pixel_cache.data();
    out_frame->pixel_data_bytes = (uint32_t)rt->pixel_cache.size();
    out_frame->is_native_surface = (rt->parent_hwnd != nullptr && rt->game_hwnd != nullptr) ? 1 : 0;
    LeaveCriticalSection(&rt->cs);

    return RP_OK;
}

EASYRPG_EXPORT int32_t rpg_easyrpg_read_pixels(void* instance, void* dst_bgra8, uint32_t cap_bytes) {
    if (!instance || !dst_bgra8) return RP_ERR_INVALID;
    auto* rt = static_cast<EasyRpgRuntime*>(instance);

    EnterCriticalSection(&rt->cs);
    if (!rt->pixel_cache.empty() && cap_bytes >= rt->pixel_cache.size()) {
        memcpy(dst_bgra8, rt->pixel_cache.data(), rt->pixel_cache.size());
        LeaveCriticalSection(&rt->cs);
        return RP_OK;
    }
    LeaveCriticalSection(&rt->cs);
    return RP_ERR_STATE;
}

EASYRPG_EXPORT int32_t rpg_easyrpg_get_hwnd(void* instance, void** out_hwnd) {
    if (!instance || !out_hwnd) return RP_ERR_INVALID;
    auto* rt = static_cast<EasyRpgRuntime*>(instance);
    EnterCriticalSection(&rt->cs);
    *out_hwnd = rt->game_hwnd;
    LeaveCriticalSection(&rt->cs);
    return RP_OK;
}

EASYRPG_EXPORT int32_t rpg_easyrpg_get_pid(void* instance, uint32_t* out_pid) {
    if (!instance || !out_pid) return RP_ERR_INVALID;
    auto* rt = static_cast<EasyRpgRuntime*>(instance);
    *out_pid = rt->pi.dwProcessId;
    return RP_OK;
}

EASYRPG_EXPORT int32_t rpg_easyrpg_host_window(void* instance, void* parent_hwnd, int32_t x, int32_t y, int32_t w, int32_t h) {
    if (!instance) return RP_ERR_INVALID;
    auto* rt = static_cast<EasyRpgRuntime*>(instance);

    EnterCriticalSection(&rt->cs);
    rt->parent_hwnd = (HWND)parent_hwnd;
    if (rt->game_hwnd && IsWindow(rt->game_hwnd) && parent_hwnd && IsWindow((HWND)parent_hwnd)) {
        SetParent(rt->game_hwnd, (HWND)parent_hwnd);
        LONG_PTR style = GetWindowLongPtrW(rt->game_hwnd, GWL_STYLE);
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        style |= WS_CHILD;
        SetWindowLongPtrW(rt->game_hwnd, GWL_STYLE, style);
        SetWindowPos(rt->game_hwnd, nullptr, x, y, w, h, SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    }
    LeaveCriticalSection(&rt->cs);

    return RP_OK;
}

EASYRPG_EXPORT int32_t rpg_easyrpg_resize(void* instance, int32_t w, int32_t h) {
    if (!instance) return RP_ERR_INVALID;
    auto* rt = static_cast<EasyRpgRuntime*>(instance);

    EnterCriticalSection(&rt->cs);
    if (w > 0 && h > 0) {
        rt->width = (uint32_t)w;
        rt->height = (uint32_t)h;
        rt->pixel_cache.resize(size_t(rt->width) * rt->height * 4, 0);
    }
    if (rt->game_hwnd && IsWindow(rt->game_hwnd)) {
        SetWindowPos(rt->game_hwnd, nullptr, 0, 0, w, h, SWP_NOZORDER | SWP_NOMOVE | SWP_SHOWWINDOW);
    }
    LeaveCriticalSection(&rt->cs);

    return RP_OK;
}

EASYRPG_EXPORT int32_t rpg_easyrpg_send_input(void* instance, uint32_t msg, uintptr_t wparam, intptr_t lparam) {
    if (!instance) return RP_ERR_INVALID;
    auto* rt = static_cast<EasyRpgRuntime*>(instance);

    HWND target = nullptr;
    EnterCriticalSection(&rt->cs);
    target = rt->game_hwnd;
    LeaveCriticalSection(&rt->cs);

    if (target && IsWindow(target)) {
        PostMessageW(target, msg, (WPARAM)wparam, (LPARAM)lparam);
        return RP_OK;
    }
    return RP_ERR_STATE;
}

EASYRPG_EXPORT int32_t rpg_easyrpg_get_stats(void* instance, rp_easyrpg_stats* out_stats) {
    if (!instance || !out_stats) return RP_ERR_INVALID;
    auto* rt = static_cast<EasyRpgRuntime*>(instance);

    EnterCriticalSection(&rt->cs);
    out_stats->size = sizeof(rp_easyrpg_stats);
    out_stats->pid = rt->pi.dwProcessId;
    out_stats->hwnd = rt->game_hwnd;
    out_stats->total_rendered_frames = rt->total_frames.load();
    out_stats->current_fps = rt->fps;
    out_stats->is_running = rt->running.load() ? 1 : 0;
    out_stats->is_hosted = (rt->parent_hwnd != nullptr) ? 1 : 0;
    LeaveCriticalSection(&rt->cs);

    return RP_OK;
}

EASYRPG_EXPORT void rpg_easyrpg_destroy(void* instance) {
    if (!instance) return;
    auto* rt = static_cast<EasyRpgRuntime*>(instance);
    rpg_easyrpg_stop(rt);
    DeleteCriticalSection(&rt->cs);
    delete rt;
}
