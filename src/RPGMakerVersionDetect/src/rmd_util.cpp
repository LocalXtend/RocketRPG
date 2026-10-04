// rmd_util.cpp - filesystem / encoding / parsing helpers for the detector.
//
// Idioms intentionally mirror src/Core/src/util.cpp (same probe style, same
// chunked token scan) so both codebases read alike; this copy is deliberately
// standalone because RPGMakerVersionDetect.dll must not link against Core.

#include "rmd_internal.hpp"

#include <cstring>
#include <cstdio>

namespace rmd {

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

std::string wide_to_utf8(const wchar_t* s) {
    if (!s || !*s) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, s, -1, nullptr, 0, nullptr, nullptr);
    std::string u(n ? n - 1 : 0, '\0');
    if (n) WideCharToMultiByte(CP_UTF8, 0, s, -1, &u[0], n, nullptr, nullptr);
    return u;
}

std::string ansi_to_utf8(const char* s) {
    if (!s || !*s) return std::string();
    int wn = MultiByteToWideChar(CP_ACP, 0, s, -1, nullptr, 0);
    std::wstring w(wn ? wn - 1 : 0, L'\0');
    if (wn) MultiByteToWideChar(CP_ACP, 0, s, -1, &w[0], wn);
    return wide_to_utf8(w.c_str());
}

static bool read_file_bin(const std::wstring& path, std::vector<uint8_t>& out) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ,
                           nullptr, OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    out.clear();
    uint8_t buf[65536];
    DWORD rd = 0;
    while (ReadFile(h, buf, sizeof(buf), &rd, nullptr) && rd > 0)
        out.insert(out.end(), buf, buf + rd);
    CloseHandle(h);
    return true;
}

bool read_file_text(const std::wstring& path, std::string& out_utf8) {
    std::vector<uint8_t> bin;
    if (!read_file_bin(path, bin)) return false;
    // UTF-8 BOM strip
    size_t off = (bin.size() >= 3 && bin[0] == 0xEF && bin[1] == 0xBB && bin[2] == 0xBF) ? 3 : 0;
    const char* raw = (const char*)bin.data() + off;
    int len = (int)(bin.size() - off);
    if (len <= 0) { out_utf8.clear(); return true; }
    // Valid UTF-8 passes through untouched; anything else is legacy ANSI.
    // (Strict flag keeps us from mis-decoding UTF-8 titles as CP_ACP.)
    int wlen = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, raw, len, nullptr, 0);
    if (wlen > 0) {
        out_utf8.assign(raw, (size_t)len);
    } else {
        out_utf8 = ansi_to_utf8(std::string(raw, (size_t)len).c_str());
    }
    return true;
}

static std::string trim(const std::string& s) {
    size_t b = s.find_first_not_of(" \t\r\n");
    if (b == std::string::npos) return std::string();
    size_t e = s.find_last_not_of(" \t\r\n");
    return s.substr(b, e - b + 1);
}

static char ascii_lower(char c) {
    return (c >= 'A' && c <= 'Z') ? (char)(c - 'A' + 'a') : c;
}

static bool ieq(const std::string& a, const std::string& b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); i++)
        if (ascii_lower(a[i]) != ascii_lower(b[i])) return false;
    return true;
}

bool ini_get(const std::wstring& path, const char* section, const char* key,
             std::string& out) {
    out.clear();
    std::string txt;
    if (!read_file_text(path, txt)) return false;
    std::string want_section = std::string("[") + section + "]";
    bool in = false;
    size_t pos = 0;
    while (pos <= txt.size()) {
        size_t eol = txt.find('\n', pos);
        if (eol == std::string::npos) eol = txt.size();
        std::string line = trim(txt.substr(pos, eol - pos));
        pos = eol + 1;
        if (line.empty()) continue;
        if (line[0] == '[') { in = ieq(trim(line), want_section); continue; }
        if (!in) continue;
        size_t eq = line.find('=');
        if (eq == std::string::npos) continue;   // junk lines are ignored
        if (ieq(trim(line.substr(0, eq)), key)) { out = trim(line.substr(eq + 1)); return true; }
    }
    return false;
}

bool json_get_string(const std::string& json, const char* key, std::string& out) {
    out.clear();
    std::string pat = "\"" + std::string(key) + "\"";
    size_t p = json.find(pat);
    if (p == std::string::npos) return false;
    size_t i = p + pat.size();
    while (i < json.size() && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
    if (i >= json.size() || json[i] != ':') return false;
    i++;
    while (i < json.size() && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
    if (i >= json.size() || json[i] != '"') return false;
    i++;
    std::string v;
    while (i < json.size() && json[i] != '"') {
        if (json[i] == '\\' && i + 1 < json.size()) {
            char c = json[++i];
            switch (c) {
            case 'n': v += '\n'; break;
            case 't': v += '\t'; break;
            case 'r': v += '\r'; break;
            case 'b': v += '\b'; break;
            case 'f': v += '\f'; break;
            default:  v += c; break;   // \" \\ \/ and best-effort leftovers
            }
        } else {
            v += json[i];
        }
        i++;
    }
    out = v;
    return true;
}

static const void* rmd_memmem(const void* hay, size_t hl, const char* nd, size_t nl) {
    if (nl == 0 || hl < nl) return nullptr;
    const uint8_t* h = (const uint8_t*)hay;
    for (size_t i = 0; i + nl <= hl; i++)
        if (h[i] == (uint8_t)nd[0] && memcmp(h + i, nd, nl) == 0) return h + i;
    return nullptr;
}

int scan_tokens(const std::wstring& path, const char* const* tokens, size_t ntok) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ,
                           nullptr, OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) return -1;
    std::vector<uint8_t> buf(1 << 20);
    std::vector<uint8_t> carry;
    int found = -1;
    DWORD rd = 0;
    while (found < 0 && ReadFile(h, buf.data(), (DWORD)buf.size(), &rd, nullptr) && rd > 0) {
        std::vector<uint8_t> all;
        all.reserve(carry.size() + rd);
        all.insert(all.end(), carry.begin(), carry.end());
        all.insert(all.end(), buf.begin(), buf.begin() + rd);
        for (size_t t = 0; t < ntok && found < 0; t++) {
            if (rmd_memmem(all.data(), all.size(), tokens[t], strlen(tokens[t]))) found = (int)t;
        }
        // Keep a tail long enough for the longest possible split token.
        size_t keep = 31;
        carry.assign(all.end() - (all.size() > keep ? keep : all.size()), all.end());
    }
    CloseHandle(h);
    return found;
}

void copy_char(char* dst, size_t cap, const std::string& src) {
    if (!dst || cap == 0) return;
    size_t n = src.size() < cap - 1 ? src.size() : cap - 1;
    memcpy(dst, src.data(), n);
    // Step back over a truncated multi-byte sequence instead of splitting it.
    while (n > 0 && ((src[n] & 0xC0) == 0x80)) n--;
    dst[n] = '\0';
}

void copy_wide(wchar_t* dst, size_t cap, const wchar_t* src) {
    if (!dst || cap == 0) return;
    size_t len = wcslen(src ? src : L"");
    size_t n = len < cap - 1 ? len : cap - 1;
    if (n) memcpy(dst, src, n * sizeof(wchar_t));
    dst[n] = L'\0';
}

} // namespace rmd
