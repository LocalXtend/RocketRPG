// multi.cpp - 멀티 방장 쪽 캡처: 게임 화면(Windows.Graphics.Capture)과 게임 소리(프로세스 루프백)
// 화면: 게임 창은 RocketRPG 창의 자식이라 WGC가 직접 잡지 못하므로, 최상위 창을 잡아 게임 창 영역만 잘라 냅니다.
// 소리: 게임 프로세스(와 자식 프로세스)가 내는 소리만 받습니다. 방장 PC의 다른 소리(음악, 통화)는 섞이지 않습니다.
#include "util.hpp"
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi.h>
#include <dwmapi.h>
#include <roapi.h>
#include <winstring.h>
#include <inspectable.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <cstring>

// Windows.Graphics.Capture ABI 선언. mingw의 WinRT 헤더는 C++에서 boolean(=BYTE) 중복 정의로 컴파일되지 않아
// 쓰는 인터페이스만 직접 적습니다 (vtable 순서는 Windows SDK의 .idl과 같음).
namespace wgc {
struct SizeInt32 { INT32 Width, Height; };
constexpr int kBgra8 = 87;   // kBgra8

struct IDirect3DDevice : IInspectable { virtual HRESULT STDMETHODCALLTYPE Trim() = 0; };
struct IDirect3DSurface : IInspectable { virtual HRESULT STDMETHODCALLTYPE get_Description(void*) = 0; };
struct IDirect3DDxgiInterfaceAccess : IUnknown { virtual HRESULT STDMETHODCALLTYPE GetInterface(REFIID iid, void** p) = 0; };
struct IClosable : IInspectable { virtual HRESULT STDMETHODCALLTYPE Close() = 0; };
struct IGraphicsCaptureItemInterop : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE CreateForWindow(HWND window, REFIID iid, void** result) = 0;
    virtual HRESULT STDMETHODCALLTYPE CreateForMonitor(HMONITOR monitor, REFIID iid, void** result) = 0;
};
struct IGraphicsCaptureItem : IInspectable {
    virtual HRESULT STDMETHODCALLTYPE get_DisplayName(HSTRING*) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_Size(SizeInt32*) = 0;
    virtual HRESULT STDMETHODCALLTYPE add_Closed(void*, INT64*) = 0;
    virtual HRESULT STDMETHODCALLTYPE remove_Closed(INT64) = 0;
};
struct IDirect3D11CaptureFrame : IInspectable {
    virtual HRESULT STDMETHODCALLTYPE get_Surface(IDirect3DSurface**) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_SystemRelativeTime(INT64*) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_ContentSize(SizeInt32*) = 0;
};
struct IGraphicsCaptureSession : IInspectable { virtual HRESULT STDMETHODCALLTYPE StartCapture() = 0; };
struct IGraphicsCaptureSession2 : IInspectable {
    virtual HRESULT STDMETHODCALLTYPE get_IsCursorCaptureEnabled(BYTE*) = 0;
    virtual HRESULT STDMETHODCALLTYPE put_IsCursorCaptureEnabled(BYTE) = 0;
};
struct IGraphicsCaptureSession3 : IInspectable {
    virtual HRESULT STDMETHODCALLTYPE get_IsBorderRequired(BYTE*) = 0;
    virtual HRESULT STDMETHODCALLTYPE put_IsBorderRequired(BYTE) = 0;
};
struct IDirect3D11CaptureFramePool : IInspectable {
    virtual HRESULT STDMETHODCALLTYPE Recreate(IDirect3DDevice*, int format, INT32 buffers, SizeInt32 size) = 0;
    virtual HRESULT STDMETHODCALLTYPE TryGetNextFrame(IDirect3D11CaptureFrame**) = 0;
    virtual HRESULT STDMETHODCALLTYPE add_FrameArrived(void*, INT64*) = 0;
    virtual HRESULT STDMETHODCALLTYPE remove_FrameArrived(INT64) = 0;
    virtual HRESULT STDMETHODCALLTYPE CreateCaptureSession(IGraphicsCaptureItem*, IGraphicsCaptureSession**) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_DispatcherQueue(void**) = 0;
};
struct IDirect3D11CaptureFramePoolStatics2 : IInspectable {
    virtual HRESULT STDMETHODCALLTYPE CreateFreeThreaded(IDirect3DDevice*, int format, INT32 buffers, SizeInt32 size, IDirect3D11CaptureFramePool**) = 0;
};
} // namespace wgc

