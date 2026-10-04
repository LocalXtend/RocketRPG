// overlay.cpp - 멀티: RocketRPG 창 맨 위에 그리는 채팅(흐르는 글)과 핑
// DirectComposition "맨 위" 대상으로 창의 모든 자식 창(다른 프로세스의 게임 창, WebView2 포함) 위에 그립니다.
// 창 자체의 합성에 포함되므로 디스코드 창 공유에도 보이고, 방장 방송 영상(게임이 직접 주는 화면)에는 섞이지 않습니다.
// 움직임은 DirectComposition 애니메이션이 맡아 CPU가 프레임마다 다시 그리지 않습니다.
#include "util.hpp"
#include <d3d11.h>
#include <dxgi.h>
#include <d2d1_1.h>
#include <dwrite.h>
#include <dcomp.h>
#include <dcompanimation.h>
#include <cmath>
#include <map>

namespace {

template <class T> void release(T*& p) { if (p) { p->Release(); p = nullptr; } }

double now_s() {
    static LARGE_INTEGER freq{};
    if (!freq.QuadPart) QueryPerformanceFrequency(&freq);
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return (double)t.QuadPart / (double)freq.QuadPart;
}

constexpr double kChatSeconds = 7.0;    // 흐르는 글이 화면을 가로지르는 시간 (길면 빨리 흐름)
constexpr double kFixedSeconds = 6.0;   // 고정 글이 한 줄을 차지하는 시간 (보낼 수 있는 간격 90초는 서버가 정함)
constexpr double kFadeSeconds = 0.5;

struct Item {
    IDCompositionVisual* visual = nullptr;
    IDCompositionSurface* surface = nullptr;
    IDCompositionEffectGroup* effect = nullptr;
    IDCompositionScaleTransform* scale = nullptr;
    double expires = 0;          // 이 시각이 지나면 지움 (0 = 계속)
    float x = 0, y = 0;          // 핑: 지금 목표 위치 (영역 기준 픽셀, 가운데)
    float half = 0;              // 핑: 그림 반쪽 크기
    void free() { release(scale); release(effect); release(surface); release(visual); }
};

struct Lane { double enter = -1e9; float width = 0, speed = 1; double fixed_until = 0; };

struct Overlay {
    HWND hwnd = nullptr;
    ID3D11Device* d3d = nullptr;
    ID2D1Factory1* d2f = nullptr;
    ID2D1Device* d2dev = nullptr;
    ID2D1DeviceContext* dc = nullptr;
    IDWriteFactory* dw = nullptr;
    IDCompositionDevice* dcomp = nullptr;
    IDCompositionTarget* target = nullptr;
    IDCompositionVisual* root = nullptr;
    IDCompositionVisual* chats = nullptr;   // 흐르는 글 층
    IDCompositionVisual* pings = nullptr;   // 핑 층 (글 위)
    int ax = 0, ay = 0, aw = 0, ah = 0;
    std::vector<Item> chat_items;
    std::map<int, Item> ping_items;
    std::vector<Item> fading;
    std::vector<Lane> lanes;

