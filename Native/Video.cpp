#include <algorithm>
#include <chrono>
#include <cstdint>
#include <memory>
#include "GpuVideo.h"
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/opt.h>
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
#include <libswscale/swscale.h>
}

// Values are shared with FrameCodec and carried in the frame header.
enum { Av1 = 3, Hevc = 4, H264 = 5 };
static const AVCodec* decoderFor(int codec) {
    switch (codec) {
    case Av1: return avcodec_find_decoder_by_name("libdav1d");
    case Hevc: return avcodec_find_decoder(AV_CODEC_ID_HEVC);
    case H264: return avcodec_find_decoder(AV_CODEC_ID_H264);
    default: return nullptr;
    }
}
// Synthetic input for Auto's calibration. Upload happens outside the timed encode.
extern "C" __declspec(dllexport) HRESULT CreateBenchmarkSurface(int w, int h, ID3D11Texture2D** surface) noexcept {
    winrt::com_ptr<ID3D11Device> device;
    HRESULT hr = CreateVideoDevice(device.put(), nullptr);
    if (FAILED(hr)) return hr;
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = w; desc.Height = h; desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.BindFlags = D3D11_BIND_RENDER_TARGET;
    return device->CreateTexture2D(&desc, nullptr, surface);
}
extern "C" __declspec(dllexport) void UploadBenchmarkSurface(ID3D11Texture2D* surface, const void* pixels, int stride) {
    winrt::com_ptr<ID3D11Device> device;
    winrt::com_ptr<ID3D11DeviceContext> context;
    surface->GetDevice(device.put()); device->GetImmediateContext(context.put());
    context->UpdateSubresource(surface, 0, nullptr, pixels, stride, 0);
}
extern "C" __declspec(dllexport) int VideoDecoderMask() {
    int mask = 0;
    for (int codec : {Av1, Hevc, H264}) if (decoderFor(codec)) mask |= 1 << codec;
    return mask;
}
// A 4:2:0 encoder needs even dimensions and has a minimum size. The picture is padded to the
// coded size, never scaled, and the receiver crops it. Scaling by one pixel softens text.
struct Video {
    AVCodecContext* context = nullptr;
    AVFrame* frame = av_frame_alloc();
    AVFrame* converted = av_frame_alloc();
    AVPacket* packet = av_packet_alloc();
    SwsContext* scale = nullptr;
    int64_t pts = 0;
    GpuColorConverter gpuColor;
    winrt::com_ptr<ID3D11Texture2D> display, staging;
    winrt::com_ptr<ID3D11DeviceContext> immediate;
    winrt::com_ptr<IDXGISwapChain1> swap;
    HWND swapWindow = nullptr, failedWindow = nullptr;
    int64_t decodeUs = 0, colorUs = 0, presentUs = 0;
    D3D11_MAPPED_SUBRESOURCE mapped{};
    int displayW = 0, displayH = 0;
    // Decoder: the picture within the decoded frame. Encoder: the picture within the coded frame.
    int contentW = 0, contentH = 0;
    bool pad = false;
    // Encoder: a coded-size copy of a GPU source whose own size the encoder cannot take.
    winrt::com_ptr<ID3D11Texture2D> padded;
    winrt::com_ptr<ID3D11DeviceContext> copier;
    void unmap() {
        if (mapped.pData) { immediate->Unmap(staging.get(), 0); mapped = {}; }
    }
    bool present(HWND window) {
        if (!window || failedWindow == window) return false;
        try {
            if (!swap || window != swapWindow) {
                swap = nullptr;
                winrt::com_ptr<ID3D11Device> dev;
                display->GetDevice(dev.put());
                winrt::com_ptr<IDXGIAdapter> adapter;
                winrt::check_hresult(dev.as<IDXGIDevice>()->GetAdapter(adapter.put()));
                winrt::com_ptr<IDXGIFactory2> factory;
                winrt::check_hresult(adapter->GetParent(__uuidof(IDXGIFactory2), factory.put_void()));
                DXGI_SWAP_CHAIN_DESC1 desc{};
                desc.Width = contentW; desc.Height = contentH; desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                desc.SampleDesc.Count = 1; desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
                desc.BufferCount = 2; desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
                desc.Scaling = DXGI_SCALING_STRETCH; desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
                // The waitable chain defaults to one queued frame per HWND. A
                // device-wide limit of one makes independent windows block each other.
                desc.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
                HRESULT hr = factory->CreateSwapChainForHwnd(dev.get(), window, &desc, nullptr, nullptr, swap.put());
                if (hr == DXGI_ERROR_UNSUPPORTED || hr == E_INVALIDARG || hr == DXGI_ERROR_INVALID_CALL) {
                    desc.Flags = 0;
                    hr = factory->CreateSwapChainForHwnd(dev.get(), window, &desc, nullptr, nullptr, swap.put());
                }
                winrt::check_hresult(hr);
                factory->MakeWindowAssociation(window, DXGI_MWA_NO_ALT_ENTER);
                swapWindow = window;
            }
            winrt::com_ptr<ID3D11Texture2D> back;
            winrt::check_hresult(swap->GetBuffer(0, __uuidof(ID3D11Texture2D), back.put_void()));
            if (contentW == displayW && contentH == displayH) immediate->CopyResource(back.get(), display.get());
            else {
                D3D11_BOX picture{0, 0, 0, static_cast<UINT>(contentW), static_cast<UINT>(contentH), 1};
                immediate->CopySubresourceRegion(back.get(), 0, 0, 0, 0, display.get(), 0, &picture);
            }
            winrt::check_hresult(swap->Present(0, 0));
            return true;
        } catch (winrt::hresult_error const&) {
            swap = nullptr; failedWindow = window;
            return false;
        }
    }
    ~Video() {
        unmap();
        sws_freeContext(scale);
        av_packet_free(&packet);
        av_frame_free(&converted);
        av_frame_free(&frame);
        avcodec_free_context(&context);
    }
    int attachDevice(ID3D11Device* device) {
        auto ref = av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA);
        if (!ref) return AVERROR(ENOMEM);
        context->hw_device_ctx = ref;
        auto hw = reinterpret_cast<AVHWDeviceContext*>(ref->data);
        auto d3d = static_cast<AVD3D11VADeviceContext*>(hw->hwctx);
        device->AddRef(); d3d->device = device;
        return av_hwdevice_ctx_init(ref);
    }
    bool init(const AVCodec* codec) {
        context = avcodec_alloc_context3(codec);
        return context && frame && converted && packet;
    }
    // With keepSize the picture is converted at its own size into the top-left of a larger NV12
    // frame, and the remainder stays black. Otherwise it is scaled to fill the output.
    int convert(const uint8_t* const src[], const int strides[], int w, int h, AVPixelFormat format,
                int outW, int outH, AVPixelFormat outFormat, bool keepSize = false) {
        scale = sws_getCachedContext(scale, w, h, format, keepSize ? w : outW, keepSize ? h : outH, outFormat,
            SWS_BILINEAR, nullptr, nullptr, nullptr);
        if (!scale) return AVERROR(ENOMEM);
        if (converted->width != outW || converted->height != outH || converted->format != outFormat) {
            av_frame_unref(converted);
            converted->width = outW; converted->height = outH; converted->format = outFormat;
            int err = av_frame_get_buffer(converted, 32);
            if (err < 0) return err;
            if (keepSize && outFormat == AV_PIX_FMT_NV12) {
                memset(converted->data[0], 16, static_cast<size_t>(converted->linesize[0]) * outH);
                memset(converted->data[1], 128, static_cast<size_t>(converted->linesize[1]) * ((outH + 1) / 2));
            }
        }
        // A copy made here, while the encoder still holds the previous frame, keeps the black remainder.
        int err = av_frame_make_writable(converted);
        if (err < 0) return err;
        return sws_scale(scale, src, strides, 0, h, converted->data, converted->linesize);
    }
};
extern "C" __declspec(dllexport) void CloseVideo(Video* video) { delete video; }
static int openEncoder(const char* name, int w, int h, int fps, int mbps, bool pad, ID3D11Device* device, Video** result) {
    const AVCodec* codec = avcodec_find_encoder_by_name(name);
    if (!codec) return AVERROR_ENCODER_NOT_FOUND;
    std::unique_ptr<Video> video(new(std::nothrow) Video());
    if (!video || !video->init(codec)) return AVERROR(ENOMEM);
    auto ctx = video->context;
    // 4:2:0 encoders require even dimensions. Small popup windows can also
    // be below the hardware minimum. The view retains the original dimensions.
    ctx->width = std::max(160, (w + 1) & ~1);
    ctx->height = std::max(128, (h + 1) & ~1);
    ctx->pix_fmt = AV_PIX_FMT_NV12;
    // A peer that cannot crop still receives the picture scaled to the coded size.
    video->pad = pad && (ctx->width != w || ctx->height != h);
    video->contentW = w; video->contentH = h;
    if (device) {
        if (!strstr(name, "_nvenc") && !strstr(name, "_amf")) return AVERROR(ENOSYS);
        int rc = video->attachDevice(device);
        if (rc < 0) return rc;
        ctx->hw_frames_ctx = av_hwframe_ctx_alloc(ctx->hw_device_ctx);
        if (!ctx->hw_frames_ctx) return AVERROR(ENOMEM);
        auto frames = reinterpret_cast<AVHWFramesContext*>(ctx->hw_frames_ctx->data);
        frames->format = ctx->pix_fmt = AV_PIX_FMT_D3D11;
        frames->sw_format = AV_PIX_FMT_NV12;
        frames->width = ctx->width; frames->height = ctx->height;
        static_cast<AVD3D11VAFramesContext*>(frames->hwctx)->BindFlags = D3D11_BIND_RENDER_TARGET;
        if ((rc = av_hwframe_ctx_init(ctx->hw_frames_ctx)) < 0) return rc;
        if (video->pad) {
            // Copy the source into a black texture of the coded size, so the color
            // conversion still maps a whole even-sized surface onto another.
            D3D11_TEXTURE2D_DESC desc{};
            desc.Width = ctx->width; desc.Height = ctx->height; desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
            desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.BindFlags = D3D11_BIND_RENDER_TARGET;
            winrt::check_hresult(device->CreateTexture2D(&desc, nullptr, video->padded.put()));
            device->GetImmediateContext(video->copier.put());
            winrt::com_ptr<ID3D11RenderTargetView> target;
            winrt::check_hresult(device->CreateRenderTargetView(video->padded.get(), nullptr, target.put()));
            const float black[4] = {0, 0, 0, 1};
            video->copier->ClearRenderTargetView(target.get(), black);
            video->gpuColor.init(device, ctx->width, ctx->height, ctx->width, ctx->height, true);
        } else video->gpuColor.init(device, w, h, ctx->width, ctx->height, true);
    }
    ctx->time_base = {1, fps}; ctx->framerate = {fps, 1};
    ctx->bit_rate = mbps * 1000000LL;
    ctx->rc_max_rate = ctx->bit_rate;
    ctx->rc_buffer_size = static_cast<int>(ctx->bit_rate / fps * 2);
    // The transport is ordered and lossless, and every restart opens a new sequence, so a
    // periodic keyframe buys nothing. Each one is a burst the two-frame rate buffer must
    // absorb. Stay inside the range every hardware encoder accepts.
    ctx->gop_size = strstr(name, "_amf") ? 1000 : fps * 600;
    ctx->max_b_frames = 0;
    ctx->flags |= AV_CODEC_FLAG_LOW_DELAY;
    AVDictionary* options = nullptr;
    if (strstr(name, "_nvenc")) {
        av_dict_set(&options, "preset", "p3", 0);
        av_dict_set(&options, "tune", "ull", 0);
        av_dict_set(&options, "rc", "cbr", 0);
        av_dict_set(&options, "zerolatency", "1", 0);
        av_dict_set(&options, "delay", "0", 0);
    } else if (strstr(name, "_qsv")) {
        av_dict_set(&options, "async_depth", "1", 0);
    } else if (strstr(name, "_amf")) {
        av_dict_set(&options, "usage", "ultralowlatency", 0);
    }
    int err = avcodec_open2(ctx, codec, &options);
    av_dict_free(&options);
    if (err < 0) return err;
    *result = video.release();
    return 0;
}
extern "C" __declspec(dllexport) int OpenVideoEncoder(const char* name, int w, int h, int fps, int mbps, int pad, Video** result) {
    return openEncoder(name, w, h, fps, mbps, pad != 0, nullptr, result);
}
extern "C" __declspec(dllexport) int OpenDeviceEncoder(const char* name, int w, int h, int fps, int mbps, int pad, ID3D11Device* device, Video** result) noexcept {
    try { return openEncoder(name, w, h, fps, mbps, pad != 0, device, result); }
    catch (...) { return winrt::to_hresult(); }
}
extern "C" __declspec(dllexport) int OpenTextureEncoder(const char* name, int w, int h, int fps, int mbps, int pad, ID3D11Texture2D* source, Video** result) noexcept {
    winrt::com_ptr<ID3D11Device> device;
    if (source) source->GetDevice(device.put());
    return OpenDeviceEncoder(name, w, h, fps, mbps, pad, device.get(), result);
}
static int submit(Video* v, AVFrame* frame, uint8_t** data, int* length) {
    frame->pts = v->pts++;
    int err = avcodec_send_frame(v->context, frame);
    if (err < 0) return err;
    av_packet_unref(v->packet);
    if ((err = avcodec_receive_packet(v->context, v->packet)) < 0) return err;
    *data = v->packet->data; *length = v->packet->size;
    return 0;
}
extern "C" __declspec(dllexport) int EncodeTexture(Video* v, ID3D11Texture2D* source, uint8_t** data, int* length) noexcept {
    try {
        av_frame_unref(v->frame);
        int err = av_hwframe_get_buffer(v->context->hw_frames_ctx, v->frame, 0);
        if (err < 0) return err;
        if (v->padded) {
            D3D11_BOX picture{0, 0, 0, static_cast<UINT>(v->contentW), static_cast<UINT>(v->contentH), 1};
            v->copier->CopySubresourceRegion(v->padded.get(), 0, 0, 0, 0, source, 0, &picture);
            source = v->padded.get();
        }
        v->gpuColor.convert(source, 0, reinterpret_cast<ID3D11Texture2D*>(v->frame->data[0]));
        return submit(v, v->frame, data, length);
    } catch (...) { return winrt::to_hresult(); }
}
extern "C" __declspec(dllexport) int EncodeVideo(Video* v, const uint8_t* pixels, int w, int h, int stride,
                                                uint8_t** data, int* length) {
    const uint8_t* sources[4] = {pixels};
    int strides[4] = {stride};
    int err = v->convert(sources, strides, w, h, AV_PIX_FMT_BGRA, v->context->width, v->context->height, v->context->pix_fmt, v->pad);
    if (err < 0) return err;
    return submit(v, v->converted, data, length);
}
extern "C" __declspec(dllexport) int OpenVideoDecoder(int id, int hardware, Video** result) {
    const AVCodec* codec = hardware && id == Av1 ? avcodec_find_decoder_by_name("av1") : decoderFor(id);
    if (!codec) return AVERROR_DECODER_NOT_FOUND;
    std::unique_ptr<Video> video(new(std::nothrow) Video());
    if (!video || !video->init(codec)) return AVERROR(ENOMEM);
    auto ctx = video->context;
    if (hardware) {
        winrt::com_ptr<ID3D11Device> device;
        if (!BorrowVideoDevice(device.put(), video->immediate.put())) {
            HRESULT hr = CreateVideoDevice(device.put(), video->immediate.put());
            if (FAILED(hr)) return hr;
        }
        int rc = video->attachDevice(device.get());
        if (rc < 0) return rc;
        ctx->get_format = [](AVCodecContext*, const AVPixelFormat* formats) {
            for (; *formats != AV_PIX_FMT_NONE; ++formats) if (*formats == AV_PIX_FMT_D3D11) return *formats;
            return AV_PIX_FMT_NONE;
        };
    }
    ctx->max_pixels = 33554432;
    ctx->thread_count = 4;
    ctx->thread_type = FF_THREAD_SLICE;
    ctx->flags |= AV_CODEC_FLAG_LOW_DELAY;
    if (id == Av1 && !hardware) av_opt_set_int(ctx->priv_data, "max_frame_delay", 1, 0);
    int err = avcodec_open2(ctx, codec, nullptr);
    if (err < 0) return err;
    *result = video.release();
    return 0;
}
extern "C" __declspec(dllexport) int ReadDecodedPixels(Video* v, uint8_t** pixels, int* stride) noexcept {
    try {
        if (!v->display) return AVERROR(EINVAL);
        if (!v->mapped.pData) {
            if (!v->staging) {
                D3D11_TEXTURE2D_DESC desc{};
                v->display->GetDesc(&desc);
                desc.BindFlags = 0; desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                winrt::com_ptr<ID3D11Device> device;
                v->display->GetDevice(device.put());
                winrt::check_hresult(device->CreateTexture2D(&desc, nullptr, v->staging.put()));
            }
            v->immediate->CopyResource(v->staging.get(), v->display.get());
            winrt::check_hresult(v->immediate->Map(v->staging.get(), 0, D3D11_MAP_READ, 0, &v->mapped));
        }
        *pixels = static_cast<uint8_t*>(v->mapped.pData); *stride = v->mapped.RowPitch;
        return 0;
    } catch (...) { return winrt::to_hresult(); }
}
extern "C" __declspec(dllexport) void LastDecodeTimes(Video* v, int64_t* decode, int64_t* color, int64_t* present) noexcept {
    *decode = v->decodeUs; *color = v->colorUs; *present = v->presentUs;
}
// contentW and contentH give the picture inside a padded frame, or zero when the whole frame is
// the picture. Returns 0 with pixels, 0 without pixels once presented in the window, or 1 when a
// GPU frame was decoded with no window to show it. ReadDecodedPixels reads that frame on request.
extern "C" __declspec(dllexport) int DecodeVideoFrame(Video* v, const uint8_t* data, int length, HWND window,
                                                     int contentW, int contentH, uint8_t** pixels, int* w, int* h, int* stride) {
    auto mark = std::chrono::steady_clock::now();
    auto lap = [&mark]() {
        auto now = std::chrono::steady_clock::now();
        auto us = std::chrono::duration_cast<std::chrono::microseconds>(now - mark).count();
        mark = now;
        return us;
    };
    v->decodeUs = v->colorUs = v->presentUs = 0;
    av_packet_unref(v->packet);
    int err = av_new_packet(v->packet, length);
    if (err < 0) return err;
    memcpy(v->packet->data, data, length);
    if ((err = avcodec_send_packet(v->context, v->packet)) < 0) return err;
    if ((err = avcodec_receive_frame(v->context, v->frame)) < 0) return err;
    v->decodeUs = lap();
    auto f = v->frame;
    if (f->width <= 0 || f->height <= 0 || (int64_t)f->width * f->height > v->context->max_pixels) return AVERROR_INVALIDDATA;
    if (contentW <= 0 || contentH <= 0) { contentW = f->width; contentH = f->height; }
    if (contentW > f->width || contentH > f->height) return AVERROR_INVALIDDATA;
    if (v->immediate) {
        if (f->format != AV_PIX_FMT_D3D11) return AVERROR_INVALIDDATA;
        try {
            auto texture = reinterpret_cast<ID3D11Texture2D*>(f->data[0]);
            v->unmap();
            if (f->width != v->displayW || f->height != v->displayH) {
                winrt::com_ptr<ID3D11Device> device;
                texture->GetDevice(device.put());
                v->gpuColor = {};
                v->gpuColor.init(device.get(), f->width, f->height, f->width, f->height, false);
                D3D11_TEXTURE2D_DESC desc{};
                desc.Width = f->width; desc.Height = f->height; desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
                desc.BindFlags = D3D11_BIND_RENDER_TARGET;
                v->display = nullptr; v->staging = nullptr;
                winrt::check_hresult(device->CreateTexture2D(&desc, nullptr, v->display.put()));
                v->swap = nullptr;
                v->displayW = f->width; v->displayH = f->height;
            }
            v->gpuColor.convert(texture, static_cast<UINT>(reinterpret_cast<uintptr_t>(f->data[1])), v->display.get());
            v->colorUs = lap();
            if (contentW != v->contentW || contentH != v->contentH) {
                v->swap = nullptr;
                v->contentW = contentW; v->contentH = contentH;
            }
            *w = contentW; *h = contentH;
            *pixels = nullptr; *stride = 0;
            // Reading a hidden or minimized window's frame back to memory would cost more than decoding it.
            if (!window) return 1;
            bool shown = v->present(window);
            v->presentUs = lap();
            if (shown) return 0;
            return ReadDecodedPixels(v, pixels, stride);
        } catch (...) { return winrt::to_hresult(); }
    }
    err = v->convert(f->data, f->linesize, f->width, f->height, (AVPixelFormat)f->format, f->width, f->height, AV_PIX_FMT_BGRA);
    v->colorUs = lap();
    if (err < 0) return err;
    // The caller copies this many pixels from the top-left of each row.
    *w = contentW; *h = contentH; *pixels = v->converted->data[0]; *stride = v->converted->linesize[0];
    return 0;
}