__CRT_UUID_DECL(wgc::IDirect3DDevice, 0xa37624ab, 0x8d5f, 0x4650, 0x9d,0x3e, 0x9e,0xae,0x3d,0x9b,0xc6,0x70)
__CRT_UUID_DECL(wgc::IDirect3DDxgiInterfaceAccess, 0xa9b3d012, 0x3df2, 0x4ee3, 0xb8,0xd1, 0x86,0x95,0xf4,0x57,0xd3,0xc1)
__CRT_UUID_DECL(wgc::IClosable, 0x30d5a829, 0x7fa4, 0x4026, 0x83,0xbb, 0xd7,0x5b,0xae,0x4e,0xa9,0x9e)
__CRT_UUID_DECL(wgc::IGraphicsCaptureItemInterop, 0x3628e81b, 0x3cac, 0x4c60, 0xb7,0xf4, 0x23,0xce,0x0e,0x0c,0x33,0x56)
__CRT_UUID_DECL(wgc::IGraphicsCaptureItem, 0x79c3f95b, 0x31f7, 0x4ec2, 0xa4,0x64, 0x63,0x2e,0xf5,0xd3,0x07,0x60)
__CRT_UUID_DECL(wgc::IDirect3D11CaptureFrame, 0xfa50c623, 0x38da, 0x4b32, 0xac,0xf3, 0xfa,0x97,0x34,0xad,0x80,0x0e)
__CRT_UUID_DECL(wgc::IGraphicsCaptureSession, 0x814e42a9, 0xf70f, 0x4ad7, 0x93,0x9b, 0xfd,0xdc,0xc6,0xeb,0x88,0x0d)
__CRT_UUID_DECL(wgc::IGraphicsCaptureSession2, 0x2c39ae40, 0x7d2e, 0x5044, 0x80,0x4e, 0x8b,0x67,0x99,0xd4,0xcf,0x9e)
__CRT_UUID_DECL(wgc::IGraphicsCaptureSession3, 0xf2cdd966, 0x22ae, 0x5ea1, 0x95,0x96, 0x3a,0x28,0x93,0x44,0xc3,0xbe)
__CRT_UUID_DECL(wgc::IDirect3D11CaptureFramePool, 0x24eb6d22, 0x1975, 0x422e, 0x82,0xe7, 0x78,0x0d,0xbd,0x8d,0xdf,0x24)
__CRT_UUID_DECL(wgc::IDirect3D11CaptureFramePoolStatics2, 0x589b103f, 0x6bbc, 0x5df5, 0xa9,0x91, 0x02,0xe2,0x8b,0x3b,0x66,0xd5)

extern "C" HRESULT WINAPI CreateDirect3D11DeviceFromDXGIDevice(IDXGIDevice* dxgi, IInspectable** device);

using namespace wgc;

namespace {

template <class T> void release(T*& p) { if (p) { p->Release(); p = nullptr; } }

HRESULT activation_factory(const wchar_t* cls, REFIID iid, void** out) {
    HSTRING name = nullptr;
    HRESULT hr = WindowsCreateString(cls, (UINT32)wcslen(cls), &name);
    if (FAILED(hr)) return hr;
    hr = RoGetActivationFactory(name, iid, out);
    WindowsDeleteString(name);
    return hr;
}

// ─────────────────────────────── 화면 ───────────────────────────────
struct ScreenCap {
    HWND top = nullptr;
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    IDirect3DDevice* rt_dev = nullptr;
    IGraphicsCaptureItem* item = nullptr;
    IDirect3D11CaptureFramePool* pool = nullptr;
    IGraphicsCaptureSession* session = nullptr;
    SizeInt32 pool_size{};
    ID3D11Texture2D* staging = nullptr;
    UINT st_w = 0, st_h = 0;
    bool pending = false;   // staging에 복사를 걸어 두고 GPU가 끝내기를 기다리는 중
    // GPU 축소 (셰이더): 큰 게임 화면을 CPU로 줄이면 한 장에 8ms 넘게 걸려서. 준비 못 하면 CPU로 줄임.
    bool gpu_tried = false, gpu_ok = false;
    ID3D11VertexShader* vs = nullptr;
    ID3D11PixelShader* ps = nullptr;
    ID3D11SamplerState* sampler = nullptr;
    ID3D11Buffer* cb = nullptr;
    ID3D11Texture2D* crop_tex = nullptr;
    ID3D11ShaderResourceView* crop_srv = nullptr;
    UINT crop_w = 0, crop_h = 0;
    ID3D11Texture2D* out_tex = nullptr;
    ID3D11RenderTargetView* out_rtv = nullptr;
    UINT out_w = 0, out_h = 0;

