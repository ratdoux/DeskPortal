#pragma once
#include <d3d11.h>
#include <dxgi1_6.h>
#include <winrt/base.h>

bool BorrowVideoDevice(ID3D11Device** device, ID3D11DeviceContext** context);

// WGC and the encoder must use the same adapter, including on hybrid laptops.
inline HRESULT CreateVideoDevice(ID3D11Device** device, ID3D11DeviceContext** context) noexcept {
    winrt::com_ptr<IDXGIFactory6> factory;
    const UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    if (SUCCEEDED(CreateDXGIFactory1(__uuidof(IDXGIFactory6), factory.put_void()))) {
        for (UINT i = 0; ; ++i) {
            winrt::com_ptr<IDXGIAdapter1> adapter;
            if (FAILED(factory->EnumAdapterByGpuPreference(i, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
                    __uuidof(IDXGIAdapter1), adapter.put_void()))) break;
            DXGI_ADAPTER_DESC1 desc{};
            if (FAILED(adapter->GetDesc1(&desc)) || (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)) continue;
            if (SUCCEEDED(D3D11CreateDevice(adapter.get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags,
                    nullptr, 0, D3D11_SDK_VERSION, device, nullptr, context))) return S_OK;
        }
    }
    return D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0,
        D3D11_SDK_VERSION, device, nullptr, context);
}

struct GpuColorConverter {
    winrt::com_ptr<ID3D11VideoDevice> video;
    winrt::com_ptr<ID3D11VideoContext> context;
    winrt::com_ptr<ID3D11VideoProcessorEnumerator> formats;
    winrt::com_ptr<ID3D11VideoProcessor> processor;
    void init(ID3D11Device* dev, int w, int h, int outW, int outH, bool toNv12) {
        using winrt::check_hresult;
        check_hresult(dev->QueryInterface(__uuidof(ID3D11VideoDevice), video.put_void()));
        winrt::com_ptr<ID3D11DeviceContext> immediate;
        dev->GetImmediateContext(immediate.put());
        context = immediate.as<ID3D11VideoContext>();
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc{};
        desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        desc.InputWidth = w; desc.InputHeight = h;
        desc.OutputWidth = outW; desc.OutputHeight = outH;
        desc.InputFrameRate = desc.OutputFrameRate = {60, 1};
        desc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        check_hresult(video->CreateVideoProcessorEnumerator(&desc, formats.put()));
        check_hresult(video->CreateVideoProcessor(formats.get(), 0, processor.put()));
        RECT src{0, 0, w, h}, dst{0, 0, outW, outH};
        context->VideoProcessorSetStreamAutoProcessingMode(processor.get(), 0, FALSE);
        context->VideoProcessorSetStreamFrameFormat(processor.get(), 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        context->VideoProcessorSetStreamSourceRect(processor.get(), 0, TRUE, &src);
        context->VideoProcessorSetStreamDestRect(processor.get(), 0, TRUE, &dst);
        context->VideoProcessorSetOutputTargetRect(processor.get(), TRUE, &dst);
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE rgb{}, yuv{};
        rgb.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_0_255;
        yuv.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
        context->VideoProcessorSetStreamColorSpace(processor.get(), 0, toNv12 ? &rgb : &yuv);
        context->VideoProcessorSetOutputColorSpace(processor.get(), toNv12 ? &yuv : &rgb);
    }
    void convert(ID3D11Texture2D* src, UINT slice, ID3D11Texture2D* dst) {
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC in{};
        in.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        in.Texture2D.ArraySlice = slice;
        winrt::com_ptr<ID3D11VideoProcessorInputView> input;
        winrt::check_hresult(video->CreateVideoProcessorInputView(src, formats.get(), &in, input.put()));
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC out{};
        out.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        winrt::com_ptr<ID3D11VideoProcessorOutputView> output;
        winrt::check_hresult(video->CreateVideoProcessorOutputView(dst, formats.get(), &out, output.put()));
        D3D11_VIDEO_PROCESSOR_STREAM stream{};
        stream.Enable = TRUE; stream.pInputSurface = input.get();
        winrt::check_hresult(context->VideoProcessorBlt(processor.get(), output.get(), 0, 1, &stream));
    }
};
