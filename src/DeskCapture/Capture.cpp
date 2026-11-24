#include <windows.h>
#include <roapi.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/Windows.Security.Authorization.AppCapabilityAccess.h>
#include <algorithm>
#include <memory>
#include <mutex>
#include <vector>
#include "GpuVideo.h"

using namespace winrt;
using winrt::Windows::Graphics::SizeInt32;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;

struct CaptureDevice {
    com_ptr<ID3D11Device> device;
    com_ptr<ID3D11DeviceContext> context;
    IDirect3DDevice surfaceDevice{nullptr};
    com_ptr<IGraphicsCaptureItemInterop> factory;
    bool shareable = false;
    CaptureDevice() {
        auto hr = CreateVideoDevice(device.put(), context.put());
        if (FAILED(hr)) hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, device.put(), nullptr, context.put());
        if (FAILED(hr)) check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, device.put(), nullptr, context.put()));
        if (auto lock = context.try_as<ID3D11Multithread>()) {
            lock->SetMultithreadProtected(TRUE);
            shareable = true;
        }
        com_ptr<IInspectable> inspectable;
        check_hresult(CreateDirect3D11DeviceFromDXGIDevice(device.as<IDXGIDevice>().get(), inspectable.put()));
        surfaceDevice = inspectable.as<IDirect3DDevice>();
        factory = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
    }
};

// A paired session keeps the device ready. The last lease releases it on disconnect.
static std::mutex deviceMutex;
static std::weak_ptr<CaptureDevice> cachedDevice;
static std::shared_ptr<CaptureDevice> acquireDevice() {
    std::lock_guard lock(deviceMutex);
    if (auto active = cachedDevice.lock()) {
        if (SUCCEEDED(active->device->GetDeviceRemovedReason())) return active;
    }
    auto fresh = std::make_shared<CaptureDevice>();
    if (fresh->shareable) cachedDevice = fresh;
    return fresh;
}
// Decoder state and swap chains stay per window. Only borrow a healthy,
// multithread-protected D3D11 device already held by the paired session.
bool BorrowVideoDevice(ID3D11Device** device, ID3D11DeviceContext** context) {
    std::lock_guard lock(deviceMutex);
    auto active = cachedDevice.lock();
    if (!active || !(active->device->GetCreationFlags() & D3D11_CREATE_DEVICE_VIDEO_SUPPORT) ||
        FAILED(active->device->GetDeviceRemovedReason())) return false;
    active->device.copy_to(device);
    active->context.copy_to(context);
    return true;
}
extern "C" __declspec(dllexport) ID3D11Device* BorrowEncoderDevice() noexcept {
    com_ptr<ID3D11Device> device;
    com_ptr<ID3D11DeviceContext> context;
    BorrowVideoDevice(device.put(), context.put());
    return device.detach();
}

struct Capture {
    HWND source;
    std::shared_ptr<CaptureDevice> setup = acquireDevice();
    com_ptr<ID3D11Device> device = setup->device;
    com_ptr<ID3D11DeviceContext> context = setup->context;
    com_ptr<ID3D11Texture2D> staging;
    com_ptr<ID3D11Texture2D> latest;
    D3D11_MAPPED_SUBRESOURCE mapped{};
    GraphicsCaptureItem item{nullptr};
    Direct3D11CaptureFramePool pool{nullptr};
    GraphicsCaptureSession session{nullptr};
    bool borderless = false;
    DWORD poolMs = 0, borderMs = 0, startMs = 0;
    std::shared_ptr<handle> arrived;
    Direct3D11CaptureFramePool::FrameArrived_revoker notification;
    SizeInt32 size{};
    int64_t frameTime = 0;

