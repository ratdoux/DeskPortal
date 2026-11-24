using System.Threading.Channels;

namespace DeskLink;

// Every slow operation measured on a shared window is appended here. The Performance
// panel shows only the latest one, and only while that window is still shared.
static class StallLog
{
    // Enabled by the interactive LAN timing check. Each duration uses one PC's clock.
    internal static bool Timing { get; set; }
    internal static void Stage(string stage, long start)
    {
        if (Timing) Console.WriteLine($"Timing {Environment.MachineName} {stage} {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1} ms");
    }
    const long MaxBytes = 1024 * 1024;
    public static readonly string File = Path.Combine(
        AppStorage.Root, "slow-stages.log");
    static readonly Channel<string> lines = Channel.CreateBounded<string>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    static int started;

    // Never blocks the caller. Lines are dropped if the disk cannot keep up.
    public static void Write(string line)
    {
        if (Interlocked.Exchange(ref started, 1) == 0) _ = Task.Run(Drain);
        lines.Writer.TryWrite($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {Environment.MachineName}  {line}");
    }

    static async Task Drain()
    {
        await foreach (var line in lines.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                var current = new FileInfo(File);
                if (current.Exists && current.Length > MaxBytes) System.IO.File.Move(File, File + ".old", true);
                System.IO.File.AppendAllText(File, line + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
