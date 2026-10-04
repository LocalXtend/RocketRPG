// crash.cpp - native crash reporter for the UI process.
// A managed VEH must not run .NET code while the runtime itself is handling an exception
// (that corrupts the runtime: COR_E_EXECUTIONENGINE). This handler is pure Win32:
// it ignores faults inside the .NET runtime / JIT code (NullReferenceException etc. start as AVs)
// and records only faults raised inside native modules (RocketRPGCore.dll, system DLLs, drivers).
#include "util.hpp"
#include <cstdarg>
#include <cwchar>

namespace rr {

static wchar_t g_crash_dir[MAX_PATH] = {0};
static volatile LONG g_reports = 0;
static PVOID g_veh = nullptr;

static bool module_of(void* addr, HMODULE* out, wchar_t* name, DWORD cap) {
    *out = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            (LPCWSTR)addr, out) || !*out)
        return false;
    if (!GetModuleFileNameW(*out, name, cap)) name[0] = 0;
    return true;
}

static const wchar_t* base_name(const wchar_t* path) {
    const wchar_t* s = wcsrchr(path, L'\\');
    return s ? s + 1 : path;
}

// Faults inside these are part of normal .NET exception handling (or JIT code, which has no module).
static bool is_runtime_module(HMODULE mod, const wchar_t* path) {
    if (mod == GetModuleHandleW(nullptr)) return true; // single-file host links coreclr statically
    const wchar_t* b = base_name(path);
    return _wcsicmp(b, L"coreclr.dll") == 0 || _wcsicmp(b, L"clrjit.dll") == 0 ||
           _wcsnicmp(b, L"System.", 7) == 0 || _wcsicmp(b, L"hostfxr.dll") == 0 ||
           _wcsicmp(b, L"hostpolicy.dll") == 0 || _wcsnicmp(b, L"Microsoft.", 10) == 0;
}

static bool is_fatal_code(DWORD code) {
    return code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_ILLEGAL_INSTRUCTION ||
           code == EXCEPTION_STACK_OVERFLOW || code == EXCEPTION_INT_DIVIDE_BY_ZERO ||
           code == EXCEPTION_PRIV_INSTRUCTION || code == EXCEPTION_IN_PAGE_ERROR ||
           code == STATUS_HEAP_CORRUPTION || code == STATUS_STACK_BUFFER_OVERRUN;
}

static void append(char* buf, size_t cap, size_t& len, const char* fmt, ...) {
    if (len >= cap) return;
    va_list ap;
    va_start(ap, fmt);
    int n = _vsnprintf_s(buf + len, cap - len, _TRUNCATE, fmt, ap);
    va_end(ap);
    if (n > 0) len += (size_t)n;
    else len = strnlen(buf, cap);
}

