// volume.cpp - per-process game volume (WASAPI session of a native runtime process: mkxp-z / EasyRPG Player)
#include "util.hpp"
#include <mmdeviceapi.h>
#include <audiopolicy.h>
#include <endpointvolume.h>
#include <cmath>
#include <cstdio>

namespace rr {

// Applies the volume to every audio session owned by pid on every active render endpoint.
// ISimpleAudioVolume only accepts 0..1, so values above 100% are clamped to 100%.
rstatus volume_set_for_pid(DWORD pid, double percent01) {
    if (!pid) RPG_FAIL(RP_ERR_STATE, "no pid");
    if (percent01 < 0) percent01 = 0;
    if (percent01 > 1) percent01 = 1;
    HRESULT co = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    IMMDeviceEnumerator* de = nullptr;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                  __uuidof(IMMDeviceEnumerator), (void**)&de);
    if (FAILED(hr)) {
        if (SUCCEEDED(co)) CoUninitialize();
        RPG_FAIL(RP_ERR_UNSUPPORTED, "WASAPI unavailable");
    }
    rstatus result = RP_ERR_NOTFOUND;
    IMMDeviceCollection* devs = nullptr;
    if (SUCCEEDED(de->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &devs))) {
        UINT dn = 0;
        devs->GetCount(&dn);
        for (UINT d = 0; d < dn; d++) {
            IMMDevice* dev = nullptr;
            if (FAILED(devs->Item(d, &dev))) continue;
            IAudioSessionManager2* mgr = nullptr;
            if (SUCCEEDED(dev->Activate(__uuidof(IAudioSessionManager2), CLSCTX_ALL, nullptr, (void**)&mgr))) {
                IAudioSessionEnumerator* en = nullptr;
                if (SUCCEEDED(mgr->GetSessionEnumerator(&en))) {
                    int n = 0;
                    en->GetCount(&n);
                    for (int i = 0; i < n; i++) {
                        IAudioSessionControl* ctl = nullptr;
                        if (FAILED(en->GetSession(i, &ctl))) continue;
                        IAudioSessionControl2* ctl2 = nullptr;
                        if (SUCCEEDED(ctl->QueryInterface(__uuidof(IAudioSessionControl2), (void**)&ctl2))) {
                            DWORD spid = 0;
                            ctl2->GetProcessId(&spid);
                            if (spid == pid) {
                                ISimpleAudioVolume* vol = nullptr;
                                if (SUCCEEDED(ctl2->QueryInterface(__uuidof(ISimpleAudioVolume), (void**)&vol))) {
                                    if (SUCCEEDED(vol->SetMasterVolume((float)percent01, nullptr))) result = RP_OK;
                                    vol->Release();
                                }
                            }
                            ctl2->Release();
                        }
                        ctl->Release();
                    }
                    en->Release();
                }
                mgr->Release();
            }
            dev->Release();
        }
        devs->Release();
    }
    de->Release();
    if (SUCCEEDED(co)) CoUninitialize();
    if (result == RP_OK) RPG_OK();
    set_last_error(result, "game audio session not found");
    return result;
}

} // namespace rr

using namespace rr;

RPG_EXPORT rstatus rpg_set_volume_for_pid(uint32_t pid, double percent) {
    return volume_set_for_pid((DWORD)pid, percent / 100.0);
}
