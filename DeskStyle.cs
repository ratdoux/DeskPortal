namespace DeskLink;

enum Tone { Canvas, Surface, Primary, Complement, Quiet, Inverse, Frame }

static class DeskStyle
{
    static readonly DeskPalette initial = DeskPalette.From(DeskAppearance.Initial, SystemInformation.HighContrast);
    public static DeskPalette PaletteFor(Control c) => c.FindForm()?.Tag as DeskPalette ?? initial;
    public static void Apply(Form form, DeskAppearance appearance) =>
        ApplyPalette(form, DeskPalette.From(appearance, SystemInformation.HighContrast));
    public static void ApplyPalette(Form form, DeskPalette palette)
    {
        form.Tag = palette;
        form.HandleCreated -= StyleTitle;
        form.HandleCreated += StyleTitle;
        if (form.IsHandleCreated) StyleTitle(form, EventArgs.Empty);
        void Paint(Control c, Color back, Color fore)
        {
            if (c is Form) { back = palette.Canvas; fore = palette.Ink; }
            if (c is ComboBox or TextBoxBase or NumericUpDown or ListBox or ListView or TabPage)
            { back = palette.Surface; fore = palette.Ink; }
            if (c.Tag is Tone tone)
            {
                (back, fore) = tone switch
                {
                    Tone.Primary => (palette.Primary, palette.OnAccent(palette.Primary)),
                    Tone.Complement => (palette.Complement, palette.OnAccent(palette.Complement)),
                    Tone.Inverse => (palette.Ink, palette.Canvas),
                    Tone.Canvas => (palette.Canvas, palette.Ink),
                    Tone.Quiet => (back, palette.Muted),
                    _ => (palette.Surface, palette.Ink)
                };
            }
            if (c is Button button) ColorButton(button, palette);
            else if (c is RadioButton { Appearance: System.Windows.Forms.Appearance.Button } tab) ColorTab(tab, palette);
            else { c.BackColor = back; c.ForeColor = fore; }
            foreach (Control child in c.Controls) Paint(child, c.BackColor, c.ForeColor);
            c.Invalidate();
        }
        Paint(form, palette.Canvas, palette.Ink);
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int bytes);
    static void StyleTitle(object? sender, EventArgs e)
    {
        if (sender is not Form { Tag: DeskPalette p } form || p.HighContrast) return;
        int dark = DeskPalette.Luminance(p.Canvas) < .15 ? 1 : 0;
        DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
    }
    static void ColorButton(Button button, DeskPalette p)
    {
        Color back = button.Enabled ? button.Tag switch { Tone.Primary => p.Primary, Tone.Complement => p.Complement, _ => p.Surface } : p.Canvas;
        button.BackColor = back; button.ForeColor = button.Enabled && button.Tag is Tone.Primary or Tone.Complement ? p.OnAccent(back) : p.Ink;
        button.FlatAppearance.BorderColor = button.Enabled ? p.Ink : p.Muted;
    }
    internal static void ColorTab(RadioButton tab, DeskPalette p)
    {
        tab.BackColor = tab.Checked ? p.Primary : p.Surface;
        tab.ForeColor = tab.Checked ? p.OnAccent(p.Primary) : p.Ink;
        tab.FlatAppearance.BorderColor = p.Ink; tab.FlatAppearance.CheckedBackColor = p.Primary;
        tab.FlatAppearance.MouseOverBackColor = tab.BackColor; tab.FlatAppearance.MouseDownBackColor = tab.BackColor;
    }