    ~ScreenCap() {
        if (session) {
            IClosable* c = nullptr;
            if (SUCCEEDED(session->QueryInterface(__uuidof(IClosable), (void**)&c))) { c->Close(); c->Release(); }
        }
        if (pool) {
            IClosable* c = nullptr;
            if (SUCCEEDED(pool->QueryInterface(__uuidof(IClosable), (void**)&c))) { c->Close(); c->Release(); }
        }
        release(out_rtv); release(out_tex); release(crop_srv); release(crop_tex);
        release(cb); release(sampler); release(ps); release(vs);
        release(staging); release(session); release(pool); release(item); release(rt_dev); release(ctx); release(dev);
    }
};

bool cap_setup(ScreenCap* c, std::string& err) {
    D3D_FEATURE_LEVEL fl;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                   nullptr, 0, D3D11_SDK_VERSION, &c->dev, &fl, &c->ctx);
    if (FAILED(hr)) { err = "D3D11CreateDevice"; return false; }
    IDXGIDevice* dxgi = nullptr;
    if (FAILED(c->dev->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgi))) { err = "IDXGIDevice"; return false; }
    IInspectable* insp = nullptr;
    hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi, &insp);
    dxgi->Release();
    if (FAILED(hr)) { err = "CreateDirect3D11DeviceFromDXGIDevice"; return false; }
    hr = insp->QueryInterface(__uuidof(IDirect3DDevice), (void**)&c->rt_dev);
    insp->Release();
    if (FAILED(hr)) { err = "IDirect3DDevice"; return false; }

    IGraphicsCaptureItemInterop* interop = nullptr;
    hr = activation_factory(L"Windows.Graphics.Capture.GraphicsCaptureItem", __uuidof(IGraphicsCaptureItemInterop), (void**)&interop);
    if (FAILED(hr)) { err = "GraphicsCaptureItem factory"; return false; }
    hr = interop->CreateForWindow(c->top, __uuidof(IGraphicsCaptureItem), (void**)&c->item);
    interop->Release();
    if (FAILED(hr) || !c->item) { err = "CreateForWindow"; return false; }
    c->item->get_Size(&c->pool_size);

    IDirect3D11CaptureFramePoolStatics2* statics = nullptr;
    hr = activation_factory(L"Windows.Graphics.Capture.Direct3D11CaptureFramePool", __uuidof(IDirect3D11CaptureFramePoolStatics2), (void**)&statics);
    if (FAILED(hr)) { err = "FramePool factory"; return false; }
    hr = statics->CreateFreeThreaded(c->rt_dev, kBgra8, 2, c->pool_size, &c->pool);
    statics->Release();
    if (FAILED(hr)) { err = "CreateFreeThreaded"; return false; }
    if (FAILED(c->pool->CreateCaptureSession(c->item, &c->session))) { err = "CreateCaptureSession"; return false; }

    // 커서는 참가자에게 보일 필요가 없고, 노란 테두리는 방장 화면을 가립니다 (Windows 11부터 끌 수 있음)
    IGraphicsCaptureSession2* s2 = nullptr;
    if (SUCCEEDED(c->session->QueryInterface(__uuidof(IGraphicsCaptureSession2), (void**)&s2))) { s2->put_IsCursorCaptureEnabled(0); s2->Release(); }
    IGraphicsCaptureSession3* s3 = nullptr;
    if (SUCCEEDED(c->session->QueryInterface(__uuidof(IGraphicsCaptureSession3), (void**)&s3))) { s3->put_IsBorderRequired(0); s3->Release(); }

    if (FAILED(c->session->StartCapture())) { err = "StartCapture"; return false; }
    return true;
}

// 캡처된 창 그림에서 target의 클라이언트 영역이 차지하는 사각형 (그림 좌표)
bool crop_rect(ScreenCap* c, HWND target, SizeInt32 content, RECT& out) {
    RECT frame{};
    if (FAILED(DwmGetWindowAttribute(c->top, DWMWA_EXTENDED_FRAME_BOUNDS, &frame, sizeof frame)) && !GetWindowRect(c->top, &frame))
        return false;
    RECT rc{};
    if (!target || !IsWindow(target) || !GetClientRect(target, &rc)) return false;
    POINT p{0, 0};
    ClientToScreen(target, &p);
    out.left = std::max<LONG>(0, p.x - frame.left);
    out.top = std::max<LONG>(0, p.y - frame.top);
    out.right = std::min<LONG>(content.Width, p.x - frame.left + rc.right);
    out.bottom = std::min<LONG>(content.Height, p.y - frame.top + rc.bottom);
    return out.right - out.left >= 16 && out.bottom - out.top >= 16;
}