    ~Overlay() {
        for (auto& i : chat_items) i.free();
        for (auto& [_, i] : ping_items) i.free();
        for (auto& i : fading) i.free();
        if (target) target->SetRoot(nullptr);
        if (dcomp) dcomp->Commit();
        release(pings); release(chats); release(root); release(target); release(dcomp);
        release(dw); release(dc); release(d2dev); release(d2f); release(d3d);
    }
};

bool setup(Overlay* o) {
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                 nullptr, 0, D3D11_SDK_VERSION, &o->d3d, nullptr, nullptr))) return false;
    IDXGIDevice* dxgi = nullptr;
    if (FAILED(o->d3d->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgi))) return false;
    bool ok = SUCCEEDED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, __uuidof(ID2D1Factory1), nullptr, (void**)&o->d2f)) &&
              SUCCEEDED(o->d2f->CreateDevice(dxgi, &o->d2dev)) &&
              SUCCEEDED(o->d2dev->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &o->dc)) &&
              SUCCEEDED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), (IUnknown**)&o->dw)) &&
              SUCCEEDED(DCompositionCreateDevice(dxgi, __uuidof(IDCompositionDevice), (void**)&o->dcomp)) &&
              SUCCEEDED(o->dcomp->CreateTargetForHwnd(o->hwnd, TRUE, &o->target)) &&
              SUCCEEDED(o->dcomp->CreateVisual(&o->root)) &&
              SUCCEEDED(o->dcomp->CreateVisual(&o->chats)) &&
              SUCCEEDED(o->dcomp->CreateVisual(&o->pings));
    dxgi->Release();
    if (!ok) return false;
    o->root->AddVisual(o->chats, FALSE, nullptr);
    o->root->AddVisual(o->pings, TRUE, o->chats);
    o->target->SetRoot(o->root);
    o->dcomp->Commit();
    return true;
}

// 투명 표면을 만들어 D2D로 그림
template <class Draw>
IDCompositionSurface* paint(Overlay* o, UINT w, UINT h, Draw&& draw) {
    IDCompositionSurface* s = nullptr;
    if (FAILED(o->dcomp->CreateSurface(w, h, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_ALPHA_MODE_PREMULTIPLIED, &s))) return nullptr;
    IDXGISurface* dxgi = nullptr;
    POINT off{};
    if (FAILED(s->BeginDraw(nullptr, __uuidof(IDXGISurface), (void**)&dxgi, &off))) { s->Release(); return nullptr; }
    D2D1_BITMAP_PROPERTIES1 bp{};
    bp.pixelFormat = { DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED };
    bp.bitmapOptions = D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW;
    ID2D1Bitmap1* bmp = nullptr;
    if (SUCCEEDED(o->dc->CreateBitmapFromDxgiSurface(dxgi, &bp, &bmp))) {
        o->dc->SetTarget(bmp);
        o->dc->BeginDraw();
        o->dc->SetTransform(D2D1::Matrix3x2F::Translation((float)off.x, (float)off.y));
        o->dc->Clear(D2D1::ColorF(0, 0, 0, 0));
        draw(o->dc);
        o->dc->EndDraw();
        o->dc->SetTarget(nullptr);
        bmp->Release();
    }
    dxgi->Release();
    s->EndDraw();
    return s;
}

IDCompositionAnimation* linear(Overlay* o, float from, float to, double seconds) {
    IDCompositionAnimation* a = nullptr;
    if (FAILED(o->dcomp->CreateAnimation(&a))) return nullptr;
    a->AddCubic(0.0, from, (float)((to - from) / seconds), 0.f, 0.f);
    a->End(seconds, to);
    return a;
}

float font_px(Overlay* o) { return std::clamp(o->ah / 15.0f, 16.0f, 44.0f); }
float ping_r(Overlay* o) { return std::clamp(o->ah / 22.0f, 12.0f, 30.0f); }

void set_offset_anim(Overlay* o, IDCompositionVisual* v, bool x, float from, float to, double seconds) {
    if (auto* a = linear(o, from, to, seconds)) {
        if (x) v->SetOffsetX(a); else v->SetOffsetY(a);
        a->Release();
    } else if (x) v->SetOffsetX(to); else v->SetOffsetY(to);
}

// 핑을 서서히 지움 (tick에서 정리)
void fade_out(Overlay* o, Item& i) {
    if (i.effect) {
        if (auto* a = linear(o, 1.f, 0.f, kFadeSeconds)) { i.effect->SetOpacity(a); a->Release(); }
    }
    i.expires = now_s() + kFadeSeconds + 0.1;
    o->fading.push_back(i);
}

} // namespace

