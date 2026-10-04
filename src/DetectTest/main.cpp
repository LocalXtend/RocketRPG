// main.cpp - DetectTest: tiny console tester for RPGMakerVersionDetect.dll.
//
// Loads the detection library purely at runtime (LoadLibraryW + GetProcAddress,
// no import-lib linkage), takes a game folder from the command line or a
// native folder-picker dialog, calls rmd_detect() and prints the verdict as
// UTF-8 text (console CP forced to 65001 so Korean labels render).
//
// Exit codes: 0 = ok / user cancelled, 1 = bad input or negative detect
// status, 2 = DLL could not be loaded or exports are missing.

#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0601 // IFileOpenDialog needs Vista+ shell dialogs
#endif
#include <windows.h>
#include <shobjidl.h>
#include <cwchar>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>

#include <rmdetect.h> // frozen public API: types + status enums only

namespace {

// Signatures mirrored from rmdetect.h; resolved via GetProcAddress so this
// exe never links against the DLL's import library.
using RmdDetectFn = int32_t (*)(const wchar_t*, RMD_RESULT*);
using RmdNameFn = const char* (*)(int32_t);

// Wide -> UTF-8 for console output (console CP is already 65001).
std::string ToUtf8(const wchar_t* w) {
    std::string out;
    if (!w || !*w) return out;
    const int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    if (n <= 1) return out;
    out.resize(static_cast<size_t>(n));
    WideCharToMultiByte(CP_UTF8, 0, w, -1, out.data(), n, nullptr, nullptr);
    out.resize(static_cast<size_t>(n - 1)); // drop the trailing NUL
    return out;
}

// Prefer the DLL sitting next to this exe so the CWD never matters; fall back
// to the bare name resolved through the loader search path (CWD included).
HMODULE LoadDetector() {
    wchar_t exe[MAX_PATH] = {};
    const DWORD n = GetModuleFileNameW(nullptr, exe, MAX_PATH);
    if (n > 0 && n < MAX_PATH) {
        std::wstring dir(exe);
        const size_t slash = dir.find_last_of(L"\\/");
        if (slash != std::wstring::npos) {
            dir.resize(slash);
            // Absolute sibling path first: immune to whatever the CWD is.
            if (HMODULE h = LoadLibraryW((dir + L"\\RPGMakerVersionDetect.dll").c_str()))
                return h;
        }
    }
    return LoadLibraryW(L"RPGMakerVersionDetect.dll");
}

// Native folder picker; false on cancel, failed COM init, or any dialog error.
bool PickFolder(std::wstring& out) {
    // The shell file dialog requires STA on the calling thread.
    if (FAILED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED))) return false;
    bool ok = false;
    IFileOpenDialog* dlg = nullptr;
    if (SUCCEEDED(CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER,
                                   IID_IFileOpenDialog,
                                   reinterpret_cast<void**>(&dlg)))) {
        DWORD opts = 0;
        if (SUCCEEDED(dlg->GetOptions(&opts)))
            dlg->SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
        dlg->SetTitle(L"RPG Maker 게임 폴더 선택");
        if (SUCCEEDED(dlg->Show(nullptr))) {
            IShellItem* item = nullptr;
            if (SUCCEEDED(dlg->GetResult(&item))) {
                PWSTR path = nullptr;
                if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path))) {
                    out.assign(path);
                    CoTaskMemFree(path);
                    ok = true;
                }
                item->Release();
            }
        }
        dlg->Release();
    }
    CoUninitialize();
    return ok;
}

// Strip surrounding quotes that survive some shell argument passing.
std::wstring TrimQuotes(const wchar_t* s) {
    std::wstring v(s ? s : L"");
    while (!v.empty() && (v.front() == L'"' || v.front() == L'\'')) v.erase(v.begin());
    while (!v.empty() && (v.back() == L'"' || v.back() == L'\'')) v.pop_back();
    return v;
}

