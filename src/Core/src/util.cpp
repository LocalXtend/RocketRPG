// util.cpp
#include "util.hpp"
#include <cstdio>
#include <cwchar>
#include <algorithm>
#include <memory>

namespace rr {

// Per-thread error text via dynamic TLS (TlsAlloc). Do NOT use thread_local here: when this DLL is loaded
// at runtime into the single-file .NET host, the loader leaves its static TLS index at 0 (the exe's slot),
// so thread_local writes landed in the .NET runtime's own TLS block and crashed the UI (0x80131506).
static DWORD g_err_tls = TLS_OUT_OF_INDEXES;
static INIT_ONCE g_err_once = INIT_ONCE_STATIC_INIT;
static char g_err_fallback[512] = {0};

static BOOL CALLBACK err_tls_init(PINIT_ONCE, PVOID, PVOID*) {
    g_err_tls = TlsAlloc();
    return TRUE;
}

static char* err_buf() {
    InitOnceExecuteOnce(&g_err_once, err_tls_init, nullptr, nullptr);
    if (g_err_tls == TLS_OUT_OF_INDEXES) return g_err_fallback;
    auto* b = static_cast<char*>(TlsGetValue(g_err_tls));
    if (!b) {
        b = static_cast<char*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 512));
        if (!b) return g_err_fallback;
        TlsSetValue(g_err_tls, b);
    }
    return b;
}

void set_last_error(rstatus st, const std::string& msg) {
    (void)st;
    snprintf(err_buf(), 512, "%s", msg.c_str());
}
const char* rpg_last_error_impl() { return err_buf(); }

std::wstring utf8_to_wide(const char* s) {
    if (!s || !*s) return std::wstring();
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    std::wstring w(n ? n - 1 : 0, L'\0');
    if (n) MultiByteToWideChar(CP_UTF8, 0, s, -1, &w[0], n);
    return w;
}
std::string wide_to_utf8(const wchar_t* s) {
    if (!s || !*s) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, s, -1, nullptr, 0, nullptr, nullptr);
    std::string u(n ? n - 1 : 0, '\0');
    if (n) WideCharToMultiByte(CP_UTF8, 0, s, -1, &u[0], n, nullptr, nullptr);
    return u;
}
std::string ansi_to_utf8(const char* s) {
    if (!s || !*s) return std::string();

    // 1. Check if already valid UTF-8
    int u8_len = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s, -1, nullptr, 0);
    if (u8_len > 0) {
        bool has_high_byte = false;
        for (const unsigned char* p = (const unsigned char*)s; *p; p++) {
            if (*p >= 0x80) { has_high_byte = true; break; }
        }
        if (has_high_byte) {
            std::wstring w(u8_len ? u8_len - 1 : 0, L'\0');
            MultiByteToWideChar(CP_UTF8, 0, s, -1, &w[0], u8_len);
            return wide_to_utf8(w.c_str());
        }
    }

    // 2. Score CP949 (Korean) vs CP932 (Shift-JIS)
    int kor_score = 0;
    int sjis_score = 0;
    const unsigned char* p = (const unsigned char*)s;
    for (size_t i = 0; p[i] && p[i + 1]; i++) {
        unsigned char b1 = p[i];
        unsigned char b2 = p[i + 1];

        // Korean KS C 5601 common Hangul syllables (0xB0..0xC8, 0xA1..0xFE)
        if (b1 >= 0xB0 && b1 <= 0xC8 && b2 >= 0xA1 && b2 <= 0xFE) {
            kor_score += 2;
            i++;
            continue;
        }

        // Shift-JIS punctuation: 0x81 [0x40..0x7E, 0x80..0xAC]
        if (b1 == 0x81 && ((b2 >= 0x40 && b2 <= 0x7E) || (b2 >= 0x80 && b2 <= 0xAC))) {
            sjis_score += 3;
            i++;
            continue;
        }

        // Hiragana: 0x82 [0x9F..0xF1]
        if (b1 == 0x82 && b2 >= 0x9F && b2 <= 0xF1) {
            sjis_score += 3;
            i++;
            continue;
        }

        // Katakana: 0x83 [0x40..0x96] (excluding 0x7F)
        if (b1 == 0x83 && b2 >= 0x40 && b2 <= 0x96 && b2 != 0x7F) {
            sjis_score += 3;
            i++;
            continue;
        }

        // Shift-JIS Kanji with low-trail byte (0x40..0x7E, excluding 0x7F)
        if (((b1 >= 0x88 && b1 <= 0x9F) || (b1 >= 0xE0 && b1 <= 0xEA)) && b2 >= 0x40 && b2 <= 0x7E && b2 != 0x7F) {
            sjis_score += 2;
            i++;
            continue;
        }
    }

    UINT sys_acp = GetACP();
    UINT try_pages[3];
    if (sjis_score > kor_score) {
        try_pages[0] = 932; // Shift-JIS
        try_pages[1] = 949; // Korean
        try_pages[2] = CP_ACP;
    } else if (kor_score > sjis_score) {
        try_pages[0] = 949; // Korean
        try_pages[1] = 932; // Shift-JIS
        try_pages[2] = CP_ACP;
    } else {
        if (sys_acp == 932) {
            try_pages[0] = 932;
            try_pages[1] = 949;
            try_pages[2] = CP_ACP;
        } else {
            try_pages[0] = 949;
            try_pages[1] = 932;
            try_pages[2] = CP_ACP;
        }
    }

    for (UINT cp : try_pages) {
        int wn = MultiByteToWideChar(cp, MB_ERR_INVALID_CHARS, s, -1, nullptr, 0);
        if (wn > 0) {
            std::wstring w(wn - 1, L'\0');
            MultiByteToWideChar(cp, 0, s, -1, &w[0], wn);
            return wide_to_utf8(w.c_str());
        }
    }

    int wn = MultiByteToWideChar(CP_ACP, 0, s, -1, nullptr, 0);
    if (wn > 0) {
        std::wstring w(wn - 1, L'\0');
        MultiByteToWideChar(CP_ACP, 0, s, -1, &w[0], wn);
        return wide_to_utf8(w.c_str());
    }

    return std::string(s);
}
std::string trim(const std::string& s) {
    size_t a = s.find_first_not_of(" \t\r\n");
    if (a == std::string::npos) return "";
    size_t b = s.find_last_not_of(" \t\r\n");
    return s.substr(a, b - a + 1);
}