MULTI_EXPORT int32_t rpg_overlay_open(void* hwnd, void** out) {
    if (!hwnd || !out) return RP_ERR_INVALID;
    *out = nullptr;
    auto* o = new Overlay();
    o->hwnd = (HWND)hwnd;
    if (!setup(o)) {
        delete o;
        RPG_FAIL(RP_ERR_UNSUPPORTED, "overlay: DirectComposition unavailable");
    }
    *out = o;
    RPG_OK();
}

// 게임 화면 영역 (창 클라이언트 좌표, 물리 픽셀). 글과 핑은 이 영역 안에만 보입니다.
MULTI_EXPORT void rpg_overlay_area(void* ov, int32_t x, int32_t y, int32_t w, int32_t h) {
    auto* o = static_cast<Overlay*>(ov);
    if (!o || (o->ax == x && o->ay == y && o->aw == w && o->ah == h)) return;
    o->ax = x; o->ay = y; o->aw = std::max(0, w); o->ah = std::max(0, h);
    o->root->SetOffsetX((float)x);
    o->root->SetOffsetY((float)y);
    o->root->SetClip(D2D1::RectF(0, 0, (float)o->aw, (float)o->ah));
    o->dcomp->Commit();
}

// 흐르는 글 (오른쪽 → 왼쪽). 길수록 빨리 흘러 모두 같은 시간에 지나갑니다.
MULTI_EXPORT void rpg_overlay_chat(void* ov, const wchar_t* text, uint32_t color, int32_t fixed_lane) {
    auto* o = static_cast<Overlay*>(ov);
    if (!o || !text || !*text || o->aw < 32 || o->ah < 32) return;
    float fs = font_px(o);
    IDWriteTextFormat* fmt = nullptr;
    if (FAILED(o->dw->CreateTextFormat(L"Malgun Gothic", nullptr, DWRITE_FONT_WEIGHT_BOLD, DWRITE_FONT_STYLE_NORMAL,
                                       DWRITE_FONT_STRETCH_NORMAL, fs, L"ko-kr", &fmt))) return;
    fmt->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
    IDWriteTextLayout* layout = nullptr;
    o->dw->CreateTextLayout(text, (UINT32)wcslen(text), fmt, 10000.f, fs * 2, &layout);
    fmt->Release();
    if (!layout) return;
    DWRITE_TEXT_METRICS m{};
    layout->GetMetrics(&m);
    const float pad = std::max(2.f, fs / 12.f);
    UINT tw = (UINT)std::ceil(m.widthIncludingTrailingWhitespace + pad * 2), th = (UINT)std::ceil(m.height + pad * 2);
    IDCompositionSurface* surf = paint(o, tw, th, [&](ID2D1DeviceContext* dc) {
        ID2D1SolidColorBrush *black = nullptr, *white = nullptr;
        dc->CreateSolidColorBrush(D2D1::ColorF(0, 0, 0, 0.85f), &black);
        dc->CreateSolidColorBrush(D2D1::ColorF(color & 0xFFFFFF), &white);
        // 테두리: 검은 글을 여덟 방향으로 비켜 그린 뒤 흰 글
        const float d = pad * 0.75f;
        for (int k = 0; k < 8; k++) {
            float a = k * 0.785398163f;
            dc->DrawTextLayout(D2D1::Point2F(pad + d * std::cos(a), pad + d * std::sin(a)), layout, black);
        }
        dc->DrawTextLayout(D2D1::Point2F(pad, pad), layout, white);
        release(black); release(white);
    });
    layout->Release();
    if (!surf) return;

    // 줄 고르기: 앞 글의 꼬리가 오른쪽 끝을 지났고, 새 글이 더 빨라도 앞 글을 따라잡지 않는 줄
    float lane_h = th * 1.05f;
    size_t count = (size_t)std::max(1.f, (o->ah * 0.75f) / lane_h);
    if (o->lanes.size() != count) o->lanes.assign(count, Lane{});
    double t = now_s();
    float speed = (o->aw + tw) / (float)kChatSeconds;
    const float gap = fs;
    size_t pick = 0;
    double best = 1e18;
    bool found = false;
    for (size_t i = 0; i < count && !found; i++) {
        const Lane& l = o->lanes[i];
        if (l.fixed_until > t) continue;
        if (fixed_lane) {
            if (l.enter + kChatSeconds <= t) { pick = i; found = true; }
            continue;
        }
        double clear = l.enter + (l.width + gap) / l.speed;          // 앞 글 꼬리가 오른쪽 끝을 지나는 때
        bool no_catch = speed <= l.speed || l.enter + kChatSeconds <= t + o->aw / speed;
        if (t >= clear && no_catch) { pick = i; found = true; }
        else if (clear < best) { best = clear; pick = i; }
    }
    // 고정 글은 빈 줄이 없으면 흐르는 글이 지나가는 줄이라도 맨 위 줄을 차지합니다 (그 뒤 흐르는 글은 이 줄을 피함).
    if (!found && fixed_lane) {
        for (size_t i = 0; i < count && !found; i++) if (o->lanes[i].fixed_until <= t) { pick = i; found = true; }
    }
    if (!found) { surf->Release(); return; } // 비어 있는 줄이 없으면 기존 글을 덮지 않습니다.
    o->lanes[pick] = fixed_lane ? Lane{ -1e9, 0, 1, t + kFixedSeconds } : Lane{ t, (float)tw, speed, 0 };

    Item it;
    it.surface = surf;
    if (FAILED(o->dcomp->CreateVisual(&it.visual))) { it.free(); return; }
    it.visual->SetContent(surf);
    it.visual->SetOffsetY(pick * lane_h + fs * 0.2f);
    if (fixed_lane) {
        float scale = std::min(1.f, (o->aw - 8.f) / tw);
        if (SUCCEEDED(o->dcomp->CreateScaleTransform(&it.scale))) {
            it.scale->SetScaleX(scale); it.scale->SetScaleY(scale); it.visual->SetTransform(it.scale);
        }
        it.visual->SetOffsetX((o->aw - tw * scale) * 0.5f);
    } else set_offset_anim(o, it.visual, true, (float)o->aw, -(float)tw, kChatSeconds);
    it.expires = t + (fixed_lane ? kFixedSeconds : kChatSeconds) + 0.1;
    o->chats->AddVisual(it.visual, TRUE, nullptr);
    o->chat_items.push_back(it);
    o->dcomp->Commit();
}

