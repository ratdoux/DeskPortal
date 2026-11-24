using System.Security.Cryptography;

namespace DeskLink;

enum DeskTheme { Light, Dark }

sealed record DeskAppearance(DeskTheme Theme, int LightHue, int DarkHue)
{
    public static DeskAppearance Initial { get; } = new(DeskTheme.Light, RandomNumberGenerator.GetInt32(360), RandomNumberGenerator.GetInt32(360));
    [System.Text.Json.Serialization.JsonIgnore]
    public int Hue => Theme == DeskTheme.Dark ? DarkHue : LightHue;
    public DeskAppearance Validated() => new(Enum.IsDefined(Theme) ? Theme : DeskTheme.Light,
        LightHue is >= 0 and < 360 ? LightHue : Initial.LightHue, DarkHue is >= 0 and < 360 ? DarkHue : Initial.DarkHue);
    public DeskAppearance Shuffle()
    {
        // A noticeable change on each click, with every hue reachable on subsequent shuffles.
        int next = (Hue + RandomNumberGenerator.GetInt32(35, 326)) % 360;
        return Theme == DeskTheme.Dark ? this with { DarkHue = next } : this with { LightHue = next };
    }
}

sealed record DeskPalette(Color Canvas, Color Surface, Color Ink, Color Muted, Color Primary, Color Complement, bool HighContrast)
{
    public static DeskPalette From(DeskAppearance appearance, bool highContrast)
    {
        if (highContrast) return new(SystemColors.Control, SystemColors.Window, SystemColors.WindowText, SystemColors.WindowText,
            SystemColors.Highlight, SystemColors.Highlight, true);
        bool dark = appearance.Theme == DeskTheme.Dark;
        double lightness = dark ? .69 : .68;
        return new(dark ? Color.FromArgb(24, 23, 29) : Color.FromArgb(248, 246, 238),
            dark ? Color.FromArgb(39, 38, 45) : Color.White,
            dark ? Color.FromArgb(246, 246, 239) : Color.FromArgb(17, 17, 19),
            dark ? Color.FromArgb(199, 199, 192) : Color.FromArgb(76, 76, 71),
            Hsl(appearance.Hue, lightness), Hsl((appearance.Hue + 180) % 360, lightness), false);
    }
    static Color Hsl(int hue, double lightness)
    {
        double amplitude = .91 * Math.Min(lightness, 1 - lightness);
        int Channel(int n)
        {
            double k = (n + hue / 30d) % 12;
            return (int)Math.Round(255 * (lightness - amplitude * Math.Max(-1, Math.Min(1, Math.Min(k - 3, 9 - k)))));
        }
        return Color.FromArgb(Channel(0), Channel(8), Channel(4));
    }
    internal static double Luminance(Color c)
    {
        static double Linear(byte b) { double s = b / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
    }
    public Color OnAccent(Color fill)
    {
        if (HighContrast) return SystemColors.HighlightText;
        double luminance = Luminance(fill);
        return (luminance + .05) / .05 >= 1.05 / (luminance + .05) ? Color.Black : Color.White;
    }
}