// BGRA 확대/축소 (쌍선형, 고정소수점). 축소 비율이 2를 넘는 일은 드물어 이것으로 충분합니다.
void scale_bgra(const uint8_t* src, int sw, int sh, int spitch, uint8_t* dst, int dw, int dh) {
    if (sw == dw && sh == dh) {
        for (int y = 0; y < dh; y++) {
            const uint32_t* s = (const uint32_t*)(src + (size_t)y * spitch);
            uint32_t* d = (uint32_t*)(dst + (size_t)y * dw * 4);
            for (int x = 0; x < dw; x++) d[x] = s[x] | 0xFF000000u;
        }
        return;
    }
    const int64_t fx = ((int64_t)sw << 16) / dw, fy = ((int64_t)sh << 16) / dh;
    for (int y = 0; y < dh; y++) {
        int64_t sy = std::max<int64_t>(0, (y * fy) + (fy >> 1) - 0x8000);
        int y0 = (int)(sy >> 16), y1 = std::min(y0 + 1, sh - 1);
        uint32_t wy = (uint32_t)(sy & 0xFFFF) >> 8;
        const uint8_t* r0 = src + (size_t)y0 * spitch;
        const uint8_t* r1 = src + (size_t)y1 * spitch;
        uint8_t* d = dst + (size_t)y * dw * 4;
        for (int x = 0; x < dw; x++) {
            int64_t sx = std::max<int64_t>(0, (x * fx) + (fx >> 1) - 0x8000);
            int x0 = (int)(sx >> 16), x1 = std::min(x0 + 1, sw - 1);
            uint32_t wx = (uint32_t)(sx & 0xFFFF) >> 8;
            const uint8_t *a = r0 + x0 * 4, *b = r0 + x1 * 4, *cc = r1 + x0 * 4, *e = r1 + x1 * 4;
            for (int k = 0; k < 3; k++) {
                uint32_t top = a[k] * (256 - wx) + b[k] * wx;
                uint32_t bot = cc[k] * (256 - wx) + e[k] * wx;
                d[k] = (uint8_t)((top * (256 - wy) + bot * wy) >> 16);
            }
            d[3] = 255;
            d += 4;
        }
    }
}

// ─────────────────────────────── 소리 ───────────────────────────────
// audioclientactivationparams.h (mingw 헤더에 없음)
enum rr_activation_type { RR_ACTIVATION_DEFAULT = 0, RR_ACTIVATION_PROCESS_LOOPBACK = 1 };
enum rr_loopback_mode { RR_LOOPBACK_INCLUDE_TREE = 0, RR_LOOPBACK_EXCLUDE_TREE = 1 };
struct rr_activation_params {
    rr_activation_type type;
    struct { DWORD pid; rr_loopback_mode mode; } loopback;
};

struct ActivateHandler final : IActivateAudioInterfaceCompletionHandler, IAgileObject {
    LONG refs = 1;
    HANDLE done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    ~ActivateHandler() { CloseHandle(done); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override {
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IActivateAudioInterfaceCompletionHandler))
            *out = static_cast<IActivateAudioInterfaceCompletionHandler*>(this);
        else if (iid == __uuidof(IAgileObject)) *out = static_cast<IAgileObject*>(this);
        else { *out = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { LONG r = InterlockedDecrement(&refs); if (!r) delete this; return r; }
    HRESULT STDMETHODCALLTYPE ActivateCompleted(IActivateAudioInterfaceAsyncOperation*) override { SetEvent(done); return S_OK; }
};

constexpr int kRingFrames = 48000 * 2;   // 2초

struct Loopback {
    IAudioClient* client = nullptr;
    IAudioCaptureClient* capture = nullptr;
    HANDLE event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    HANDLE thread = nullptr;
    volatile LONG stop = 0;
    CRITICAL_SECTION cs;
    std::vector<int16_t> ring = std::vector<int16_t>(kRingFrames * 2);
    int head = 0, count = 0;   // frames

    Loopback() { InitializeCriticalSection(&cs); }
    ~Loopback() {
        if (thread) { InterlockedExchange(&stop, 1); SetEvent(event); WaitForSingleObject(thread, 2000); CloseHandle(thread); }
        if (client) client->Stop();
        release(capture); release(client);
        CloseHandle(event);
        DeleteCriticalSection(&cs);
    }

