// rmd_internal.hpp - internal helpers shared by the detection sources.
//
// Nothing here is exported; everything lives in namespace rmd and mirrors the
// compact Win32 idioms used by src/Core/src/util.cpp / detect.cpp (file-exists
// probes, ANSI->UTF8 conversion, line-based INI parsing, naive JSON string
// lookup, chunked token scan over big files).

#ifndef RMD_INTERNAL_H
#define RMD_INTERNAL_H

#include <windows.h>
#include <stdint.h>
#include <string>
#include <vector>

namespace rmd {

/* File-system probes. Win32 name lookup is case-insensitive on NTFS, so all
 * literal-name checks done through these are case-insensitive by design. */
bool file_exists(const std::wstring& path);
bool dir_exists(const std::wstring& path);
std::wstring join_path(const std::wstring& a, const std::wstring& b);

/* Encoding bridges. */
std::string wide_to_utf8(const wchar_t* s);
std::string ansi_to_utf8(const char* s);   /* CP_ACP -> UTF-8 */

/* Whole-file text read with BOM strip + UTF-8 validity heuristic; falls back
 * to ANSI(CP_ACP) decoding when the bytes are not valid UTF-8. */
bool read_file_text(const std::wstring& path, std::string& out_utf8);

/* Minimal INI reader: finds one key inside [section] (both case-insensitive),
 * ignores unknown lines (junk such as "translated by ..." is tolerated). */
bool ini_get(const std::wstring& path, const char* section, const char* key,
             std::string& out);

/* Naive JSON string getter: locates "key" and copies its quoted string value,
 * resolving the usual backslash escapes. Best-effort by design. */
bool json_get_string(const std::string& json, const char* key, std::string& out);

/* Stream-scan a (possibly huge) file for ASCII tokens; returns the first
 * matching token index or -1. Reads 1MB chunks with a 31-byte carry-over so a
 * token split across a chunk boundary is still found. */
int scan_tokens(const std::wstring& path, const char* const* tokens, size_t ntok);

/* Truncating copies into caller-owned fixed buffers (never split a UTF-8
 * sequence mid-character for the char variant). */
void copy_char(char* dst, size_t cap, const std::string& src);
void copy_wide(wchar_t* dst, size_t cap, const wchar_t* src);

} // namespace rmd

#endif /* RMD_INTERNAL_H */