    Capture(HWND window, bool preparing) : source(window) {
        auto begin = GetTickCount64();
        using namespace winrt::Windows::Security::Authorization::AppCapabilityAccess;
        if (preparing && AppCapability::Create(L"graphicsCaptureWithoutBorder").CheckAccess() != AppCapabilityAccessStatus::Allowed)
            throw hresult_access_denied();
        check_hresult(setup->factory->CreateForWindow(window, guid_of<GraphicsCaptureItem>(), put_abi(item)));
        size = item.Size();
        pool = Direct3D11CaptureFramePool::CreateFreeThreaded(setup->surfaceDevice,
            DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
        session = pool.CreateCaptureSession(item);
        auto poolReady = GetTickCount64();
        poolMs = static_cast<DWORD>(poolReady - begin);
        try { session.IsCursorCaptureEnabled(false); } catch (hresult_no_interface const&) {}
        if (auto border = session.try_as<IGraphicsCaptureSession3>()) {
            try {
                if (preparing || GraphicsCaptureAccess::RequestAccessAsync(GraphicsCaptureAccessKind::Borderless).get() == AppCapabilityAccessStatus::Allowed) {
                    border.IsBorderRequired(false);
                    borderless = !border.IsBorderRequired();
                }
            } catch (hresult_error const&) { /* Keep the system border if Windows declines the request. */ }
        }
        if (preparing && !borderless) throw hresult_access_denied();
        arrived = std::make_shared<handle>(CreateEventW(nullptr, FALSE, FALSE, nullptr));
        auto borderReady = GetTickCount64();
        borderMs = static_cast<DWORD>(borderReady - poolReady);
        check_bool(static_cast<bool>(*arrived));
        notification = pool.FrameArrived(auto_revoke, [signal = arrived](auto const&, auto const&) {
            SetEvent(signal->get());
        });
        session.StartCapture();
        startMs = static_cast<DWORD>(GetTickCount64() - borderReady);
        RedrawWindow(source, nullptr, nullptr, RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN);
    }
    ~Capture() {
        unmap();
        notification.revoke();
        try { if (session) session.Close(); if (pool) pool.Close(); } catch (...) {}
    }

    void unmap() {
        if (mapped.pData) { context->Unmap(staging.get(), 0); mapped = {}; }
    }

    bool next(DWORD waitMs) {
        if (WaitForSingleObject(arrived->get(), waitMs) == WAIT_TIMEOUT) return false;
        auto frame = pool.TryGetNextFrame();
        if (!frame) return false;
        // If encoding ran behind, display the newest of the two pool buffers.
        if (auto newer = pool.TryGetNextFrame()) frame = std::move(newer);
        auto nextSize = frame.ContentSize();
        if (nextSize.Width <= 0 || nextSize.Height <= 0) return false;
        bool resized = nextSize.Width != size.Width || nextSize.Height != size.Height;
        if (resized) {
            unmap();
            staging = nullptr;
            latest = nullptr;
            size = nextSize;
        }
        auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        com_ptr<ID3D11Texture2D> texture;
        check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), texture.put_void()));
        D3D11_TEXTURE2D_DESC desc{};
        texture->GetDesc(&desc);
        bool fits = desc.Width >= static_cast<UINT>(size.Width) && desc.Height >= static_cast<UINT>(size.Height);
        if (fits) {
            if (!latest) {
                desc.Width = size.Width; desc.Height = size.Height;
                desc.Usage = D3D11_USAGE_DEFAULT;
                desc.BindFlags = D3D11_BIND_RENDER_TARGET;
                desc.CPUAccessFlags = desc.MiscFlags = 0;
                check_hresult(device->CreateTexture2D(&desc, nullptr, latest.put()));
            }
            unmap();
            // The first frame after shrinking can be the only update from a
            // static window. Its complete new content fits in the old pool.
            D3D11_BOX content{0, 0, 0, static_cast<UINT>(size.Width), static_cast<UINT>(size.Height), 1};
            context->CopySubresourceRegion(latest.get(), 0, 0, 0, 0, texture.get(), 0, &content);
            frameTime = frame.SystemRelativeTime().count();
        }
        if (resized) {
            texture = nullptr; access = nullptr; frame.Close();
            pool.Recreate(setup->surfaceDevice, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
            if (!fits) RedrawWindow(source, nullptr, nullptr, RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN);
        }
        return fits;
    }
    void readback() {
        if (mapped.pData || !latest) return;
        if (!staging) {
            D3D11_TEXTURE2D_DESC desc{};
            latest->GetDesc(&desc);
            desc.Usage = D3D11_USAGE_STAGING;
            desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            check_hresult(device->CreateTexture2D(&desc, nullptr, staging.put()));
        }
        context->CopyResource(staging.get(), latest.get());
        check_hresult(context->Map(staging.get(), 0, D3D11_MAP_READ, 0, &mapped));
    }
};

