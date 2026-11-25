namespace DeskLink;

sealed class PairingDialog : Form
{
    readonly TaskCompletionSource<bool> decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PairingDialog(string computer, string code)
    {
        SuspendLayout();
        Text = "Confirm DeskPortal connection";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var panel = new TableLayoutPanel { AutoSize = true, Padding = new Padding(18), ColumnCount = 1 };
        panel.Controls.Add(new Label { Text = $"Compare this code with the one on {computer}.", AutoSize = true, MaximumSize = new Size(380, 0) });
        panel.Controls.Add(new Label { Text = $"Window sharing lets {computer} project your monitors. Mouse and keyboard sharing also lets it control them.", AutoSize = true, MaximumSize = new Size(380, 0) });
        panel.Controls.Add(new Label { Text = code.Insert(3, " "), Font = new Font("Consolas", 28, FontStyle.Bold),
            Tag = Tone.Primary, Padding = new Padding(10, 3, 10, 3), AutoSize = true, Margin = new Padding(0, 8, 0, 12), AccessibleName = $"Confirmation code {code}" });
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var approve = new BlockButton { Text = "Codes match" };
        var decline = new BlockButton { Text = "Decline" };
        DeskStyle.Button(approve, true); DeskStyle.Button(decline);
        approve.Click += (_, _) => { decision.TrySetResult(true); Close(); };
        decline.Click += (_, _) => Close();
        CancelButton = decline;
        actions.Controls.AddRange([approve, decline]);
        panel.Controls.Add(actions);
        Controls.Add(panel);
        ActiveControl = decline;
        FormClosed += (_, _) => decision.TrySetResult(false);
        ResumeLayout(true);
    }

    public async Task<bool> Ask(Form owner, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        DeskStyle.ApplyPalette(this, DeskStyle.PaletteFor(owner));
        Show(owner);
        Activate();
        try { return await decision.Task.WaitAsync(stop); }
        finally { if (!IsDisposed) Close(); }
    }
}
