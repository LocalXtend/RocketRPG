#include "util.hpp"
#include <shlwapi.h>

extern "C" IMAGE_DOS_HEADER __ImageBase;

namespace rr {
void detect_set_rtp_root(const wchar_t* root);
}

static DWORD g_main_tid = 0;

static BOOL CALLBACK init_once(PINIT_ONCE, PVOID, PVOID*) {
    g_main_tid = GetCurrentThreadId();
    wchar_t self[MAX_PATH]{};
    GetModuleFileNameW((HMODULE)&__ImageBase, self, MAX_PATH);
    rr::log_line("core", std::string("loaded: ") + rr::wide_to_utf8(self));
    return TRUE;
}

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID) {
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls((HINSTANCE)&__ImageBase);
        break;
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}

RPG_EXPORT rstatus rpg_core_init(const wchar_t* portable_root) {
    static INIT_ONCE once = INIT_ONCE_STATIC_INIT;
    InitOnceExecuteOnce(&once, init_once, nullptr, nullptr);
    rr::detect_set_rtp_root(portable_root);
    rr::set_last_error(RP_OK, "");
    return RP_OK;
}

RPG_EXPORT void rpg_core_shutdown() {}

RPG_EXPORT const char* rpg_version() { return "1.1.3"; }