    public static Label Heading(string title, float size = 14) => new()
    {
        Text = title.ToUpperInvariant(), AutoSize = true, Font = new Font("Impact", size + 2),
        Margin = new Padding(0, 0, 0, 8), Anchor = AnchorStyles.Left
    };
    public static void Button(Button button, bool primary = false)
    {
        button.AutoSize = true; button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.FlatStyle = FlatStyle.Flat; button.UseVisualStyleBackColor = false;
        button.Tag = primary ? Tone.Primary : Tone.Surface;
        button.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        button.FlatAppearance.BorderSize = 2;
        button.Padding = new Padding(9, 3, 9, 3); button.Margin = new Padding(0, 3, 7, 3);
        button.Cursor = Cursors.Hand;
        button.EnabledChanged += (_, _) => ColorButton(button, PaletteFor(button));
    }
    public static FlowLayoutPanel LineOf(params Control[] controls)
    {
        var line = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, Margin = Padding.Empty };
        line.Controls.AddRange(controls);
        return line;
    }
    public static TableLayoutPanel Column(Tone tone, int padding = 0)
    {
        TableLayoutPanel grid = tone == Tone.Frame ? new BlockPanel() : new TableLayoutPanel();
        grid.ColumnCount = 1; grid.RowCount = 0; grid.Tag = tone; grid.Dock = DockStyle.Fill;
        grid.Padding = new Padding(padding); grid.Margin = Padding.Empty; return grid;
    }
    public static void Row(TableLayoutPanel grid, Control child, bool expand = false)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(expand ? SizeType.Percent : SizeType.AutoSize, expand ? 100 : 0));
        grid.Controls.Add(child, 0, row);
    }
    public static Label Caption(string text) => new() { Text = text, AutoSize = true, Tag = Tone.Quiet,
        Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 4) };
}

sealed class BlockPanel : TableLayoutPanel
{
    public BlockPanel() { DoubleBuffered = true; ResizeRedraw = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var p = DeskStyle.PaletteFor(this);
        e.Graphics.Clear(Parent?.BackColor ?? p.Canvas);
        BlockFace.Draw(e.Graphics, ClientRectangle, BackColor, p.Ink, LogicalToDeviceUnits(2), LogicalToDeviceUnits(5));
    }
}

static class BlockFace
{
    public static Rectangle Draw(Graphics graphics, Rectangle bounds, Color fill, Color ink, int line, int offset)
    {
        var face = new Rectangle(bounds.X, bounds.Y, bounds.Width - offset, bounds.Height - offset);
        if (face.Width <= 0 || face.Height <= 0) return Rectangle.Empty;
        using var outline = new SolidBrush(ink);
        using var paper = new SolidBrush(fill);
        graphics.FillRectangle(outline, face.X + offset, face.Y + offset, face.Width, face.Height);
        graphics.FillRectangle(paper, face);
        ControlPaint.DrawBorder(graphics, face, ink, line, ButtonBorderStyle.Solid, ink, line,
            ButtonBorderStyle.Solid, ink, line, ButtonBorderStyle.Solid, ink, line, ButtonBorderStyle.Solid);
        return face;
    }
}

// Keep the native button's activation, keyboard and accessibility behavior while drawing its raised face.
sealed class BlockButton : Button
{
    bool held, spaceDown, hovered;
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); held = e.Button == MouseButtons.Left; Invalidate(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); held = false; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); held = false; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) { spaceDown = true; Invalidate(); } }
    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (e.KeyCode == Keys.Space) { spaceDown = false; Invalidate(); } }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); spaceDown = false; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var p = DeskStyle.PaletteFor(this);
        var area = ClientRectangle;
        e.Graphics.Clear(Parent?.BackColor ?? p.Canvas);
        int depth = LogicalToDeviceUnits(3);
        int travel = Enabled && (spaceDown || (held && hovered)) ? depth : Enabled && hovered ? LogicalToDeviceUnits(1) : 0;
        area = Rectangle.FromLTRB(area.Left + travel, area.Top + travel, area.Right, area.Bottom);
        var ink = Enabled ? p.Ink : p.Muted;
        var face = BlockFace.Draw(e.Graphics, area, BackColor, ink, LogicalToDeviceUnits(2), depth - travel);
        if (face.IsEmpty) return;
        TextRenderer.DrawText(e.Graphics, Text, Font, face, Enabled ? ForeColor : p.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            (ShowKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix));
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(face, -LogicalToDeviceUnits(4), -LogicalToDeviceUnits(4)), ForeColor, BackColor);
    }
}

