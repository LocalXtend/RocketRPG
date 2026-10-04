#include "rocketrpg.h"
#include <windows.h>
#include <stdio.h>
#include <string>
#include <vector>

static std::wstring s2w(const std::string& u) {
    int n = MultiByteToWideChar(CP_UTF8, 0, u.c_str(), -1, nullptr, 0);
    std::wstring w(n - 1, 0);
    MultiByteToWideChar(CP_UTF8, 0, u.c_str(), -1, &w[0], n);
    return w;
}
static const char* eng(int e) {
    switch (e) { case RPG_ENGINE_RM2000: return "2000"; case RPG_ENGINE_RM2003: return "2003";
        case RPG_ENGINE_RMXP: return "XP"; case RPG_ENGINE_RMVX: return "VX";
        case RPG_ENGINE_RMVXACE: return "ACE"; case RPG_ENGINE_RMMV: return "MV";
        case RPG_ENGINE_RMMZ: return "MZ"; default: return "?"; }
}

int main() {
    if (rpg_core_init(nullptr) != RP_OK) { printf("init failed\n"); return 1; }
    printf("core version: %s\n", rpg_version());

    wchar_t root[MAX_PATH];
    GetCurrentDirectoryW(MAX_PATH, root);
    std::wstring samples = std::wstring(root) + L"\\GameSample";

    wchar_t** dirs = nullptr; int32_t n = 0;
    rstatus st = rpg_scan_games(samples.c_str(), &dirs, &n);
    printf("scan(%ls) => %d, found=%d\n", samples.c_str(), st, n);
    if (st != RP_OK || n == 0) { printf("SCAN FAILED: %s\n", rpg_last_error()); return 2; }

    int pass = 0, fail = 0;
    for (int i = 0; i < n; i++) {
        rp_game_info gi{};
        st = rpg_detect_game(dirs[i], &gi);
        char rep[512]{};
        rpg_validate_game(dirs[i], rep, sizeof(rep));
        // folder name only
        std::wstring d = dirs[i];
        size_t slash = d.find_last_of(L'\\');
        std::wstring name = slash == std::wstring::npos ? d : d.substr(slash + 1);
        wprintf(L"%-42ls ", name.c_str());
        if (st == RP_OK) {
            pass++;
            printf("[%s] layout=%d integrity-rep: %s\n", eng(gi.engine), gi.layout, rep + strlen("engine=0 "));
        } else {
            fail++;
            printf("[FAIL] %s | %s\n", rep, rpg_last_error());
        }
    }
    rpg_free_strings(dirs, n);
    printf("\nRESULT: pass=%d fail=%d / total=%d\n", pass, fail, n);

    rpg_core_shutdown();
    return fail == 0 ? 0 : 3;
}