    void push(const int16_t* src, int frames) {
        EnterCriticalSection(&cs);
        for (int i = 0; i < frames; i++) {
            int at = (head + count) % kRingFrames;
            ring[at * 2] = src ? src[i * 2] : 0;
            ring[at * 2 + 1] = src ? src[i * 2 + 1] : 0;
            if (count < kRingFrames) count++;
            else head = (head + 1) % kRingFrames;   // 가득 차면 가장 오래된 것을 버림
        }
        LeaveCriticalSection(&cs);
    }
};

DWORD WINAPI loopback_thread(void* arg) {
    auto* lb = static_cast<Loopback*>(arg);
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    while (!lb->stop) {
        WaitForSingleObject(lb->event, 100);
        UINT32 packet = 0;
        while (!lb->stop && SUCCEEDED(lb->capture->GetNextPacketSize(&packet)) && packet > 0) {
            BYTE* data = nullptr;
            UINT32 frames = 0;
            DWORD flags = 0;
            if (FAILED(lb->capture->GetBuffer(&data, &frames, &flags, nullptr, nullptr))) break;
            lb->push((flags & AUDCLNT_BUFFERFLAGS_SILENT) ? nullptr : (const int16_t*)data, (int)frames);
            lb->capture->ReleaseBuffer(frames);
        }
    }
    CoUninitialize();
    return 0;
}

} // namespace

MULTI_EXPORT int32_t rpg_cap_open(void* top_hwnd, void** out_cap) {
    if (!top_hwnd || !out_cap) RPG_FAIL(RP_ERR_INVALID, "cap_open: args");
    *out_cap = nullptr;
    HRESULT ro = RoInitialize(RO_INIT_MULTITHREADED);
    (void)ro;   // 이미 초기화된 스레드면 그대로 씁니다
    auto* c = new ScreenCap();
    c->top = (HWND)top_hwnd;
    std::string err;
    if (!cap_setup(c, err)) {
        delete c;
        rr::log_line("multi", "capture setup failed: " + err);
        RPG_FAIL(RP_ERR_UNSUPPORTED, "capture: " + err);
    }
    *out_cap = c;
    RPG_OK();
}

// 화면 전체를 덮는 삼각형 하나로 그리며, 출력 한 칸마다 원본을 4번(1/4칸씩 비켜) 읽어 평균 (2배까지 줄여도 깨끗함)
const char kScaleHlsl[] = R"(
Texture2D src : register(t0);
SamplerState smp : register(s0);
cbuffer params : register(b0) { float2 quarter; float2 pad; };
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V vs_main(uint id : SV_VertexID) {
    V o;
    float2 t = float2((id << 1) & 2, id & 2);
    o.pos = float4(t * float2(2, -2) + float2(-1, 1), 0, 1);
    o.uv = t;
    return o;
}
float4 ps_main(V i) : SV_Target {
    float3 c = src.Sample(smp, i.uv + float2(-quarter.x, -quarter.y)).rgb
             + src.Sample(smp, i.uv + float2( quarter.x, -quarter.y)).rgb
             + src.Sample(smp, i.uv + float2(-quarter.x,  quarter.y)).rgb
             + src.Sample(smp, i.uv + float2( quarter.x,  quarter.y)).rgb;
    return float4(c * 0.25, 1);
}
)";

bool gpu_setup(ScreenCap* c) {
    if (c->gpu_tried) return c->gpu_ok;
    c->gpu_tried = true;
    using compile_fn = HRESULT(WINAPI*)(const void*, SIZE_T, const char*, const D3D_SHADER_MACRO*, ID3DInclude*,
                                        const char*, const char*, UINT, UINT, ID3DBlob**, ID3DBlob**);
    HMODULE lib = LoadLibraryW(L"d3dcompiler_47.dll");
    auto compile = lib ? (compile_fn)(void*)GetProcAddress(lib, "D3DCompile") : nullptr;
    if (!compile) { rr::log_line("multi", "d3dcompiler_47 unavailable, scaling on CPU"); return false; }
    ID3DBlob *vsb = nullptr, *psb = nullptr, *err = nullptr;
    bool ok = SUCCEEDED(compile(kScaleHlsl, sizeof kScaleHlsl - 1, "scale", nullptr, nullptr, "vs_main", "vs_4_0", 0, 0, &vsb, &err)) &&
              SUCCEEDED(compile(kScaleHlsl, sizeof kScaleHlsl - 1, "scale", nullptr, nullptr, "ps_main", "ps_4_0", 0, 0, &psb, &err)) &&
              SUCCEEDED(c->dev->CreateVertexShader(vsb->GetBufferPointer(), vsb->GetBufferSize(), nullptr, &c->vs)) &&
              SUCCEEDED(c->dev->CreatePixelShader(psb->GetBufferPointer(), psb->GetBufferSize(), nullptr, &c->ps));
    if (err) { if (!ok) rr::log_line("multi", std::string("shader: ") + (const char*)err->GetBufferPointer()); err->Release(); }
    release(vsb); release(psb);
    if (ok) {
        D3D11_SAMPLER_DESC sd{};
        sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sd.MaxLOD = D3D11_FLOAT32_MAX;
        D3D11_BUFFER_DESC bd{};
        bd.ByteWidth = 16;
        bd.Usage = D3D11_USAGE_DEFAULT;
        bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        ok = SUCCEEDED(c->dev->CreateSamplerState(&sd, &c->sampler)) && SUCCEEDED(c->dev->CreateBuffer(&bd, nullptr, &c->cb));
    }
    c->gpu_ok = ok;
    if (!ok) rr::log_line("multi", "GPU scaling setup failed, scaling on CPU");
    return ok;
}

