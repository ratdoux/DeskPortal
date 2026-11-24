namespace DeskLink;

static class VideoChecks
{
    public static unsafe int Benchmark()
    {
        try
        {
            const int rate = StreamOptions.DefaultMbps;
            foreach (var size in new BenchmarkSize[] { new(320, 180, 30, rate), new(320, 180, 60, rate),
                new(640, 360, 60, rate), new(1280, 720, 60, rate), new(1920, 1080, 60, rate),
                new(2560, 1440, 60, rate), new(3840, 2160, 60, rate) })
            {
                Console.WriteLine($"{size.Width}x{size.Height}, {size.Fps} fps target, {size.Mbps} Mbps, 8 warm-up and 48 timed frames");
                using var surface = new BenchmarkSurface(size.Width, size.Height);
                var results = surface.Texture == 0 ? [] : EncoderBenchmarks.Measure(size with { TextureInput = true }, Console.WriteLine);
                bool gpu = results.Length > 0;
                if (!gpu) results = EncoderBenchmarks.Measure(size, Console.WriteLine);
                foreach (var r in results)
                    Console.WriteLine($"{r.Encoder.Name,-12} open {r.OpenMs,8:F2} ms  first {r.FirstMs,7:F2} ms  median {r.MedianMs,7:F2} ms  p95 {r.P95Ms,7:F2} ms");
                int mask = VideoCodec.VideoDecoderMask();
                var winner = results.First(r => r.Encoder.SupportedBy(mask)).Encoder;
                using var writer = new VideoWriter(mask);
                using var reader = new VideoReader();
                var pixels = new byte[size.Width * size.Height * 4];
                fixed (byte* data = pixels)
                {
                    if (gpu) VideoCodec.UploadBenchmarkSurface(surface.Texture, (nint)data, size.Width * 4);
                    var packet = writer.Encode(new(FrameRate: size.Fps, Mbps: size.Mbps), gpu ? surface.Texture : (nint)data,
                        size.Width, size.Height, gpu ? 0 : size.Width * 4);
                    using var image = reader.Read(packet, 0);
                    if (image.Size != new Size(size.Width, size.Height) || !writer.Description.Contains($"({winner.Name})"))
                        throw new Exception($"Expected {winner.Name}, got {writer.Description}.");
                }
                Console.WriteLine($"PASS Auto opened {writer.Description} and decoded its first frame with {(reader.Hardware ? "D3D11VA" : "software")}.");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    public static unsafe int Run()
    {
        try
        {
            const int width = 640, height = 360, fps = 60, samples = 75;
            var pixels = new byte[width * height * 4];
            int decoders = VideoCodec.VideoDecoderMask();
            using var writer = new VideoWriter(decoders);
            using var reader = new VideoReader();
            foreach (var codec in new[] { FrameCodec.Av1, FrameCodec.Hevc, FrameCodec.H264 })
            {
                double previous = 0;
                foreach (var target in new[] { 2, 14 })
                {
                    var random = new Random(420);
                    var settings = new StreamOptions(codec, fps, Mbps: target);
                    long bytes = 0;
                    for (int i = 0; i < samples; ++i)
                    {
                        random.NextBytes(pixels);
                        byte[] packet;
                        fixed (byte* raw = pixels) packet = writer.Encode(settings, (nint)raw, width, height, width * 4);
                        if (packet[2] != (i == 0 ? 1 : 0) || packet[1] != (byte)codec) throw new Exception("Unexpected video stream boundary.");
                        using var decoded = reader.Read(packet, 0);
                        if (decoded.Size != new Size(width, height)) throw new Exception("Wrong decoded frame size.");
                        if (i >= 15) bytes += packet.Length;
                    }
                    double measured = bytes * 8d * fps / (samples - 15) / 1e6;
                    if (previous > 0 && measured < previous * 2) throw new Exception("The encoder did not respond to the bitrate change.");
                    Console.WriteLine($"PASS  {writer.Description} · measured {measured:F2} Mbps on the same changing test pattern.");
                    previous = measured;
                }
            }
            using var fallback = new VideoWriter(1 << (int)FrameCodec.H264);
            fixed (byte* raw = pixels)
            {
                var packet = fallback.Encode(new(), (nint)raw, width, height, width * 4);
                if (packet[1] != (byte)FrameCodec.H264) throw new Exception("Auto ignored the peer decoder list.");
            }
            Console.WriteLine("PASS  Auto selected H.264 for a peer advertising only H.264.");
            CheckPadding(decoders);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    // An odd-sized picture is padded to the encoder's even size and cropped by the decoder.
    // Scaling it by the missing pixel instead would turn one-pixel columns grey at mid-width.
    static unsafe void CheckPadding(int decoders)
    {
        const int w = 641, h = 361;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte shade = y < 48 ? (byte)(x % 2 == 0 ? 255 : 0) : (byte)200;
                int i = (y * w + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = shade; pixels[i + 3] = 255;
            }
        var settings = new StreamOptions(FrameCodec.H264, 60, Mbps: 14);
        void Verify(string path, Func<nint, byte[]> encode)
        {
            using var reader = new VideoReader();
            Bitmap? last = null;
            try
            {
                fixed (byte* raw = pixels)
                    for (int i = 0; i < 24; i++)
                    {
                        byte[] packet = encode((nint)raw);
                        if (packet[3] != 2) throw new Exception($"{path}: the frame did not carry its picture size.");
                        if (i == 7)
                        {
                            packet = VideoCodec.WithClick(packet, 17, 12.5);
                            if (VideoCodec.ReadClick(Packet.Binary(Kind.Frame, Guid.Empty, packet).Data) != (17L, 12.5))
                                throw new Exception("Missing click receipt on a padded video packet.");
                        }
                        last?.Dispose();
                        last = reader.Read(packet, 0);
                    }
                if (last!.Size != new Size(w, h)) throw new Exception($"{path}: expected {w}x{h}, decoded {last.Size}.");
                int contrast = Math.Abs(last.GetPixel(w / 2, 20).G - last.GetPixel(w / 2 + 1, 20).G);
                int edge = last.GetPixel(w - 1, 200).G, corner = last.GetPixel(w - 1, h - 1).G;
                if (contrast < 100 || edge < 150 || corner < 130)
                    throw new Exception($"{path}: the odd-sized picture was scaled or lost its edge. Mid-width contrast {contrast}, right edge {edge}, corner {corner}.");
                Console.WriteLine($"PASS  {path}: a {w}x{h} picture kept one-pixel detail (contrast {contrast}) and its edges ({edge}, {corner}) through {(reader.Hardware ? "D3D11VA" : "software")} decoding.");
            }
            finally { last?.Dispose(); }
        }
        using (var writer = new VideoWriter(decoders))
            Verify("CPU pixels", raw => writer.Encode(settings, raw, w, h, w * 4));
        using var surface = new BenchmarkSurface(w, h);
        if (surface.Texture == 0) { Console.WriteLine("SKIP  No GPU surface for the texture padding check."); return; }
        fixed (byte* raw = pixels) VideoCodec.UploadBenchmarkSurface(surface.Texture, (nint)raw, w * 4);
        using var gpu = new VideoWriter(decoders);
        try { Verify("GPU texture", _ => gpu.Encode(settings, surface.Texture, w, h, 0)); }
        catch (IOException e) { Console.WriteLine($"SKIP  No texture encoder for the padding check. {e.Message}"); }
    }
}