// 핑: 새로(fresh=1, 퍼지는 고리와 함께 나타남) 만들거나 옮김. nx, ny = 영역 안 비율 (0~1)
MULTI_EXPORT void rpg_overlay_ping(void* ov, int32_t id, float nx, float ny, int32_t fresh, uint32_t color) {
    auto* o = static_cast<Overlay*>(ov);
    if (!o || o->aw < 16 || o->ah < 16) return;
    float x = nx * o->aw, y = ny * o->ah;
    auto found = o->ping_items.find(id);
    if (found != o->ping_items.end() && !fresh) {
        Item& it = found->second;
        if (std::fabs(it.x - x) < 0.5f && std::fabs(it.y - y) < 0.5f) return;
        // 카메라 정보는 띄엄띄엄 오므로 0.1초 동안 미끄러지듯 옮김
        set_offset_anim(o, it.visual, true, it.x - it.half, x - it.half, 0.1);
        set_offset_anim(o, it.visual, false, it.y - it.half, y - it.half, 0.1);
        it.x = x; it.y = y;
        o->dcomp->Commit();
        return;
    }
    if (found != o->ping_items.end()) { fade_out(o, found->second); o->ping_items.erase(found); }

    float r = ping_r(o);
    UINT size = (UINT)std::ceil(r * 4);
    float c = size / 2.f;
    IDCompositionSurface* surf = paint(o, size, size, [&](ID2D1DeviceContext* dc) {
        ID2D1SolidColorBrush *dark = nullptr, *gold = nullptr;
        dc->CreateSolidColorBrush(D2D1::ColorF(0.05f, 0.05f, 0.05f, 0.85f), &dark);
        dc->CreateSolidColorBrush(D2D1::ColorF(color & 0xFFFFFF), &gold);
        auto ring = D2D1::Ellipse(D2D1::Point2F(c, c), r, r);
        dc->DrawEllipse(ring, dark, r * 0.42f);
        dc->DrawEllipse(ring, gold, r * 0.24f);
        auto dot = D2D1::Ellipse(D2D1::Point2F(c, c), r * 0.3f, r * 0.3f);
        dc->FillEllipse(D2D1::Ellipse(D2D1::Point2F(c, c), r * 0.42f, r * 0.42f), dark);
        dc->FillEllipse(dot, gold);
        release(dark); release(gold);
    });
    if (!surf) return;
    Item it;
    it.surface = surf;
    it.half = c;
    it.x = x; it.y = y;
    if (FAILED(o->dcomp->CreateVisual(&it.visual)) || FAILED(o->dcomp->CreateEffectGroup(&it.effect)) ||
        FAILED(o->dcomp->CreateScaleTransform(&it.scale))) { it.free(); return; }
    it.visual->SetContent(surf);
    it.visual->SetOffsetX(x - c);
    it.visual->SetOffsetY(y - c);
    // 나타날 때: 크게 퍼졌다가 제자리로 줄어들며 또렷해짐
    it.scale->SetCenterX(c);
    it.scale->SetCenterY(c);
    if (auto* a = linear(o, 1.8f, 1.0f, 0.25)) { it.scale->SetScaleX(a); it.scale->SetScaleY(a); a->Release(); }
    it.visual->SetTransform(it.scale);
    if (auto* a = linear(o, 0.f, 1.f, 0.15)) { it.effect->SetOpacity(a); a->Release(); }
    it.visual->SetEffect(it.effect);
    o->pings->AddVisual(it.visual, TRUE, nullptr);
    o->ping_items[id] = it;
    o->dcomp->Commit();
}

