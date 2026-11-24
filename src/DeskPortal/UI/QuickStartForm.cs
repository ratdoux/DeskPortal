namespace DeskLink;

sealed class QuickStartForm : Form
{
    public QuickStartForm(DeskSettings settings)
    {
        SuspendLayout();
        Text = "DeskPortal quick start";
        Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(540, 445);
        var page = DeskStyle.Column(Tone.Canvas, 16);
        page.Dock = DockStyle.Top; page.AutoSize = true; page.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Step(string title, string instruction)
        {
            DeskStyle.Row(page, DeskStyle.Heading(title));
            DeskStyle.Row(page, new Label { Text = instruction, AutoSize = true, Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 12) });
        }
        Step("Connect the two PCs", "Open DeskPortal on both PCs on the same network. In Network access on each PC, enter the IPv4 shown on the other PC’s dashboard. Choose Allow peer and approve the Windows firewall prompt.");
        Step("Pair and arrange screens", "Select the other PC under Nearby computers. Compare the six digits and choose Codes match on both PCs. In Edit screen layout, drag the monitors into position and choose Apply layout.");
        string modifier = settings.Crossing switch { EdgeActivation.HoldCtrl => "Hold Ctrl and move", EdgeActivation.HoldShift => "Hold Shift and move", _ => "Move" };
        Step("Work across the edge", $"{modifier} the pointer through a shared edge to use the other PC. Drag a window across by its title bar and drag it back to return it. Copy on one PC and paste on the other, or use Send files.");
        string shortcut = settings.Shortcuts.Recover == Keys.None ? "" : $" You can also press {ActionShortcuts.Format(settings.Shortcuts.Recover)}.";
        Step("Return to this PC", $"Choose Bring windows home to return your windows and reclaim the mouse and keyboard.{shortcut}");
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(page);
        var done = new BlockButton { Text = "Done", DialogResult = DialogResult.OK };
        DeskStyle.Button(done, true); done.Click += (_, _) => Close();
        var footer = DeskStyle.LineOf(done); footer.Dock = DockStyle.Bottom;
        footer.FlowDirection = FlowDirection.RightToLeft; footer.Padding = new Padding(8, 4, 8, 4);
        Controls.Add(scroll); Controls.Add(footer); AcceptButton = CancelButton = done;
        DeskStyle.Apply(this, settings.Appearance); ResumeLayout(true);
    }
}