// Interactive pause unless RMD_NOPAUSE=1 (used by automated validation loops).
void Pause() {
    wchar_t flag[8] = {};
    if (GetEnvironmentVariableW(L"RMD_NOPAUSE", flag, 8) != 0 &&
        wcscmp(flag, L"1") == 0)
        return;
    system("pause");
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    SetConsoleOutputCP(65001); // UTF-8 console so Korean labels render correctly
    SetConsoleCP(65001);

    HMODULE dll = LoadDetector();
    if (!dll) {
        printf("[오류] RPGMakerVersionDetect.dll을 찾을 수 없습니다. DetectTest.exe와 같은 폴더에 두세요.\n");
        printf("[Error] RPGMakerVersionDetect.dll not found. It must sit next to DetectTest.exe.\n");
        Pause();
        return 2;
    }

    // Guard every export: a stale/mismatched DLL may lack required symbols.
    const RmdDetectFn detect =
        reinterpret_cast<RmdDetectFn>(GetProcAddress(dll, "rmd_detect"));
    const RmdNameFn engineName =
        reinterpret_cast<RmdNameFn>(GetProcAddress(dll, "rmd_engine_name"));
    const RmdNameFn kindName =
        reinterpret_cast<RmdNameFn>(GetProcAddress(dll, "rmd_kind_name"));
    const RmdNameFn layoutName =
        reinterpret_cast<RmdNameFn>(GetProcAddress(dll, "rmd_layout_name"));
    if (!detect || !engineName || !kindName || !layoutName) {
        printf("[오류] DLL에 필요한 함수가 없습니다 (rmd_detect / rmd_*_name).\n");
        printf("[Error] Required exports missing from RPGMakerVersionDetect.dll.\n");
        Pause();
        return 2;
    }

    std::wstring dir;
    if (argc > 1) {
        dir = TrimQuotes(argv[1]);
    } else if (!PickFolder(dir)) {
        printf("폴더가 선택되지 않아 종료합니다.\nNo folder selected; exiting.\n");
        Pause();
        return 0;
    }

    const DWORD attr = GetFileAttributesW(dir.c_str());
    if (attr == INVALID_FILE_ATTRIBUTES || !(attr & FILE_ATTRIBUTE_DIRECTORY)) {
        const std::string shown = ToUtf8(dir.c_str());
        printf("[오류] 폴더가 아니거나 존재하지 않습니다: %s\n", shown.c_str());
        printf("[Error] Path does not exist or is not a directory: %s\n", shown.c_str());
        Pause();
        return 1;
    }

    RMD_RESULT res{};              // caller-owned; zero-init per ABI contract
    res.size = sizeof(RMD_RESULT); // version tag validated inside the DLL
    const int32_t st = detect(dir.c_str(), &res);
    if (st < 0) {
        switch (st) {
        case RMD_ERR_INVALID:
            printf("감지 실패: 잘못된 인자(null)입니다.\nDetection failed: invalid argument (null).\n");
            break;
        case RMD_ERR_NOTDIR:
            printf("감지 실패: 경로가 없거나 폴더가 아닙니다.\nDetection failed: path does not exist or is not a directory.\n");
            break;
        default:
            printf("감지 실패: 알 수 없는 상태 코드 %d\nDetection failed: unknown status code %d\n",
                   st, st);
            break;
        }
        Pause();
        return 1;
    }

    // Report: Korean labels, values straight from the API.
    const std::string dirUtf8 =
        ToUtf8(res.dir[0] ? static_cast<const wchar_t*>(res.dir) : dir.c_str());
    printf("\n=== RPG Maker 감지 결과 ===\n");
    printf("경로: %s\n", dirUtf8.c_str());
    printf("엔진: %s (%d)\n", engineName(res.engine), res.engine);
    printf("종류: %s\n", kindName(res.kind));
    printf("레이아웃: %s\n", layoutName(res.layout));
    printf("제목: %s\n", res.title_utf8);
    printf("런타임: %s\n", res.runtime_utf8);
    printf("판단 근거: %s\n", res.detail_utf8);

    Pause();
    return 0;
}