bool read_file_bin(const std::wstring& path, std::vector<uint8_t>& out) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER sz{};
    bool ok = false;
    if (GetFileSizeEx(h, &sz) && sz.QuadPart >= 0 && sz.QuadPart < (1LL << 31)) {
        out.resize((size_t)sz.QuadPart);
        DWORD rd = 0;
        ok = out.empty() || (ReadFile(h, out.data(), (DWORD)out.size(), &rd, nullptr) && rd == out.size());
    }
    CloseHandle(h);
    return ok;
}
bool write_file_bin(const std::wstring& path, const std::string& data) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, 0,
                           nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    DWORD wr = 0;
    bool ok = data.empty() || (WriteFile(h, data.data(), (DWORD)data.size(), &wr, nullptr) && wr == data.size());
    CloseHandle(h);
    return ok;
}

bool read_file_text(const std::wstring& path, std::string& out_utf8) {
    std::vector<uint8_t> bin;
    if (!read_file_bin(path, bin)) return false;
    // BOM detect
    if (bin.size() >= 3 && bin[0] == 0xEF && bin[1] == 0xBB && bin[2] == 0xBF) {
        out_utf8.assign(bin.begin() + 3, bin.end());
        return true;
    }
    bin.push_back(0); bin.push_back(0);
    const char* raw = (const char*)bin.data();
    bool is_utf8 = true;
    for (size_t i = 0; i + 1 < bin.size(); i++) { if ((unsigned char)raw[i] >= 0x80) { /* heuristic: assume ANSI */ is_utf8 = false; break; } }
    if (is_utf8) out_utf8.assign(raw);
    else out_utf8 = ansi_to_utf8(raw);
    return true;
}
bool write_file_text(const std::wstring& path, const std::string& utf8) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    DWORD wr = 0;
    BOOL ok = WriteFile(h, utf8.data(), (DWORD)utf8.size(), &wr, nullptr);
    CloseHandle(h);
    return !!ok && wr == utf8.size();
}
bool dir_exists(const std::wstring& p) {
    DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
}
bool file_exists(const std::wstring& p) {
    DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}