MULTI_EXPORT void rpg_overlay_remove(void* ov, int32_t id) {
    auto* o = static_cast<Overlay*>(ov);
    if (!o) return;
    auto found = o->ping_items.find(id);
    if (found == o->ping_items.end()) return;
    fade_out(o, found->second);
    o->ping_items.erase(found);
    o->dcomp->Commit();
}

// what: 1 = 흐르는 글, 2 = 핑, 3 = 모두
MULTI_EXPORT void rpg_overlay_clear(void* ov, int32_t what) {
    auto* o = static_cast<Overlay*>(ov);
    if (!o) return;
    if (what & 1) {
        for (auto& i : o->chat_items) { o->chats->RemoveVisual(i.visual); i.free(); }
        o->chat_items.clear();
        o->lanes.clear();
    }
    if (what & 2) {
        for (auto& [_, i] : o->ping_items) { o->pings->RemoveVisual(i.visual); i.free(); }
        o->ping_items.clear();
        for (auto& i : o->fading) { o->pings->RemoveVisual(i.visual); i.free(); }
        o->fading.clear();
    }
    o->dcomp->Commit();
}

// 다 흐른 글, 다 사라진 핑을 정리 (몇백 ms마다)
MULTI_EXPORT void rpg_overlay_tick(void* ov) {
    auto* o = static_cast<Overlay*>(ov);
    if (!o) return;
    double t = now_s();
    bool changed = false;
    auto sweep = [&](std::vector<Item>& items, IDCompositionVisual* parent) {
        for (size_t i = 0; i < items.size();) {
            if (items[i].expires > 0 && t >= items[i].expires) {
                parent->RemoveVisual(items[i].visual);
                items[i].free();
                items.erase(items.begin() + (long)i);
                changed = true;
            } else i++;
        }
    };
    sweep(o->chat_items, o->chats);
    sweep(o->fading, o->pings);
    if (changed) o->dcomp->Commit();
}

MULTI_EXPORT void rpg_overlay_close(void* ov) {
    delete static_cast<Overlay*>(ov);
}
