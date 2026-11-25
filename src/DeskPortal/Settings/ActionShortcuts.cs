namespace DeskLink;

enum DeskAction { History = 1, Recover, Files, Performance, Project }
enum EdgeActivation { Automatic, HoldCtrl, HoldShift }

sealed record ActionShortcuts
{
    public Keys History { get; init; } = Keys.Control | Keys.Alt | Keys.V;
    public Keys Recover { get; init; } = Keys.Control | Keys.Alt | Keys.Home;
    public Keys Files { get; init; }
    public Keys Performance { get; init; }
    public Keys Project { get; init; } = Keys.Control | Keys.Alt | Keys.P;

    internal static readonly DeskAction[] Actions = Enum.GetValues<DeskAction>();
    internal static readonly Keys[] Modifiers = [Keys.Control | Keys.Alt, Keys.Control | Keys.Shift, Keys.Alt | Keys.Shift,
        Keys.Control | Keys.Alt | Keys.Shift, Keys.Control, Keys.Alt];
    internal static readonly Keys[] MainKeys = [Keys.None, .. Enumerable.Range((int)Keys.A, 26).Select(k => (Keys)k),
        .. Enumerable.Range((int)Keys.D0, 10).Select(k => (Keys)k),
        .. Enumerable.Range((int)Keys.F1, 11).Select(k => (Keys)k), Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown,
        Keys.Insert, Keys.Left, Keys.Right, Keys.Up, Keys.Down];

    public Keys this[DeskAction action] => action switch
    {
        DeskAction.History => History, DeskAction.Recover => Recover, DeskAction.Files => Files,
        DeskAction.Performance => Performance, DeskAction.Project => Project, _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
    public static string Name(DeskAction action) => action switch
    {
        DeskAction.History => "Open clipboard history", DeskAction.Recover => "Bring windows and input home",
        DeskAction.Files => "Send files", DeskAction.Performance => "Open performance", DeskAction.Project => "Project a screen",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
    public static string Format(Keys chord)
    {
        if (chord == Keys.None) return "Unassigned";
        var parts = new List<string>();
        if (chord.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (chord.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (chord.HasFlag(Keys.Shift)) parts.Add("Shift");
        var key = chord & Keys.KeyCode;
        if (key != Keys.None) parts.Add(key switch { >= Keys.D0 and <= Keys.D9 => ((int)(key - Keys.D0)).ToString(),
            Keys.PageUp => "Page Up", Keys.PageDown => "Page Down", _ => key.ToString() });
        return string.Join(" + ", parts);
    }
    public string? Error()
    {
        var used = new Dictionary<Keys, DeskAction>();
        foreach (var action in Actions)
        {
            Keys chord = this[action], key = chord & Keys.KeyCode;
            if (chord == Keys.None) continue;
            if (key == Keys.None || !MainKeys.Contains(key) || !Modifiers.Contains(chord & Keys.Modifiers) || chord == (Keys.Alt | Keys.F4))
                return $"Choose a different shortcut for {Name(action)}.";
            if (!used.TryAdd(chord, action))
                return $"{Name(used[chord])} and {Name(action)} both use {Format(chord)}. Choose a different shortcut for one.";
        }
        return null;
    }
}

// Windows owns global chord registration. The hook uses only successfully registered
// bindings when the originating keyboard is controlling the peer.
sealed class SessionHotkeys
{
    const uint NoRepeat = 0x4000;
    readonly Dictionary<DeskAction, Keys> registered = [];
    static uint Mod(Keys chord) => (chord.HasFlag(Keys.Alt) ? 1u : 0) |
        (chord.HasFlag(Keys.Control) ? 2u : 0) | (chord.HasFlag(Keys.Shift) ? 4u : 0);
    static bool Register(nint hwnd, int id, Keys chord) =>
        Win.RegisterHotKey(hwnd, id, NoRepeat | Mod(chord), (uint)(chord & Keys.KeyCode));

    public string? CheckAvailable(nint hwnd, ActionShortcuts bindings)
    {
        foreach (var action in ActionShortcuts.Actions)
        {
            var chord = bindings[action];
            if (chord == Keys.None || registered.ContainsValue(chord)) continue;
            const int probe = 100;
            if (!Register(hwnd, probe, chord)) return $"{ActionShortcuts.Format(chord)} is unavailable on this PC. Choose another shortcut for {ActionShortcuts.Name(action)}.";
            Win.UnregisterHotKey(hwnd, probe);
        }
        return null;
    }

    public string? Update(nint hwnd, ActionShortcuts bindings, Func<DeskAction, bool> enabled)
    {
        foreach (var action in ActionShortcuts.Actions)
            if (registered.TryGetValue(action, out var existing) && (existing != bindings[action] || !enabled(action)))
            {
                Win.UnregisterHotKey(hwnd, (int)action);
                registered.Remove(action);
            }
        var unavailable = new List<string>();
        foreach (var action in ActionShortcuts.Actions)
        {
            var chord = bindings[action];
            if (chord == Keys.None || !enabled(action) || registered.ContainsKey(action)) continue;
            if (Register(hwnd, (int)action, chord)) registered.Add(action, chord);
            else unavailable.Add($"{ActionShortcuts.Name(action)} ({ActionShortcuts.Format(chord)})");
        }
        return unavailable.Count == 0 ? null : $"Shortcut unavailable on this PC: {string.Join(", ", unavailable)}. Change it in Settings → Actions.";
    }
    public DeskAction? Find(Keys chord)
    {
        foreach (var item in registered) if (item.Value == chord) return item.Key;
        return null;
    }
    public bool Matches(DeskAction action, nint message)
    {
        if (!registered.TryGetValue(action, out var chord)) return false;
        long data = message.ToInt64();
        return (data & 0xffff) == Mod(chord) && ((data >> 16) & 0xffff) == (int)(chord & Keys.KeyCode);
    }
    public void Clear(nint hwnd)
    {
        foreach (var action in registered.Keys) Win.UnregisterHotKey(hwnd, (int)action);
        registered.Clear();
    }
}