std::wstring join_path(const std::wstring& a, const std::wstring& b) {
    if (a.empty()) return b;
    if (b.empty()) return a;
    wchar_t last = a[a.size() - 1];
    if (last == L'\\' || last == L'/') return a + b;
    return a + L"\\" + b;
}

bool ini_read(const std::wstring& path, const char* section, std::vector<IniKV>& out) {
    std::string txt;
    if (!read_file_text(path, txt)) return false;
    std::string cur;
    std::string want = section ? ("[" + trim(section) + "]") : std::string();
    bool in = want.empty();
    size_t pos = 0;
    while (pos <= txt.size()) {
        size_t eol = txt.find('\n', pos);
        if (eol == std::string::npos) eol = txt.size();
        std::string line = trim(txt.substr(pos, eol - pos));
        pos = eol + 1;
        if (line.empty()) continue;
        if (line[0] == '[') {
            cur = line;
            in = (want.empty() || _stricmp(cur.c_str(), want.c_str()) == 0);
            continue;
        }
        if (!in) continue;
        size_t eq = line.find('=');
        if (eq == std::string::npos) continue;
        IniKV kv{ trim(line.substr(0, eq)), trim(line.substr(eq + 1)) };
        out.push_back(kv);
    }
    return !out.empty();
}
bool ini_get(const std::wstring& path, const char* section, const char* key, std::string& out) {
    std::vector<IniKV> kvs;
    if (!ini_read(path, section, kvs)) return false;
    for (auto& kv : kvs) if (_stricmp(kv.key.c_str(), key) == 0) { out = kv.val; return true; }
    return false;
}

std::string json_escape(const std::string& s) {
    std::string o; o.reserve(s.size() + 8);
    for (unsigned char c : s) {
        switch (c) {
        case '"': o += "\\\""; break; case '\\': o += "\\\\"; break;
        case '\n': o += "\\n"; break; case '\r': o += "\\r"; break; case '\t': o += "\\t"; break;
        default:
            if (c < 0x20) { char b[8]; snprintf(b, sizeof(b), "\\u%04x", c); o += b; }
            else o += (char)c;
        }
    }
    return o;
}
static bool json_skip_ws(const std::string& j, size_t& i) {
    while (i < j.size() && (j[i] == ' ' || j[i] == '\t' || j[i] == '\r' || j[i] == '\n')) i++;
    return i < j.size();
}
// generic key finder: finds "key" : value (string|number|bool) at any depth
bool json_find_string(const std::string& j, const std::string& key, std::string& out) {
    std::string pat = "\"" + key + "\"";
    size_t p = j.find(pat);
    if (p == std::string::npos) return false;
    size_t i = p + pat.size();
    if (!json_skip_ws(j, i)) return false;
    if (j[i] != ':') return false;
    i++;
    if (!json_skip_ws(j, i)) return false;
    if (j[i] != '"') return false;
    i++;
    std::string v;
    while (i < j.size() && j[i] != '"') {
        if (j[i] == '\\' && i + 1 < j.size()) {
            char c = j[++i];
            switch (c) { case 'n': v += '\n'; break; case 't': v += '\t'; break; case 'r': v += '\r'; break;
                         default: v += c; }
        } else v += j[i];
        i++;
    }
    out = v;
    return true;
}
bool json_find_int(const std::string& j, const std::string& key, long long& out) {
    std::string pat = "\"" + key + "\"";
    size_t p = j.find(pat);
    if (p == std::string::npos) return false;
    size_t i = p + pat.size();
    if (!json_skip_ws(j, i) || j[i] != ':') return false;
    i++;
    if (!json_skip_ws(j, i)) return false;
    if (j[i] == '"') { std::string s; return json_find_string(j, key, s) && sscanf(s.c_str(), "%lld", &out) == 1; }
    out = strtoll(j.c_str() + i, nullptr, 10);
    return true;
}

void log_line(const char* tag, const std::string& msg) {
    OutputDebugStringA("[RocketRPG] ");
    OutputDebugStringA(tag);
    OutputDebugStringA(": ");
    OutputDebugStringA(msg.c_str());
    OutputDebugStringA("\n");
}

} // namespace rr

extern "C" const char* rpg_last_error(void) { return rr::rpg_last_error_impl(); }
