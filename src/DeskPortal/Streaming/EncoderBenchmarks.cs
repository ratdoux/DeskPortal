using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DeskLink;

readonly record struct EncoderChoice(FrameCodec Codec, string Name)
{
    public static readonly EncoderChoice[] Candidates =
        (from codec in new[] { FrameCodec.Av1, FrameCodec.Hevc, FrameCodec.H264 }
         let prefix = codec.ToString().ToLowerInvariant()
         from backend in codec == FrameCodec.H264 ? new[] { "nvenc", "qsv", "amf", "mf" } : new[] { "nvenc", "qsv", "amf" }
         select new EncoderChoice(codec, prefix + "_" + backend)).ToArray();

    public bool SupportedBy(int mask) => (mask & (1 << (int)Codec)) != 0;
    public bool TextureInput => Name.EndsWith("_nvenc") || Name.EndsWith("_amf");
}

sealed record EncoderTiming(EncoderChoice Encoder, double OpenMs, double FirstMs, double MedianMs, double P95Ms);
readonly record struct BenchmarkSize(int Width, int Height, int Fps, int Mbps, bool TextureInput = false)
{
    public static BenchmarkSize For(int width, int height, StreamOptions options)
    {
        long pixels = (long)width * height;
        var (w, h) = pixels switch
        {
            <= 320 * 180 => (320, 180), <= 640 * 360 => (640, 360), <= 1280 * 720 => (1280, 720),
            <= 1920 * 1080 => (1920, 1080), <= 2560 * 1440 => (2560, 1440), _ => (3840, 2160)
        };
        return new(w, h, options.FrameRate, options.Mbps);
    }
}

static class EncoderBenchmarks
{
    const int Warmup = 8, Samples = 48;
    static readonly ConcurrentDictionary<BenchmarkSize, Lazy<Task<EncoderTiming[]>>> profiles = new();
    static readonly SemaphoreSlim benchmarkSlot = new(1);
    static readonly Lazy<string> signature = new(HardwareSignature);
    static readonly string folder = Path.Combine(AppStorage.Root, "encoder-timings");
    sealed record SavedProfile(string Signature, EncoderTiming[] Timings);