bool make_texture(ID3D11Device* dev, UINT w, UINT h, UINT bind, ID3D11Texture2D** out) {
    D3D11_TEXTURE2D_DESC d{};
    d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1;
    d.Format = DXGI_FORMAT_B8G8R8A8_UNORM; d.SampleDesc.Count = 1;
    d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = bind;
    return SUCCEEDED(dev->CreateTexture2D(&d, nullptr, out));
}

// 게임 영역(crop)을 ow×oh로 줄여 out_tex에 그림
bool gpu_scale(ScreenCap* c, ID3D11Texture2D* tex, const D3D11_BOX& box, UINT cw, UINT ch, UINT ow, UINT oh) {
    if (!c->crop_tex || c->crop_w != cw || c->crop_h != ch) {
        release(c->crop_srv); release(c->crop_tex);
        c->crop_w = c->crop_h = 0;
        if (!make_texture(c->dev, cw, ch, D3D11_BIND_SHADER_RESOURCE, &c->crop_tex) ||
            FAILED(c->dev->CreateShaderResourceView(c->crop_tex, nullptr, &c->crop_srv))) return false;
        c->crop_w = cw; c->crop_h = ch;
    }
    if (!c->out_tex || c->out_w != ow || c->out_h != oh) {
        release(c->out_rtv); release(c->out_tex);
        c->out_w = c->out_h = 0;
        if (!make_texture(c->dev, ow, oh, D3D11_BIND_RENDER_TARGET, &c->out_tex) ||
            FAILED(c->dev->CreateRenderTargetView(c->out_tex, nullptr, &c->out_rtv))) return false;
        c->out_w = ow; c->out_h = oh;
    }
    c->ctx->CopySubresourceRegion(c->crop_tex, 0, 0, 0, 0, tex, 0, &box);
    float params[4] = { 0.25f / ow, 0.25f / oh, 0, 0 };
    c->ctx->UpdateSubresource(c->cb, 0, nullptr, params, 0, 0);
    D3D11_VIEWPORT vp{ 0, 0, (float)ow, (float)oh, 0, 1 };
    c->ctx->OMSetRenderTargets(1, &c->out_rtv, nullptr);
    c->ctx->RSSetViewports(1, &vp);
    c->ctx->IASetInputLayout(nullptr);
    c->ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    c->ctx->VSSetShader(c->vs, nullptr, 0);
    c->ctx->PSSetShader(c->ps, nullptr, 0);
    c->ctx->PSSetShaderResources(0, 1, &c->crop_srv);
    c->ctx->PSSetSamplers(0, 1, &c->sampler);
    c->ctx->PSSetConstantBuffers(0, 1, &c->cb);
    c->ctx->Draw(3, 0);
    ID3D11ShaderResourceView* none = nullptr;
    c->ctx->PSSetShaderResources(0, 1, &none);
    return true;
}

bool ensure_staging(ScreenCap* c, UINT w, UINT h) {
    if (c->staging && c->st_w == w && c->st_h == h) return true;
    release(c->staging);
    c->st_w = c->st_h = 0;
    D3D11_TEXTURE2D_DESC d{};
    d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1;
    d.Format = DXGI_FORMAT_B8G8R8A8_UNORM; d.SampleDesc.Count = 1;
    d.Usage = D3D11_USAGE_STAGING; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    if (FAILED(c->dev->CreateTexture2D(&d, nullptr, &c->staging))) return false;
    c->st_w = w; c->st_h = h;
    return true;
}

