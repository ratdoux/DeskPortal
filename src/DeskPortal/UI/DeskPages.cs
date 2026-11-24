namespace DeskLink;

// Radio buttons keep native keyboard navigation and expose the selected page to assistive technology.
sealed class DeskPages : TableLayoutPanel
{
    readonly FlowLayoutPanel navigation = DeskStyle.LineOf();
    readonly Panel content = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, Tag = Tone.Surface };
    readonly List<(RadioButton Tab, Control Page)> pages = [];
    public DeskPages()
    {
        Dock = DockStyle.Fill; Margin = Padding.Empty; ColumnCount = 1; RowCount = 0;
        navigation.WrapContents = false; navigation.Padding = new Padding(0, 0, 0, 4);
        DeskStyle.Row(this, navigation);
        var frame = DeskStyle.Column(Tone.Frame);
        frame.Padding = new Padding(2, 2, 7, 7);
        DeskStyle.Row(frame, content, true); DeskStyle.Row(this, frame, true);
    }
    public void AddPage(string title, Control page)
    {
        var tab = new RadioButton { Text = title, Appearance = Appearance.Button, AutoSize = true, FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false, AccessibleName = title, AccessibleRole = AccessibleRole.PageTab,
            Font = new Font("Segoe UI", 9, FontStyle.Bold), Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 4, 0) };
        tab.FlatAppearance.BorderSize = 2;
        page.Dock = DockStyle.Fill; page.Visible = false;
        navigation.Controls.Add(tab); content.Controls.Add(page); pages.Add((tab, page));
        tab.CheckedChanged += (_, _) =>
        {
            page.Visible = tab.Checked;
            if (tab.Checked) page.BringToFront();
            DeskStyle.ColorTab(tab, DeskStyle.PaletteFor(tab));
        };
        tab.Checked = pages.Count == 1;
    }
    public void SelectPage(string title) => pages.First(p => p.Tab.Text == title).Tab.Checked = true;
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is (Keys.Control | Keys.Tab) or (Keys.Control | Keys.Shift | Keys.Tab))
        {
            int index = pages.FindIndex(p => p.Tab.Checked);
            var tab = pages[(index + (keyData.HasFlag(Keys.Shift) ? -1 : 1) + pages.Count) % pages.Count].Tab;
            tab.Checked = true; tab.Focus(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