sealed class DeskBanner : TableLayoutPanel
{
    public DeskBanner() { DoubleBuffered = true; ResizeRedraw = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var ink = new SolidBrush(ForeColor);
        int rule = Math.Max(1, LogicalToDeviceUnits(2)), step = LogicalToDeviceUnits(10);
        e.Graphics.FillRectangle(ink, 0, Height - rule, Width, rule);
        if (Controls.Count < 3) return;
        int right = Controls[2].Left - step, left = Math.Max(Controls[1].Right + 2 * step, right - 11 * step);
        // A tapered halftone field fills only the gap between the wordmark and the computer name.
        for (int row = 0; row < 4; row++)
            for (int x = left + row * step; x < right; x += step)
                e.Graphics.FillRectangle(ink, x, Height / 2 + (row - 2) * step, rule, rule);
    }
}

sealed class EmptyListLabel(bool clipboard) : Label
{
    protected override void OnPaint(PaintEventArgs e)
    {
        var p = DeskStyle.PaletteFor(this);
        int gap = LogicalToDeviceUnits(8), size = LogicalToDeviceUnits(38);
        int textHeight = TextRenderer.MeasureText(e.Graphics, Text, Font, new Size(Math.Max(1, Width), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        if (Height < size + gap + textHeight) { base.OnPaint(e); return; }
        int y = (Height - size - gap - textHeight) / 2;
        var saved = e.Graphics.Save();
        e.Graphics.TranslateTransform((Width - size) / 2, y);
        e.Graphics.ScaleTransform(size / 38f, size / 38f);
        using var ink = new Pen(p.Ink, 2);
        using var accent = new SolidBrush(clipboard ? p.Complement : p.Primary);
        if (clipboard)
        {
            e.Graphics.FillRectangle(accent, 11, 8, 21, 27); e.Graphics.DrawRectangle(ink, 11, 8, 21, 27);
            using var face = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(face, 6, 5, 22, 26); e.Graphics.DrawRectangle(ink, 6, 5, 22, 26);
            e.Graphics.FillRectangle(accent, 12, 2, 10, 6); e.Graphics.DrawRectangle(ink, 12, 2, 10, 6);
            e.Graphics.DrawLine(ink, 11, 15, 23, 15); e.Graphics.DrawLine(ink, 11, 21, 19, 21);
        }
        else
        {
            e.Graphics.FillRectangle(accent, 2, 7, 25, 18); e.Graphics.DrawRectangle(ink, 2, 7, 25, 18);
            e.Graphics.DrawLine(ink, 14, 26, 14, 31); e.Graphics.DrawLine(ink, 7, 32, 21, 32);
            e.Graphics.DrawArc(ink, 23, 8, 9, 15, -55, 110); e.Graphics.DrawArc(ink, 20, 4, 16, 23, -55, 110);
        }
        e.Graphics.Restore(saved);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, y + size + gap, Width, textHeight), ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }
}

sealed class ScreenArrangement : Control
{
    bool peerRight = true;
    DeskLayout? map;
    string peerName = "Other PC";
    public ScreenArrangement()
    {
        Dock = DockStyle.Top; Height = 53; Margin = new Padding(0, 0, 0, 4);
        TabStop = false; DoubleBuffered = true; AccessibleRole = AccessibleRole.Graphic; Describe();
    }
    public void SetPeer(string? name) { peerName = name ?? "Other PC"; Describe(); Invalidate(); }
    public void SetSide(bool right) { peerRight = right; Describe(); Invalidate(); }
    public void SetLayout(DeskLayout? value) { map = value; Describe(); Invalidate(); }
    void Describe() => AccessibleName = map?.Editable == true ? $"{map.Here.Length} displays on this PC and {map.There.Length} on {peerName}" :
        $"{peerName} is on the {(peerRight ? "right" : "left")} of this PC";
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (map?.Editable == true)
        {
            var palette = DeskStyle.PaletteFor(this);
            var area = map.Tiles.Select(t => t.Desk).Aggregate(Rectangle.Union);
            float scale = Math.Min((Width-4f)/area.Width,(Height-4f)/area.Height);
            if (scale <= 0) return;
            foreach (var t in map.Tiles)
            {
                var box = Rectangle.Round(new RectangleF(2+(t.Desk.X-area.X)*scale,2+(t.Desk.Y-area.Y)*scale,
                    t.Desk.Width*scale,t.Desk.Height*scale));
                var fill = t.Here ? palette.Primary : palette.Complement;
                BlockFace.Draw(e.Graphics,box,fill,palette.Ink,LogicalToDeviceUnits(1),0);
                TextRenderer.DrawText(e.Graphics,t.Here ? Environment.MachineName : peerName,Font,box,palette.OnAccent(fill),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            return;
        }
        int pad = LogicalToDeviceUnits(6), gap = pad * 2, w = Math.Min(LogicalToDeviceUnits(104), (Width - gap - 2) / 2);
        int h = Height - pad * 2;
        if (w <= pad || h <= pad) return;
        void Monitor(int x, bool local)
        {
            var box = new Rectangle(x, 1, w, h);
            var palette = DeskStyle.PaletteFor(this);
            var color = local ? palette.Primary : palette.Complement;
            using var fill = new SolidBrush(color);
            using var pen = new Pen(palette.Ink, LogicalToDeviceUnits(2));
            e.Graphics.FillRectangle(fill, box);
            e.Graphics.DrawRectangle(pen, box);
            using var stand = new Pen(palette.Ink, Math.Max(2, LogicalToDeviceUnits(2)));
            int mid = x + w / 2;
            e.Graphics.DrawLine(stand, mid, box.Bottom, mid, box.Bottom + pad);
            e.Graphics.DrawLine(stand, mid - pad * 2, box.Bottom + pad, mid + pad * 2, box.Bottom + pad);
            TextRenderer.DrawText(e.Graphics, local ? Environment.MachineName : peerName, Font,
                Rectangle.Inflate(box, -pad / 2, 0), palette.OnAccent(color),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        Monitor(1, peerRight); Monitor(1 + w + gap, !peerRight);
    }
}

sealed class DeskMark : Control
{
    public DeskMark()
    {
        Size = new Size(43, 38); TabStop = false; AccessibleRole = AccessibleRole.Graphic; AccessibleName = "DeskPortal";
        Margin = new Padding(0, 0, 10, 0);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.ScaleTransform(Width / 43f, Height / 38f);
        var palette = DeskStyle.PaletteFor(this);
        using var rear = new Pen(ForeColor, 3);
        using var front = new Pen(ForeColor, 3);
        using var fill = new SolidBrush(palette.Complement);
        g.DrawRectangle(rear, 3, 5, 23, 17); g.DrawLine(rear, 10, 25, 21, 25);
        g.FillRectangle(fill, 16, 14, 23, 16); g.DrawRectangle(front, 16, 14, 23, 16);
        g.DrawLine(front, 27, 30, 27, 34); g.DrawLine(front, 21, 34, 33, 34);
    }
}

sealed class DeskList : ListBox
{
    public DeskList()
    {
        Dock = DockStyle.Fill; IntegralHeight = false; BorderStyle = BorderStyle.None;
        DrawMode = DrawMode.OwnerDrawFixed;
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateHeight(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateHeight(); }
    void UpdateHeight() => ItemHeight = Math.Max(LogicalToDeviceUnits(45), Font.Height * 2 + LogicalToDeviceUnits(5));
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var palette = DeskStyle.PaletteFor(this);
        bool selected = e.State.HasFlag(DrawItemState.Selected);
        using var background = new SolidBrush(selected ? palette.Complement : BackColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        var text = selected ? palette.OnAccent(palette.Complement) : ForeColor;
        var (title, subtitle) = Items[e.Index] switch
        {
            NearbyComputer pc => (pc.Name, pc.Address.ToString()),
            HistoryItem entry => (entry.Clip.Label, entry.Clip.Origin),
            var item => (item?.ToString() ?? "", "")
        };
        var bounds = Rectangle.Inflate(e.Bounds, -LogicalToDeviceUnits(6), 0);
        int height = Font.Height;
        var row = new Rectangle(bounds.X, bounds.Y + (bounds.Height - height * 2) / 2, bounds.Width, height);
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(e.Graphics, title, Font, row, text, flags);
        row.Y += height;
        TextRenderer.DrawText(e.Graphics, subtitle, Font, row,
            selected ? text : palette.Muted, flags);
        e.DrawFocusRectangle();
    }
}
