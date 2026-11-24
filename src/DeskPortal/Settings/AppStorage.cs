namespace DeskLink;

static class AppStorage
{
    // Upgrades continue to use the existing settings, received files and encoder cache.
    public static readonly string Root = Resolve(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    internal static string Resolve(string appData)
    {
        string current = Path.Combine(appData, "DeskPortal");
        if (Directory.Exists(current)) return current;
        foreach (string previous in new[] { "DeskStream", "DeskLink" })
        {
            string path = Path.Combine(appData, previous);
            if (Directory.Exists(path)) return path;
        }
        return current;
    }
}