// 걸어 둔 복사가 끝났으면 읽어서 줄여 dst에 씀. 아직이면 RP_ERR_STATE (pending 유지).
static int32_t finish_copy(ScreenCap* c, int32_t max_w, int32_t max_h, void* dst, uint32_t cap_bytes, int32_t* out_w, int32_t* out_h) {
    D3D11_MAPPED_SUBRESOURCE m{};
    HRESULT hr = c->ctx->Map(c->staging, 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &m);
    if (hr == DXGI_ERROR_WAS_STILL_DRAWING) return RP_ERR_STATE;
    c->pending = false;
    if (FAILED(hr)) return RP_ERR_STATE;
    int32_t result = RP_ERR_STATE;
    UINT cw = c->st_w, ch = c->st_h;
    double s = std::min({ 1.0, (double)max_w / cw, (double)max_h / ch });
    int ow = std::max(16, (int)(cw * s)) & ~1, oh = std::max(16, (int)(ch * s)) & ~1;   // 영상 부호화는 짝수 크기를 원함
    if ((uint64_t)ow * oh * 4 <= cap_bytes) {
        scale_bgra((const uint8_t*)m.pData, (int)cw, (int)ch, (int)m.RowPitch, (uint8_t*)dst, ow, oh);
        *out_w = ow;
        *out_h = oh;
        result = RP_OK;
    }
    c->ctx->Unmap(c->staging, 0);
    return result;
}

MULTI_EXPORT int32_t rpg_cap_read(void* cap, void* target_hwnd, int32_t max_w, int32_t max_h, int32_t allow_new,
                                  void* dst, uint32_t cap_bytes, int32_t* out_w, int32_t* out_h, int32_t* out_took) {
    auto* c = static_cast<ScreenCap*>(cap);
    if (!c || !dst || !out_w || !out_h || !out_took || max_w < 16 || max_h < 16) return RP_ERR_INVALID;
    *out_took = 0;

    // 앞서 걸어 둔 복사: GPU를 기다리며 멈추지 않도록 끝났을 때만 읽음 (호출하는 쪽이 몇 ms마다 다시 부름)
    if (c->pending) {
        int32_t r = finish_copy(c, max_w, max_h, dst, cap_bytes, out_w, out_h);
        if (r == RP_OK || c->pending) return r;
    }

    // 쌓인 프레임 중 가장 최근 것만 씁니다. 아직 보낼 때가 아니면(초당 프레임 제한) 버림.
    IDirect3D11CaptureFrame* frame = nullptr;
    for (;;) {
        IDirect3D11CaptureFrame* f = nullptr;
        if (FAILED(c->pool->TryGetNextFrame(&f)) || !f) break;
        release(frame);
        frame = f;
    }
    if (!frame) return RP_ERR_STATE;
    if (!allow_new) { release(frame); return RP_ERR_STATE; }

    SizeInt32 content{};
    frame->get_ContentSize(&content);
    if (content.Width != c->pool_size.Width || content.Height != c->pool_size.Height) {
        c->pool_size = content;
        c->pool->Recreate(c->rt_dev, kBgra8, 2, content);
    }

    IDirect3DSurface* surface = nullptr;
    IDirect3DDxgiInterfaceAccess* access = nullptr;
    ID3D11Texture2D* tex = nullptr;
    RECT crop{};
    if (crop_rect(c, (HWND)target_hwnd, content, crop) &&
        SUCCEEDED(frame->get_Surface(&surface)) &&
        SUCCEEDED(surface->QueryInterface(__uuidof(IDirect3DDxgiInterfaceAccess), (void**)&access)) &&
        SUCCEEDED(access->GetInterface(__uuidof(ID3D11Texture2D), (void**)&tex))) {
        UINT cw = (UINT)(crop.right - crop.left), ch = (UINT)(crop.bottom - crop.top);
        double s = std::min({ 1.0, (double)max_w / cw, (double)max_h / ch });
        UINT ow = (UINT)std::max(16, (int)(cw * s)) & ~1u, oh = (UINT)std::max(16, (int)(ch * s)) & ~1u;
        D3D11_BOX box{ (UINT)crop.left, (UINT)crop.top, 0, (UINT)crop.right, (UINT)crop.bottom, 1 };
        bool queued = false;
        if ((ow != cw || oh != ch) && gpu_setup(c) && gpu_scale(c, tex, box, cw, ch, ow, oh) && ensure_staging(c, ow, oh)) {
            c->ctx->CopyResource(c->staging, c->out_tex);   // GPU에서 줄인 것을 읽음
            queued = true;
        } else if (ensure_staging(c, cw, ch)) {
            c->ctx->CopySubresourceRegion(c->staging, 0, 0, 0, 0, tex, 0, &box);   // 그대로 (줄일 게 있으면 CPU가 줄임)
            queued = true;
        }
        if (queued) {
            c->ctx->Flush();
            c->pending = true;
            *out_took = 1;
        }
    }
    release(tex); release(access); release(surface); release(frame);
    return c->pending ? finish_copy(c, max_w, max_h, dst, cap_bytes, out_w, out_h) : RP_ERR_STATE;
}

