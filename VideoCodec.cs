using System.Diagnostics;
using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskLink;

static class VideoCodec
{
    public const byte Marker = 0xD7;
    [DllImport("DeskCapture.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int OpenDeviceEncoder(string name, int w, int h, int fps, int mbps, int pad, nint device, out nint encoder);
    // Marker, codec, restart flag and version. Version 2 adds the picture's width and height
    // as two little-endian 16-bit numbers, for a picture padded to the coded size.
    public const int Header = 4, PaddedHeader = 8;
    // Advertised beside the decoder bits: this build crops padded frames. A sender scales
    // the picture to the coded size for a peer without it, as earlier builds always did.
    public const int PaddedFrames = 0x100;
    public const int ClickFeedback = 0x200;
    // Versions 3/4 carry the same bitstream as 1/2, followed by an echoed click
    // ID and the source's receive-to-capture time. Neither PC compares clocks.
    const int FeedbackBytes = sizeof(long) + sizeof(uint);
    public static byte[] WithClick(byte[] frame, long click, double sourceMs)
    {
        int end = frame.Length;
        Array.Resize(ref frame, checked(end + FeedbackBytes));
        frame[3] += 2;
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(end), click);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(end + 8), (uint)Math.Clamp(sourceMs * 1000, 0, uint.MaxValue));
        return frame;
    }
    public static (long Click, double SourceMs) ReadClick(byte[] packet)
    {
        if (packet.Length < 16 + Header + FeedbackBytes + 1 || packet[16] != Marker || packet[19] is not (3 or 4)) return default;
        return (BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(packet.Length - FeedbackBytes)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(packet.Length - 4)) / 1000d);
    }
    public static int BitstreamEnd(byte[] packet, int version) => version is 3 or 4 ? packet.Length - FeedbackBytes : packet.Length;
    // The native 4:2:0 encoder rounds to even dimensions and its minimum frame size.
    public static Size EncodedSize(Size source) => new(Math.Max(160, (source.Width + 1) & ~1), Math.Max(128, (source.Height + 1) & ~1));
    [DllImport("DeskCapture.dll", EntryPoint = "VideoDecoderMask")] static extern int NativeDecoderMask();
    internal static int VideoDecoderMask() => NativeDecoderMask() | PaddedFrames | ClickFeedback;
    [DllImport("DeskCapture.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern int OpenVideoEncoder(string name, int w, int h, int fps, int mbps, int pad, out nint encoder);
    [DllImport("DeskCapture.dll")] internal static extern int EncodeVideo(nint encoder, nint pixels, int w, int h, int stride, out nint data, out int length);
    [DllImport("DeskCapture.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern int OpenTextureEncoder(string name, int w, int h, int fps, int mbps, int pad, nint texture, out nint encoder);
    [DllImport("DeskCapture.dll")] static extern int EncodeTexture(nint encoder, nint texture, out nint data, out int length);
    [DllImport("DeskCapture.dll")] internal static extern int CreateBenchmarkSurface(int w, int h, out nint texture);
    [DllImport("DeskCapture.dll")] internal static extern void UploadBenchmarkSurface(nint texture, nint pixels, int stride);
    public static int OpenEncoder(string name, int w, int h, int fps, int mbps, bool pad, nint texture, out nint encoder) =>
        texture != 0 ? OpenTextureEncoder(name, w, h, fps, mbps, pad ? 1 : 0, texture, out encoder)
            : OpenVideoEncoder(name, w, h, fps, mbps, pad ? 1 : 0, out encoder);
    public static int EncodeFrame(nint encoder, nint source, int w, int h, int stride, out nint data, out int length) =>
        stride == 0 ? EncodeTexture(encoder, source, out data, out length) : EncodeVideo(encoder, source, w, h, stride, out data, out length);
    [DllImport("DeskCapture.dll")] internal static extern int OpenVideoDecoder(FrameCodec codec, int hardware, out nint decoder);
    [DllImport("DeskCapture.dll", EntryPoint = "DecodeVideoFrame")] internal static extern unsafe int DecodeVideo(nint decoder, byte* packet, int length, nint window, int contentW, int contentH, out nint pixels, out int w, out int h, out int stride);
    [DllImport("DeskCapture.dll")] internal static extern void LastDecodeTimes(nint decoder, out long decodeUs, out long colorUs, out long presentUs);
    [DllImport("DeskCapture.dll")] internal static extern int ReadDecodedPixels(nint decoder, out nint pixels, out int stride);
    [DllImport("DeskCapture.dll")] internal static extern void CloseVideo(nint handle);
    [DllImport("avutil-60.dll")] static extern int av_strerror(int code, StringBuilder message, nuint size);
    [DllImport("avcodec-62.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern uint avcodec_version();
    public static string Error(int code)
    {
        var message = new StringBuilder(128);
        return av_strerror(code, message, 128) == 0 ? message.ToString() : code.ToString();
    }
    public static string Name(FrameCodec codec) => codec switch
    { FrameCodec.Av1 => "AV1", FrameCodec.Hevc => "HEVC", FrameCodec.H264 => "H.264", _ => codec.ToString() };
}

sealed class BenchmarkSurface : IDisposable
{
    public nint Texture { get; private set; }
    public BenchmarkSurface(int width, int height)
    {
        if (VideoCodec.CreateBenchmarkSurface(width, height, out var texture) >= 0) Texture = texture;
    }
    public void Dispose()
    {
        if (Texture != 0) Marshal.Release(Texture);
        Texture = 0;
    }
}

// Each shared HWND owns one encoder. A new stream starts with an independently
// decodable frame, including after a resize, codec change, or bitrate change.
sealed class VideoWriter(int peerDecoders) : IDisposable
{
    nint encoder;
    StreamOptions? current;
    Size dimensions;
    EncoderChoice actual;
    Task<EncoderTiming[]>? pending;
    sealed record Opening(StreamOptions Settings, Size Size, EncoderChoice Choice, nint Handle);
    Task<Opening?>? opening;
    public string Description { get; private set; } = "";
    public long SetupTicks { get; private set; }

    IEnumerable<EncoderChoice> Choices(StreamOptions settings, bool gpu, IEnumerable<EncoderChoice> ranked)
    {
        var available = EncoderChoice.Candidates.Where(c => c.SupportedBy(peerDecoders) && (!gpu || c.TextureInput));
        return settings.Codec == FrameCodec.Auto
            ? ranked.Where(c => c.SupportedBy(peerDecoders) && (!gpu || c.TextureInput))
                .Concat(available.OrderBy(c => c.Codec == FrameCodec.H264 ? 0 : c.Codec == FrameCodec.Hevc ? 1 : 2)).Distinct()
            : available.Where(c => c.Codec == settings.Codec);
    }
    // The device is available before the first WGC texture. The first input picture is
    // submitted at handoff, so it is current and starts an independent video sequence.
    public void PrepareDevice(StreamOptions settings, nint device, int w, int h, CancellationToken stop, Task? move = null)
    {
        if (opening != null || settings.Codec is FrameCodec.Jpeg or FrameCodec.Png) return;
        Marshal.AddRef(device);
        opening = Task.Run(async () =>
        {
            try
            {
                if (move != null) await move.WaitAsync(stop).ConfigureAwait(false);
                var known = settings.Codec == FrameCodec.Auto
                    ? EncoderBenchmarks.Known(BenchmarkSize.For(w, h, settings) with { TextureInput = true }) : null;
                foreach (var choice in Choices(settings, true, known?.Select(t => t.Encoder) ?? []))
                {
                    if (stop.IsCancellationRequested) break;
                    if (VideoCodec.OpenDeviceEncoder(choice.Name, w, h, settings.FrameRate, settings.Mbps, pad ? 1 : 0, device, out var handle) >= 0)
                        return new Opening(settings, new Size(w, h), choice, handle);
                }
                return null;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return null; }
            finally { Marshal.Release(device); }
        });
    }

    public byte[] Encode(StreamOptions settings, nint pixels, int w, int h, int stride)
    {
        long start = Stopwatch.GetTimestamp();
        SetupTicks = 0;
        bool restart = encoder == 0 || current != settings || dimensions != new Size(w, h);
        bool gpu = stride == 0;
        if (restart)
            pending = settings.Codec == FrameCodec.Auto ? EncoderBenchmarks.For(BenchmarkSize.For(w, h, settings), gpu) : null;
        EncoderChoice[] ranked = [];
        if (pending?.IsCompleted == true)
        {
            if (pending.IsCompletedSuccessfully)
                ranked = pending.Result.Select(t => t.Encoder).Where(c => c.SupportedBy(peerDecoders)).ToArray();
            pending = null;
            if (ranked.Length > 0 && ranked[0] != actual) restart = true;
        }
        nint packet = 0;
        int length = 0;
        if (restart)
        {
            if (encoder != 0) VideoCodec.CloseVideo(encoder);
            encoder = 0;
            var choices = Choices(settings, gpu, ranked);
            var warm = TakeOpening();
            if (StallLog.Timing && warm != null)
                Console.WriteLine($"Timing {Environment.MachineName} prepared {warm.Choice.Name} {warm.Size.Width}x{warm.Size.Height}, actual {w}x{h}, preferred {ranked.FirstOrDefault().Name ?? "default"}");
            if (warm != null && (warm.Settings != settings || warm.Size != new Size(w, h) ||
                (ranked.Length > 0 && warm.Choice != ranked[0])))
            {
                VideoCodec.CloseVideo(warm.Handle);
                warm = null;
            }
            string reason = "The other computer did not advertise a decoder for this selection.";
            foreach (var choice in choices)
            {
                nint candidate;
                int rc = 0;
                if (warm?.Choice == choice)
                {
                    candidate = warm.Handle;
                    warm = null;
                }
                else rc = VideoCodec.OpenEncoder(choice.Name, w, h, settings.FrameRate, settings.Mbps, pad, gpu ? pixels : 0, out candidate);
                if (rc >= 0)
                {
                    long setup = Stopwatch.GetTimestamp() - start;
                    rc = VideoCodec.EncodeFrame(candidate, pixels, w, h, stride, out packet, out length);
                    if (rc >= 0)
                    {
                        SetupTicks = setup;
                        encoder = candidate; actual = choice;
                        Description = $"{VideoCodec.Name(choice.Codec)} ({choice.Name}) · {(gpu ? "GPU texture" : "CPU pixels")} · {settings.Mbps} Mbps target · {settings.FrameRate} fps cap";
                        break;
                    }
                    VideoCodec.CloseVideo(candidate);
                }
                reason = $"{choice.Name}: {VideoCodec.Error(rc)}.";
            }
            if (warm != null) VideoCodec.CloseVideo(warm.Handle);
            if (encoder == 0) throw new IOException($"Could not start {VideoCodec.Name(settings.Codec)} for this window. {reason} Choose another codec in Settings.");
            current = settings; dimensions = new(w, h);
        }
        else
        {
            int rc = VideoCodec.EncodeFrame(encoder, pixels, w, h, stride, out packet, out length);
            if (rc < 0) throw new IOException($"Video encoding failed. {VideoCodec.Error(rc)}");
        }
        return CopyPacket(packet, length, actual.Codec, restart, pad ? new Size(w, h) : Size.Empty);
    }
    // The peer crops a padded frame to the picture size carried in its header.
    bool pad => (peerDecoders & VideoCodec.PaddedFrames) != 0;
    internal static byte[] CopyPacket(nint packet, int length, FrameCodec codec, bool restart, Size picture = default)
    {
        if (length is < 1 or > 12 * 1024 * 1024) throw new IOException("Encoded frame exceeds the stream size limit.");
        int header = picture.IsEmpty ? VideoCodec.Header : VideoCodec.PaddedHeader;
        byte[] output = new byte[header + length];
        output[0] = VideoCodec.Marker; output[1] = (byte)codec; output[2] = (byte)(restart ? 1 : 0); output[3] = (byte)(picture.IsEmpty ? 1 : 2);
        if (!picture.IsEmpty)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), checked((ushort)picture.Width));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(6), checked((ushort)picture.Height));
        }
        Marshal.Copy(packet, output, header, length);
        return output;
    }
    Opening? TakeOpening()
    {
        var task = opening;
        opening = null;
        try { return task?.GetAwaiter().GetResult(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ExternalException or InvalidOperationException)
        {
            // The normal setup path will try the eligible encoders and report a real failure.
            return null;
        }
    }
    public void Dispose()
    {
        if (TakeOpening() is { } warm) VideoCodec.CloseVideo(warm.Handle);
        if (encoder != 0) VideoCodec.CloseVideo(encoder);
        encoder = 0; current = null; pending = null; SetupTicks = 0;
    }
}

sealed class VideoReader : IDisposable
{
    nint decoder;
    FrameCodec codec;
    public bool Hardware { get; private set; }
    public string? DescribeLastDecode()
    {
        if (decoder == 0) return null;
        VideoCodec.LastDecodeTimes(decoder, out long decode, out long color, out long present);
        return $"{(Hardware ? "D3D11VA" : "software")}, decoder call {decode / 1000d:F1} ms, color conversion {color / 1000d:F1} ms, presentation call {present / 1000d:F1} ms";
    }
    public Size Size { get; private set; }
    public Bitmap Read(byte[] packet, int offset) => Decode(packet, offset, 0, out _) ?? Snapshot();
    // Returns the pixels, or null when the frame stayed on the GPU. It was then either presented
    // in the window, or held because there was no window. Snapshot reads a held frame.
    public unsafe Bitmap? Decode(byte[] packet, int offset, nint window, out bool held)
    {
        held = false;
        if (packet.Length <= offset) throw new InvalidDataException("Empty window frame.");
        if (packet[offset] != VideoCodec.Marker)
        {
            Dispose();
            using var stream = new MemoryStream(packet, offset, packet.Length - offset, false);
            using var still = System.Drawing.Image.FromStream(stream);
            var bitmap = new Bitmap(still);
            Size = bitmap.Size;
            return bitmap;
        }
        if (packet.Length <= offset + VideoCodec.Header || packet[offset + 3] is < 1 or > 4) throw new InvalidDataException("Invalid video frame.");
        int header = packet[offset + 3] is 2 or 4 ? VideoCodec.PaddedHeader : VideoCodec.Header;
        int end = VideoCodec.BitstreamEnd(packet, packet[offset + 3]);
        if (end <= offset + header) throw new InvalidDataException("Invalid video frame.");
        int pictureW = 0, pictureH = 0;
        if (header == VideoCodec.PaddedHeader)
        {
            pictureW = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(offset + 4));
            pictureH = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(offset + 6));
            if (pictureW == 0 || pictureH == 0) throw new InvalidDataException("Invalid video frame.");
        }
        var incoming = (FrameCodec)packet[offset + 1];
        if (packet[offset + 2] == 1)
        {
            long openAt = Stopwatch.GetTimestamp();
            Dispose();
            int rc = VideoCodec.OpenVideoDecoder(incoming, 1, out decoder);
            Hardware = rc >= 0;
            if (!Hardware) rc = VideoCodec.OpenVideoDecoder(incoming, 0, out decoder);
            if (rc < 0) throw new IOException($"Could not open the video decoder. {VideoCodec.Error(rc)}");
            codec = incoming;
            StallLog.Stage("open hardware/software decoder", openAt);
        }
        if (decoder == 0 || codec != incoming) throw new InvalidDataException("Missing start of window video stream.");
        int start = offset + header;
        fixed (byte* data = &packet[start])
        {
            long submit = Stopwatch.GetTimestamp();
            int rc = VideoCodec.DecodeVideo(decoder, data, end - start, window, pictureW, pictureH, out var pixels, out int w, out int h, out int stride);
            if (packet[offset + 2] == 1) StallLog.Stage("first decode and GPU presentation", submit);
            // Drivers may accept the decoder at open and reject the stream profile on the first packet.
            if (rc < 0 && Hardware && packet[offset + 2] == 1)
            {
                Dispose();
                rc = VideoCodec.OpenVideoDecoder(incoming, 0, out decoder);
                if (rc >= 0) rc = VideoCodec.DecodeVideo(decoder, data, end - start, window, pictureW, pictureH, out pixels, out w, out h, out stride);
            }
            if (rc < 0) throw new IOException($"Could not decode {VideoCodec.Name(codec)}. {VideoCodec.Error(rc)}");
            Size = new(w, h);
            held = rc == 1;
            return pixels == 0 ? null : Copy(pixels, w, h, stride);
        }
    }
    public Bitmap Snapshot()
    {
        int rc = VideoCodec.ReadDecodedPixels(decoder, out var pixels, out var stride);
        if (rc < 0) throw new IOException(VideoCodec.Error(rc));
        return Copy(pixels, Size.Width, Size.Height, stride);
    }
    static unsafe Bitmap Copy(nint pixels, int w, int h, int stride)
    {
        var image = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        try
        {
            var bits = image.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy((byte*)pixels + y * stride, (byte*)bits.Scan0 + y * bits.Stride, w * 4, w * 4);
            }
            finally { image.UnlockBits(bits); }
            return image;
        }
        catch { image.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (decoder != 0) VideoCodec.CloseVideo(decoder);
        decoder = 0; Hardware = false;
    }
}