static void write_report(EXCEPTION_POINTERS* ep, const wchar_t* fault_path, HMODULE fault_mod) {
    static char buf[8192]; // static: no heap/stack pressure during a crash (reports are serialized by g_reports)
    size_t len = 0;
    buf[0] = 0;
    SYSTEMTIME st;
    GetLocalTime(&st);
    auto* rec = ep->ExceptionRecord;
    char mod_u8[MAX_PATH * 3];
    WideCharToMultiByte(CP_UTF8, 0, fault_path, -1, mod_u8, sizeof(mod_u8), nullptr, nullptr);

    append(buf, sizeof(buf), len, "timestamp       : %04u-%02u-%02u %02u:%02u:%02u.%03u\r\n",
           st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    append(buf, sizeof(buf), len, "source          : native VEH (RocketRPGCore)\r\n");
    append(buf, sizeof(buf), len, "exception.code  : 0x%08lX\r\n", (unsigned long)rec->ExceptionCode);
    append(buf, sizeof(buf), len, "fault.address   : %p\r\n", rec->ExceptionAddress);
    append(buf, sizeof(buf), len, "fault.module    : %s + 0x%llX\r\n", mod_u8,
           (unsigned long long)((uintptr_t)rec->ExceptionAddress - (uintptr_t)fault_mod));
    if (rec->ExceptionCode == EXCEPTION_ACCESS_VIOLATION && rec->NumberParameters >= 2)
        append(buf, sizeof(buf), len, "access          : %s %p\r\n",
               rec->ExceptionInformation[0] == 0 ? "read" : rec->ExceptionInformation[0] == 1 ? "write" : "execute",
               (void*)rec->ExceptionInformation[1]);
    append(buf, sizeof(buf), len, "thread          : %lu\r\n\r\nstack (module + offset):\r\n", GetCurrentThreadId());

    void* frames[48];
    USHORT n = RtlCaptureStackBackTrace(0, 48, frames, nullptr);
    for (USHORT i = 0; i < n; i++) {
        HMODULE m = nullptr;
        wchar_t p[MAX_PATH];
        p[0] = 0;
        if (module_of(frames[i], &m, p, MAX_PATH)) {
            char u8[MAX_PATH * 3];
            WideCharToMultiByte(CP_UTF8, 0, base_name(p), -1, u8, sizeof(u8), nullptr, nullptr);
            append(buf, sizeof(buf), len, "  %2u  %s + 0x%llX\r\n", i, u8,
                   (unsigned long long)((uintptr_t)frames[i] - (uintptr_t)m));
        } else {
            append(buf, sizeof(buf), len, "  %2u  %p (managed/JIT)\r\n", i, frames[i]);
        }
    }

    wchar_t file[MAX_PATH * 2];
    _snwprintf_s(file, _countof(file), _TRUNCATE, L"%s\\crash-native-%04u%02u%02u-%02u%02u%02u-%08lX.txt",
                 g_crash_dir, st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond,
                 (unsigned long)rec->ExceptionCode);
    CreateDirectoryW(g_crash_dir, nullptr);
    HANDLE h = CreateFileW(file, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h != INVALID_HANDLE_VALUE) {
        DWORD wr = 0;
        WriteFile(h, buf, (DWORD)len, &wr, nullptr);
        CloseHandle(h);
    }
}

static LONG CALLBACK crash_veh(EXCEPTION_POINTERS* ep) {
    if (!ep || !ep->ExceptionRecord || !g_crash_dir[0]) return EXCEPTION_CONTINUE_SEARCH;
    DWORD code = ep->ExceptionRecord->ExceptionCode;
    if (!is_fatal_code(code)) return EXCEPTION_CONTINUE_SEARCH;

    HMODULE mod = nullptr;
    wchar_t path[MAX_PATH];
    path[0] = 0;
    bool has_mod = module_of(ep->ExceptionRecord->ExceptionAddress, &mod, path, MAX_PATH);
    if (code != EXCEPTION_STACK_OVERFLOW && (!has_mod || is_runtime_module(mod, path)))
        return EXCEPTION_CONTINUE_SEARCH;

    if (InterlockedIncrement(&g_reports) > 3) return EXCEPTION_CONTINUE_SEARCH; // 세션당 최대 3건
    write_report(ep, has_mod ? path : L"(unknown)", mod);
    return EXCEPTION_CONTINUE_SEARCH;
}

} // namespace rr

RPG_EXPORT rstatus rpg_install_crash_handler(const wchar_t* crash_dir) {
    if (!crash_dir || !crash_dir[0]) RPG_FAIL(RP_ERR_INVALID, "null dir");
    wcsncpy_s(rr::g_crash_dir, crash_dir, _TRUNCATE);
    if (!rr::g_veh) rr::g_veh = AddVectoredExceptionHandler(0, rr::crash_veh); // 0 = 런타임 핸들러 뒤에 호출
    if (!rr::g_veh) RPG_FAIL(RP_ERR_STATE, "AddVectoredExceptionHandler failed");
    RPG_OK();
}