MULTI_EXPORT void rpg_cap_close(void* cap) {
    delete static_cast<ScreenCap*>(cap);
}

MULTI_EXPORT int32_t rpg_loopback_open(uint32_t pid, void** out_lb) {
    if (!pid || !out_lb) RPG_FAIL(RP_ERR_INVALID, "loopback_open: args");
    *out_lb = nullptr;
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    rr_activation_params params{};
    params.type = RR_ACTIVATION_PROCESS_LOOPBACK;
    params.loopback.pid = pid;
    params.loopback.mode = RR_LOOPBACK_INCLUDE_TREE;
    PROPVARIANT pv{};
    pv.vt = VT_BLOB;
    pv.blob.cbSize = sizeof params;
    pv.blob.pBlobData = (BYTE*)&params;

    auto* handler = new ActivateHandler();
    IActivateAudioInterfaceAsyncOperation* op = nullptr;
    HRESULT hr = ActivateAudioInterfaceAsync(L"VAD\\Process_Loopback", __uuidof(IAudioClient), &pv, handler, &op);
    if (FAILED(hr)) { handler->Release(); RPG_FAIL(RP_ERR_UNSUPPORTED, "process loopback unavailable"); }
    WaitForSingleObject(handler->done, 5000);
    HRESULT act = E_FAIL;
    IUnknown* unk = nullptr;
    op->GetActivateResult(&act, &unk);
    op->Release();
    handler->Release();
    if (FAILED(act) || !unk) { if (unk) unk->Release(); RPG_FAIL(RP_ERR_UNSUPPORTED, "process loopback activate failed"); }

    auto* lb = new Loopback();
    hr = unk->QueryInterface(__uuidof(IAudioClient), (void**)&lb->client);
    unk->Release();
    WAVEFORMATEX fmt{};
    fmt.wFormatTag = WAVE_FORMAT_PCM;
    fmt.nChannels = 2;
    fmt.nSamplesPerSec = 48000;
    fmt.wBitsPerSample = 16;
    fmt.nBlockAlign = 4;
    fmt.nAvgBytesPerSec = 48000 * 4;
    if (SUCCEEDED(hr))
        hr = lb->client->Initialize(AUDCLNT_SHAREMODE_SHARED,
                                    AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
                                    2000000, 0, &fmt, nullptr);
    if (SUCCEEDED(hr)) hr = lb->client->SetEventHandle(lb->event);
    if (SUCCEEDED(hr)) hr = lb->client->GetService(__uuidof(IAudioCaptureClient), (void**)&lb->capture);
    if (SUCCEEDED(hr)) hr = lb->client->Start();
    if (FAILED(hr)) {
        delete lb;
        char msg[64];
        snprintf(msg, sizeof msg, "loopback init 0x%08lx", (unsigned long)hr);
        rr::log_line("multi", msg);
        RPG_FAIL(RP_ERR_UNSUPPORTED, msg);
    }
    lb->thread = CreateThread(nullptr, 0, loopback_thread, lb, 0, nullptr);
    *out_lb = lb;
    RPG_OK();
}

MULTI_EXPORT int32_t rpg_loopback_read(void* handle, int16_t* dst, int32_t max_frames) {
    auto* lb = static_cast<Loopback*>(handle);
    if (!lb || !dst || max_frames <= 0) return 0;
    EnterCriticalSection(&lb->cs);
    int n = std::min(max_frames, lb->count);
    for (int i = 0; i < n; i++) {
        int at = (lb->head + i) % kRingFrames;
        dst[i * 2] = lb->ring[at * 2];
        dst[i * 2 + 1] = lb->ring[at * 2 + 1];
    }
    lb->head = (lb->head + n) % kRingFrames;
    lb->count -= n;
    LeaveCriticalSection(&lb->cs);
    return n;
}

MULTI_EXPORT void rpg_loopback_close(void* lb) {
    delete static_cast<Loopback*>(lb);
}
