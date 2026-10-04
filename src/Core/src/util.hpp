// util.hpp - internal helpers (data-oriented, no exceptions)
#pragma once
#include <windows.h>
#include <string>
#include <vector>
#include "rocketrpg.h"
#include <functional>
#include <algorithm>

namespace rr {

std::wstring utf8_to_wide(const char* s);
std::string  wide_to_utf8(const wchar_t* s);
std::string  ansi_to_utf8(const char* s);      // CP949/CP932 -> UTF-8 (best effort via CP_ACP)
std::string  trim(const std::string& s);

bool   read_file_bin(const std::wstring& path, std::vector<uint8_t>& out);
bool   write_file_bin(const std::wstring& path, const std::string& data);
bool   read_file_text(const std::wstring& path, std::string& out_utf8); // ANSI-aware
bool   write_file_text(const std::wstring& path, const std::string& utf8);
bool   dir_exists(const std::wstring& p);
bool   file_exists(const std::wstring& p);
std::wstring join_path(const std::wstring& a, const std::wstring& b);

// INI (ANSI) simple reader: returns section->key->value of first section match
struct IniKV { std::string key, val; };
bool   ini_read(const std::wstring& path, const char* section, std::vector<IniKV>& out);
bool   ini_get(const std::wstring& path, const char* section, const char* key, std::string& out);

// tiny JSON helpers (object field extraction / escaping) - sufficient for System.json/package.json/config
std::string json_escape(const std::string& s);
bool   json_find_string(const std::string& json, const std::string& key, std::string& out);
bool   json_find_int(const std::string& json, const std::string& key, long long& out);

void   log_line(const char* tag, const std::string& msg);

std::wstring exe_of(const std::wstring& dir);      // defined in detect.cpp

void   set_last_error(rstatus st, const std::string& msg);
#define RPG_FAIL(st, msg) do { rr::set_last_error(st, msg); return (st); } while(0)
#define RPG_OK()          do { rr::set_last_error(RP_OK, ""); return RP_OK; } while(0)

} // namespace rr
