namespace DeskLink;

sealed class SettingsDialog : Form
{
    readonly Dictionary<Sharing, CheckBox> switches = [];
    readonly ComboBox tiling = Choice("Incoming window placement", "Free placement", "Fit left half", "Fit right half", "Fit work area");
    readonly ComboBox crossing = Choice("Cross the screen edge", "Move through the edge", "Hold Ctrl at the edge", "Hold Shift at the edge");
    static readonly FrameCodec[] modes = [FrameCodec.Auto, FrameCodec.Av1, FrameCodec.Hevc, FrameCodec.H264, FrameCodec.Png, FrameCodec.Jpeg];
    readonly ComboBox codec = Choice("Video codec", "Auto (fastest encoder)", "AV1", "HEVC (H.265)", "H.264 (AVC)", "PNG (lossless)", "JPEG");
    readonly TrackBar bitrate = new() { AccessibleName = "Target bitrate in Mbps per window", Minimum = 1, Maximum = 80,
        TickFrequency = 10, LargeChange = 5, Width = 156, Height = 34, AutoSize = false };
    readonly Label rateValue = DeskStyle.Caption("");
    readonly NumericUpDown fps = Number("Maximum frames per second", 60), quality = Number("JPEG quality", 100);
    readonly Dictionary<DeskAction, (ComboBox Modifier, ComboBox Key)> shortcuts = [];
    readonly DeskPages pages = new();
    DeskAppearance appearance;
    readonly Label actionError = DeskStyle.Caption("");
    public DeskSettings Selection => new()
    {
        Features = switches.Where(s => s.Value.Checked).Aggregate(Sharing.None, (features, s) => features | s.Key),
        Tiling = (TileMode)tiling.SelectedIndex, Crossing = (EdgeActivation)crossing.SelectedIndex, Appearance = appearance,
        Stream = new(modes[codec.SelectedIndex], (int)fps.Value, (int)quality.Value, bitrate.Value),
        Shortcuts = new() { History = Read(DeskAction.History), Recover = Read(DeskAction.Recover),
            Files = Read(DeskAction.Files), Performance = Read(DeskAction.Performance), Project = Read(DeskAction.Project) }
    };
    Keys Read(DeskAction action)
    {
        var (modifier, key) = shortcuts[action];
        return key.SelectedIndex <= 0 ? Keys.None : ActionShortcuts.Modifiers[modifier.SelectedIndex] | ActionShortcuts.MainKeys[key.SelectedIndex];
    }
    void SetShortcuts(ActionShortcuts bindings)
    {
        foreach (var (action, inputs) in shortcuts)
        {
            var chord = bindings[action];
            inputs.Modifier.SelectedIndex = Math.Max(0, Array.IndexOf(ActionShortcuts.Modifiers, chord & Keys.Modifiers));
            inputs.Key.SelectedIndex = Math.Max(0, Array.IndexOf(ActionShortcuts.MainKeys, chord & Keys.KeyCode));
            inputs.Modifier.Enabled = inputs.Key.SelectedIndex > 0;
        }
    }
    public SettingsDialog(DeskSettings current, Sharing? peerFeatures, Func<ActionShortcuts, string?>? checkAvailable = null)
    {
        SuspendLayout();
        appearance = current.Appearance;
        Text = "DeskPortal settings"; Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent; MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(590, 498); MinimumSize = new Size(520, 420);
        var body = DeskStyle.Column(Tone.Canvas, 16);
        DeskStyle.Row(body, DeskStyle.Heading("Settings", 24));
        DeskStyle.Row(body, pages, true);
        var sharing = Page("Sharing");
        Wide(sharing, DeskStyle.Heading("Share between computers"));
        Wide(sharing, DeskStyle.Caption("Enable each feature on both computers to use it."));
        foreach (var (flag, name) in new[] { (Sharing.Mouse, "Mouse and keyboard"), (Sharing.Files, "Files"),
            (Sharing.Clipboard, "Clipboard and history"), (Sharing.Windows, "Interactive windows") })
        {
            var check = new CheckBox { Text = name, AutoSize = true, Checked = current.Features.HasFlag(flag), Margin = new Padding(0, 5, 10, 5) };
            switches.Add(flag, check);
            if (peerFeatures.HasValue && !peerFeatures.Value.HasFlag(flag)) Field(sharing, check, DeskStyle.Caption("Off on other PC"));
            else Wide(sharing, check);
        }
        Wide(sharing, Section("When receiving a window"));
        Field(sharing, DeskStyle.Caption("Placement on drop"), tiling); tiling.SelectedIndex = (int)current.Tiling;

        var stream = Page("Streaming");
        Wide(stream, DeskStyle.Heading("Outgoing window video"));
        Field(stream, DeskStyle.Caption("Video codec"), codec);
        var rateLine = DeskStyle.LineOf(bitrate, rateValue); rateLine.WrapContents = false;
        Field(stream, DeskStyle.Caption("Bitrate per window"), rateLine);
        void UpdateRate() => rateValue.Text = $"{bitrate.Value} Mbps";
        bitrate.ValueChanged += (_, _) => UpdateRate(); bitrate.Value = current.Stream.Mbps; UpdateRate();
        Field(stream, DeskStyle.Caption("Maximum FPS"), fps); Field(stream, DeskStyle.Caption("JPEG quality"), quality);
        codec.SelectedIndexChanged += (_, _) =>
        {
            var selected = modes[codec.SelectedIndex];
            quality.Enabled = selected == FrameCodec.Jpeg;
            rateLine.Enabled = selected is not (FrameCodec.Jpeg or FrameCodec.Png);
        };
        codec.SelectedIndex = Array.IndexOf(modes, current.Stream.Codec);
        fps.Value = current.Stream.FrameRate; quality.Value = current.Stream.JpegQuality;

        var actions = Page("Actions");
        Wide(actions, DeskStyle.Heading("Screen edge"));
        Field(actions, DeskStyle.Caption("Leave this PC"), crossing); crossing.SelectedIndex = (int)current.Crossing;
        Wide(actions, DeskStyle.Caption("Window and file drags can always cross the edge."));
        Wide(actions, Section("Keyboard shortcuts"));
        foreach (var action in ActionShortcuts.Actions)
        {
            string name = ActionShortcuts.Name(action);
            var modifier = Choice($"{name} modifier", ActionShortcuts.Modifiers.Select(ActionShortcuts.Format).ToArray());
            var key = Choice($"{name} key", ActionShortcuts.MainKeys.Select(ActionShortcuts.Format).ToArray());
            modifier.Width = 132; key.Width = 104;
            key.SelectedIndexChanged += (_, _) => { modifier.Enabled = key.SelectedIndex > 0; actionError.Visible = false; };
            modifier.SelectedIndexChanged += (_, _) => actionError.Visible = false;
            shortcuts.Add(action, (modifier, key));
            var inputs = DeskStyle.LineOf(modifier, key); inputs.WrapContents = false;
            Field(actions, DeskStyle.Caption(name), inputs);
        }
        SetShortcuts(current.Shortcuts);
        var reset = new BlockButton { Text = "Reset shortcuts" }; DeskStyle.Button(reset);
        reset.Click += (_, _) => SetShortcuts(new());
        Wide(actions, reset);
        actionError.AccessibleRole = AccessibleRole.Alert; actionError.MaximumSize = new Size(450, 0); actionError.Visible = false;
        actionError.Tag = Tone.Inverse;
        Wide(actions, actionError);

        var colors = Page("Appearance");
        Wide(colors, DeskStyle.Heading("Color palette"));
        var mode = Choice("Theme", "Light", "Dark"); mode.SelectedIndex = (int)appearance.Theme;
        Field(colors, DeskStyle.Caption("Theme"), mode);
        Label Swatch(Tone tone) => new() { AutoSize = true, Tag = tone, Font = new Font("Consolas", 12, FontStyle.Bold),
            Padding = new Padding(12, 14, 12, 14), Margin = new Padding(0, 6, 8, 6) };
        var primary = Swatch(Tone.Primary); var complement = Swatch(Tone.Complement);
        Wide(colors, DeskStyle.LineOf(primary, complement));
        var shuffle = new BlockButton { Text = "Shuffle colors" }; DeskStyle.Button(shuffle);
        Wide(colors, shuffle);
        Wide(colors, DeskStyle.Caption("Each theme keeps its own complementary pair."));
        void Preview()
        {
            var p = DeskPalette.From(appearance, SystemInformation.HighContrast);
            primary.Text = $"PRIMARY  {ColorTranslator.ToHtml(p.Primary)}";
            complement.Text = $"COMPLEMENT  {ColorTranslator.ToHtml(p.Complement)}";
            DeskStyle.Apply(this, appearance);
        }
        mode.SelectedIndexChanged += (_, _) => { appearance = appearance with { Theme = (DeskTheme)mode.SelectedIndex }; Preview(); };
        shuffle.Click += (_, _) => { appearance = appearance.Shuffle(); Preview(); };
        var save = new BlockButton { Text = "Save", DialogResult = DialogResult.OK };
        var cancel = new BlockButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        DeskStyle.Button(save, true); DeskStyle.Button(cancel);
        var footer = DeskStyle.LineOf(save, cancel); footer.FlowDirection = FlowDirection.RightToLeft;
        footer.Padding = new Padding(0, 10, 0, 0); DeskStyle.Row(body, footer);
        Controls.Add(body); AcceptButton = save; CancelButton = cancel;
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;
            var bindings = Selection.Shortcuts;
            string? error = bindings.Error() ?? (bindings != current.Shortcuts ? checkAvailable?.Invoke(bindings) : null);
            if (error == null) return;
            e.Cancel = true; DialogResult = DialogResult.None;
            pages.SelectPage("Actions");
            actionError.Text = error; actionError.Visible = true;
            if (actions.Parent is ScrollableControl scroll) scroll.ScrollControlIntoView(actionError);
        };
        Preview(); ResumeLayout(true);
    }
    TableLayoutPanel Page(string title)
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Tag = Tone.Surface, Padding = new Padding(14) };
        var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(grid); pages.AddPage(title, scroll); return grid;
    }
    static void Wide(TableLayoutPanel grid, Control control)
    {
        int row = grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(control, 0, row); grid.SetColumnSpan(control, 2);
    }
    static void Field(TableLayoutPanel grid, Control label, Control control)
    {
        int row = grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        label.Margin = new Padding(0, 6, 14, 6);
        if (control is ComboBox or NumericUpDown) control.MinimumSize = new Size(control.Width, 0);
        control.Anchor = AnchorStyles.Left;
        grid.Controls.Add(label, 0, row); grid.Controls.Add(control, 1, row);
    }
    static Label Section(string title)
    {
        var label = DeskStyle.Heading(title); label.Margin = new Padding(0, 15, 0, 4); return label;
    }
    static ComboBox Choice(string name, params string[] items)
    {
        var box = new ComboBox { AccessibleName = name, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 192 };
        box.Items.AddRange(items); return box;
    }
    static NumericUpDown Number(string name, int max) => new() { AccessibleName = name, Minimum = 1, Maximum = max, Width = 80 };
}
