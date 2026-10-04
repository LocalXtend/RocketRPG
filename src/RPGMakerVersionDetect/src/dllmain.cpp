// dllmain.cpp - RPGMakerVersionDetect.dll entry point
//
// The library is stateless (no globals, no TLS, no threads it owns), so the
// DllMain body stays empty on purpose. rmd_abi() lives here next to the module
// entry because it is the smallest export and describes the module itself.

#include <windows.h>
#include "rmdetect.h"

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) {
    return TRUE;
}

// Library ABI revision. Must match the RMD_RESULT layout in rmdetect.h.
RMD_EXPORT int32_t rmd_abi(void) {
    return 1;
}
