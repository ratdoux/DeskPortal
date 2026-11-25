using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskLink;

[Flags]
enum Sharing { None = 0, Mouse = 1, Files = 2, Clipboard = 4, Windows = 8, All = Mouse | Files | Clipboard | Windows }
enum TileMode { Free, LeftHalf, RightHalf, WorkArea }
enum FrameCodec { Jpeg, Png, Auto, Av1, Hevc, H264 }

sealed record StreamOptions(FrameCodec Codec = FrameCodec.Auto, int FrameRate = 60, int JpegQuality = 90, int Mbps = StreamOptions.DefaultMbps)
{
    public const int DefaultMbps = 10;
    public StreamOptions Validated() => new(Enum.IsDefined(Codec) ? Codec : FrameCodec.Auto,
        Math.Clamp(FrameRate, 1, 60), Math.Clamp(JpegQuality, 1, 100), Math.Clamp(Mbps, 1, 80));
}

sealed record DeskSettings
{
    public Sharing Features { get; init; } = Sharing.All;
    public TileMode Tiling { get; init; }
    public StreamOptions Stream { get; init; } = new();
    public ActionShortcuts Shortcuts { get; init; } = new();
    public EdgeActivation Crossing { get; init; } = EdgeActivation.Automatic;
    public DeskAppearance Appearance { get; init; } = DeskAppearance.Initial;
    static string DefaultPath => Path.Combine(AppStorage.Root, "settings.json");
    static readonly JsonSerializerOptions json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static DeskSettings Load(string? path = null)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path ?? DefaultPath));
            var saved = document.RootElement.Deserialize<DeskSettings>(json) ?? throw new JsonException();
            var stream = saved.Stream?.Validated() ?? new();
            if (stream.Codec == FrameCodec.Jpeg && document.RootElement.TryGetProperty("Stream", out var old) &&
                old.ValueKind == JsonValueKind.Object && !old.TryGetProperty("Mbps", out _))
                stream = stream with { Codec = FrameCodec.Auto };
            var shortcuts = saved.Shortcuts ?? new();
            if (document.RootElement.TryGetProperty("Shortcuts", out var oldKeys) && oldKeys.ValueKind == JsonValueKind.Object &&
                !oldKeys.TryGetProperty("Project", out _) && ActionShortcuts.Actions.Any(a => a != DeskAction.Project && shortcuts[a] == shortcuts.Project))
                shortcuts = shortcuts with { Project = Keys.None };
            return saved with { Features = saved.Features & Sharing.All, Tiling = Enum.IsDefined(saved.Tiling) ? saved.Tiling : TileMode.Free,
                Stream = stream, Appearance = saved.Appearance?.Validated() ?? DeskAppearance.Initial,
                Crossing = Enum.IsDefined(saved.Crossing) ? saved.Crossing : EdgeActivation.Automatic,
                Shortcuts = shortcuts.Error() == null ? shortcuts : new() };
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
    }
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this, json)); File.Move(temporary, path, true); }
        finally { File.Delete(temporary); }
    }
    public static Rectangle Place(TileMode mode, Rectangle original, Rectangle work, bool reflow = false)
    {
        if (mode == TileMode.Free) return original;
        int leftWidth = work.Width / 2;
        var tile = mode switch
        {
            TileMode.LeftHalf => new Rectangle(work.Left, work.Top, leftWidth, work.Height),
            TileMode.RightHalf => new Rectangle(work.Left + leftWidth, work.Top, work.Width - leftWidth, work.Height),
            _ => work
        };
        if (reflow) return tile;
        double scale = Math.Min((double)tile.Width / original.Width, (double)tile.Height / original.Height);
        int width = Math.Max(1, (int)Math.Round(original.Width * scale));
        int height = Math.Max(1, (int)Math.Round(original.Height * scale));
        return new(tile.Left + (tile.Width - width) / 2, tile.Top + (tile.Height - height) / 2, width, height);
    }
}