    static string HardwareSignature()
    {
        // Unrelated UI rebuilds must not trigger GPU calibration inside a live
        // session. Bump this revision when encoder settings or the workload change.
        var hardware = new StringBuilder("window-encoder-v1|").Append(Environment.MachineName)
            .Append('|').Append(VideoCodec.avcodec_version());
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (adapters != null)
                foreach (string name in adapters.GetSubKeyNames().Where(n => int.TryParse(n, out _)).Order())
                {
                    using var adapter = adapters.OpenSubKey(name);
                    hardware.Append('|').Append(adapter?.GetValue("MatchingDeviceId")).Append(':').Append(adapter?.GetValue("DriverVersion"));
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hardware.ToString())));
    }
    static string PathFor(BenchmarkSize size) => Path.Combine(folder, $"{size.Width}x{size.Height}-{size.Fps}-{size.Mbps}{(size.TextureInput ? "-gpu" : "")}.json");
    static EncoderTiming[]? Load(BenchmarkSize size)
    {
        try
        {
            string path = PathFor(size);
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(7)) return null;
            var saved = JsonSerializer.Deserialize<SavedProfile>(File.ReadAllText(path));
            if (saved?.Signature == signature.Value && saved.Timings is { Length: > 0 } scores &&
                scores.All(s => s != null && EncoderChoice.Candidates.Contains(s.Encoder) && s.P95Ms > 0 && double.IsFinite(s.P95Ms)))
                return scores.OrderBy(s => s.P95Ms).ThenBy(s => s.MedianMs).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return null;
    }
    static void Save(BenchmarkSize size, EncoderTiming[] scores)
    {
        if (scores.Length == 0) return;
        string path = PathFor(size), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(new SavedProfile(signature.Value, scores)));
                File.Move(temp, path, true);
            }
            finally { File.Delete(temp); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public static Task<EncoderTiming[]> For(BenchmarkSize size, bool gpu = false) =>
        profiles.GetOrAdd(size with { TextureInput = gpu }, key => new(() => Load(key) is { } cached ? Task.FromResult(cached) : Calibrate(key))).Value;

    // A drag that may stay local can reuse calibration, without starting a benchmark.
    public static EncoderTiming[]? Known(BenchmarkSize key) =>
        profiles.TryGetValue(key, out var profile) && profile.IsValueCreated && profile.Value.IsCompletedSuccessfully
            ? profile.Value.Result : Load(key);

    static async Task<EncoderTiming[]> Calibrate(BenchmarkSize size)
    {
        await benchmarkSlot.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => Measure(size)).ConfigureAwait(false); }
        finally { benchmarkSlot.Release(); }
    }

    // All candidates scroll the same textured BGRA surface. The production encoder does
    // conversion, submission, packet retrieval and the managed copy inside each timed call.
    public static unsafe EncoderTiming[] Measure(BenchmarkSize size, Action<string>? report = null)
    {
        int stride = size.Width * 4;
        var pixels = new byte[checked(stride * (size.Height + Warmup + Samples))];
        for (int y = 0; y < pixels.Length / stride; y++)
            for (int x = 0; x < size.Width; x++)
            {
                int offset = y * stride + x * 4;
                int shade = (y % 23 < 3 && x % 180 < 112) ? 48 : 220;
                if (x > size.Width / 2) shade = (x * 3 + y * 7 + ((x * y) >> 5)) & 255;
                pixels[offset] = (byte)shade;
                pixels[offset + 1] = (byte)((shade + x / 13) & 255);
                pixels[offset + 2] = (byte)((shade + y / 7) & 255);
                pixels[offset + 3] = 255;
            }
        var results = new List<EncoderTiming>();
        using var surface = size.TextureInput ? new BenchmarkSurface(size.Width, size.Height) : null;
        nint texture = surface?.Texture ?? 0;
        if (size.TextureInput && texture == 0) return [];
        fixed (byte* start = pixels)
        {
            foreach (var candidate in EncoderChoice.Candidates)
            {
                if (size.TextureInput && !candidate.TextureInput) continue;
                long opening = Stopwatch.GetTimestamp();
                int rc = VideoCodec.OpenEncoder(candidate.Name, size.Width, size.Height, size.Fps, size.Mbps, true, texture, out var encoder);
                double openMs = Stopwatch.GetElapsedTime(opening).TotalMilliseconds;
                if (rc < 0) { report?.Invoke($"{candidate.Name} unavailable ({VideoCodec.Error(rc)})"); continue; }
                try
                {
                    var times = new double[Samples];
                    double first = 0;
                    for (int i = 0; i < Warmup + Samples; i++)
                    {
                        nint source = (nint)(start + i * stride);
                        if (texture != 0) { VideoCodec.UploadBenchmarkSurface(texture, source, stride); source = texture; }
                        long begin = Stopwatch.GetTimestamp();
                        rc = VideoCodec.EncodeFrame(encoder, source, size.Width, size.Height, texture == 0 ? stride : 0, out var packet, out var length);
                        if (rc < 0) break;
                        VideoWriter.CopyPacket(packet, length, candidate.Codec, i == 0);
                        double elapsed = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                        if (i == 0) first = elapsed;
                        if (i >= Warmup) times[i - Warmup] = elapsed;
                    }
                    if (rc < 0) { report?.Invoke($"{candidate.Name} produced no immediate packet ({VideoCodec.Error(rc)})"); continue; }
                    Array.Sort(times);
                    results.Add(new(candidate, openMs, first, (times[Samples / 2 - 1] + times[Samples / 2]) / 2, times[(int)Math.Ceiling(.95 * Samples) - 1]));
                }
                catch (IOException e) { report?.Invoke($"{candidate.Name}: {e.Message}"); }
                finally { VideoCodec.CloseVideo(encoder); }
            }
        }
        var ranked = results.OrderBy(r => r.P95Ms).ThenBy(r => r.MedianMs).ToArray();
        Save(size, ranked);
        return ranked;
    }
}