// The window's ordered dispatch worker awaits each decode. A view may appear after the
// first packet or close while its last frame is still decoding.
sealed class ReceivedVideo : IDisposable
{
    readonly VideoReader reader = new();
    public FrameMeter Meter { get; } = new();
    Bitmap? bitmap;
    public bool Presented { get; private set; }
    // The latest frame was decoded on the GPU while its window was hidden or minimized.
    public bool Held { get; private set; }
    public Size FrameSize => reader.Size;
    // A frame left on the GPU is read back only when something asks to see it.
    public Bitmap? Image => bitmap ?? ((Presented || Held) && !busy && !disposed ? bitmap = reader.Snapshot() : null);
    public long Updated { get; private set; } = Environment.TickCount64;
    bool busy, disposed;
    public async Task Decode(byte[] data, nint window = 0, bool prepareFirstPicture = false)
    {
        Updated = Environment.TickCount64;
        busy = true;
        try
        {
            long queued = Stopwatch.GetTimestamp();
            var (image, held) = await Task.Run(() =>
            {
                long start = Stopwatch.GetTimestamp();
                Meter.ObserveStage("Decode scheduling", start - queued);
                var decoded = reader.Decode(data, 16, window, out bool kept);
                // The first paint must be available before the form is shown.
                // Copy a held GPU frame on the decoder worker, away from input dispatch.
                if (kept && prepareFirstPicture) decoded = reader.Snapshot();
                long decodeTime = Stopwatch.GetTimestamp() - start;
                Meter.ObserveStage("Decode / submit", decodeTime,
                    decodeTime >= Stopwatch.Frequency / 10 ? reader.DescribeLastDecode() : null);
                string path = decoded != null ? (reader.Hardware ? "D3D11VA" : "software") : kept ? "D3D11VA, not shown" : "D3D11VA, GPU display";
                string codec = data[16] == VideoCodec.Marker ? $"{VideoCodec.Name((FrameCodec)data[17])} ({path})" :
                    data[16] == 0x89 ? "PNG" : "JPEG";
                Meter.Record(data.Length - 16, codec, reader.Size, decodeTime);
                return (decoded, kept);
            });
            if (disposed) image?.Dispose();
            else { bitmap?.Dispose(); bitmap = image; Presented = image == null && !held; Held = held; }
        }
        finally { busy = false; if (disposed) reader.Dispose(); }
    }
    public void Dispose()
    {
        disposed = true; bitmap?.Dispose(); bitmap = null;
        if (!busy) reader.Dispose();
    }
}