extern "C" __declspec(dllexport) void* PrepareCaptureDevice() noexcept {
    if (FAILED(RoInitialize(RO_INIT_MULTITHREADED))) return nullptr;
    std::shared_ptr<CaptureDevice>* lease = nullptr;
    try { lease = new std::shared_ptr<CaptureDevice>(acquireDevice()); } catch (...) {}
    RoUninitialize();
    return lease;
}
extern "C" __declspec(dllexport) void ReleaseCaptureDevice(void* lease) noexcept {
    delete static_cast<std::shared_ptr<CaptureDevice>*>(lease);
}
extern "C" __declspec(dllexport) HRESULT OpenWindowCapture(HWND window, BOOL preparing, Capture** capture) noexcept {
    HRESULT initialized = RoInitialize(RO_INIT_MULTITHREADED);
    if (FAILED(initialized)) return initialized;
    try { *capture = new Capture(window, preparing != FALSE); return S_OK; }
    catch (...) { RoUninitialize(); return to_hresult(); }
}
extern "C" __declspec(dllexport) HRESULT OpenCapture(HWND window, Capture** capture) noexcept {
    return OpenWindowCapture(window, FALSE, capture);
}
extern "C" __declspec(dllexport) HRESULT ReadWindowTexture(Capture* capture, DWORD waitMs, ID3D11Texture2D** data, int* width, int* height) noexcept {
    try {
        bool changed = capture->next(waitMs);
        *data = capture->latest.get();
        *width = capture->size.Width;
        *height = capture->size.Height;
        return changed ? S_OK : S_FALSE;
    } catch (...) { return to_hresult(); }
}
extern "C" __declspec(dllexport) HRESULT CaptureTexture(Capture* capture, ID3D11Texture2D** data, int* width, int* height) noexcept {
    return ReadWindowTexture(capture, 100, data, width, height);
}
extern "C" __declspec(dllexport) int64_t WindowFrameTime(Capture* capture) noexcept { return capture->frameTime; }
extern "C" __declspec(dllexport) HRESULT ReadCapturePixels(Capture* capture, BYTE** data, int* stride) noexcept {
    try {
        capture->readback();
        *data = static_cast<BYTE*>(capture->mapped.pData);
        *stride = static_cast<int>(capture->mapped.RowPitch);
        return S_OK;
    } catch (...) { return to_hresult(); }
}
// Scale the first WGC texture on the GPU. Only its small mip level crosses to CPU memory.
extern "C" __declspec(dllexport) HRESULT ReadOpeningImage(Capture* capture, UINT level,
        BYTE* pixels, int width, int height, int stride) noexcept {
    try {
        if (!capture->latest || !pixels || level > 14) return E_INVALIDARG;
        D3D11_TEXTURE2D_DESC desc{};
        capture->latest->GetDesc(&desc);
        if (static_cast<UINT>(width) != std::max(1u, desc.Width >> level) ||
            static_cast<UINT>(height) != std::max(1u, desc.Height >> level) || stride < width * 4) return E_INVALIDARG;
        auto& device = capture->device;
        auto& ctx = capture->context;
        auto source = capture->latest;
        if (level) {
            desc.MipLevels = level + 1;
            desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
            desc.MiscFlags = D3D11_RESOURCE_MISC_GENERATE_MIPS;
            com_ptr<ID3D11Texture2D> pyramid;
            check_hresult(device->CreateTexture2D(&desc, nullptr, pyramid.put()));
            ctx->CopySubresourceRegion(pyramid.get(), 0, 0, 0, 0, source.get(), 0, nullptr);
            com_ptr<ID3D11ShaderResourceView> view;
            check_hresult(device->CreateShaderResourceView(pyramid.get(), nullptr, view.put()));
            ctx->GenerateMips(view.get());
            source = std::move(pyramid);
        }
        desc.Width = width; desc.Height = height; desc.MipLevels = 1;
        desc.BindFlags = desc.MiscFlags = 0;
        desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        com_ptr<ID3D11Texture2D> staging;
        check_hresult(device->CreateTexture2D(&desc, nullptr, staging.put()));
        ctx->CopySubresourceRegion(staging.get(), 0, 0, 0, 0, source.get(), level, nullptr);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        check_hresult(ctx->Map(staging.get(), 0, D3D11_MAP_READ, 0, &mapped));
        auto src = static_cast<const BYTE*>(mapped.pData);
        for (int y = 0; y < height; ++y)
            memcpy(pixels + y * stride, src + y * mapped.RowPitch, width * 4);
        ctx->Unmap(staging.get(), 0);
        return S_OK;
    } catch (...) { return to_hresult(); }
}
extern "C" __declspec(dllexport) BOOL BorderlessCapture(Capture* capture) noexcept {
    return capture->borderless;
}
extern "C" __declspec(dllexport) ID3D11Device* CaptureEncoderDevice(Capture* capture, int* width, int* height) noexcept {
    *width = capture->size.Width;
    *height = capture->size.Height;
    return capture->device.get();
}
extern "C" __declspec(dllexport) void CaptureSetupTimes(Capture* capture, DWORD* pool, DWORD* border, DWORD* start) noexcept {
    *pool = capture->poolMs; *border = capture->borderMs; *start = capture->startMs;
}
extern "C" __declspec(dllexport) void CloseCapture(Capture* capture) noexcept {
    delete capture;
    RoUninitialize();
}
