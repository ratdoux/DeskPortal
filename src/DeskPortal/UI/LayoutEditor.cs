namespace DeskLink;

sealed class LayoutEditor : Form
{
    readonly TileBoard board;
    public ScreenPlan Selection => board.Selection;
    public LayoutEditor(DeskLayout layout, string peer, DeskAppearance appearance)
    {
        SuspendLayout();
        Text = "DeskPortal screen layout"; Font = new Font("Segoe UI",10);
        AutoScaleDimensions = new(96,96); AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent; MinimumSize = new(580,420); ClientSize = new(750,505);
        var body = DeskStyle.Column(Tone.Canvas,16);
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        DeskStyle.Row(body,DeskStyle.Heading("Screen layout",24));
        DeskStyle.Row(body,DeskStyle.Caption("Drag screens together at their edges. Changes apply to both computers."));
        board = new TileBoard(layout,peer);
        var frame = DeskStyle.Column(Tone.Frame,8);
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        DeskStyle.Row(frame,board,true); DeskStyle.Row(body,frame,true);
        ComboBox Choose(string label, int width) => new() { AccessibleName = label, DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat, Width = width };
        var selected = Choose("Screen to move",155); var target = Choose("Reference screen",155); var side = Choose("Placement",95);
        foreach (string name in board.Names) { selected.Items.Add(name); target.Items.Add(name); }
        side.Items.AddRange(["left of","right of","above","below"]); side.SelectedIndex = 1;
        var place = new BlockButton { Text = "Place" }; DeskStyle.Button(place);
        var controls = DeskStyle.LineOf(selected,side,target,place); controls.Padding = new Padding(0,6,0,2);
        DeskStyle.Row(body,controls);
        var detail = DeskStyle.Caption(""); detail.MaximumSize = new Size(690,0); DeskStyle.Row(body,detail);
        var apply = new BlockButton { Text = "Apply layout", DialogResult = DialogResult.OK };
        var reset = new BlockButton { Text = "Reset layout" };
        var cancel = new BlockButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        DeskStyle.Button(apply,true); DeskStyle.Button(cancel); DeskStyle.Button(reset);
        var footer = DeskStyle.LineOf(apply,cancel,reset); footer.FlowDirection = FlowDirection.RightToLeft;
        DeskStyle.Row(body,footer);
        void RefreshSelection()
        {
            selected.SelectedIndex = board.Selected;
            if (target.SelectedIndex < 0 || target.SelectedIndex == board.Selected)
                target.SelectedIndex = board.Selected == 0 ? 1 : 0;
            var error = layout.Validate(board.Selection,out _);
            detail.Text = error ?? board.Description;
            apply.Enabled = error == null;
            place.Enabled = selected.SelectedIndex != target.SelectedIndex;
        }
        board.Changed += RefreshSelection;
        selected.SelectedIndexChanged += (_,_) => board.SelectTile(selected.SelectedIndex);
        target.SelectedIndexChanged += (_,_) => place.Enabled = selected.SelectedIndex != target.SelectedIndex;
        place.Click += (_,_) => board.Place(target.SelectedIndex,side.SelectedIndex);
        reset.Click += (_,_) => board.Reset();
        Controls.Add(body); CancelButton = cancel; AcceptButton = apply;
        RefreshSelection(); DeskStyle.Apply(this,appearance); ResumeLayout(true);
    }
}

sealed class TileBoard : Control
{
    readonly DeskLayout layout;
    readonly Rectangle[] boxes;
    readonly string[] names;
    public IReadOnlyList<string> Names => names;
    public int Selected { get; private set; }
    public event Action? Changed;
    public ScreenPlan Selection
    {
        get
        {
            TilePosition[] Positions(int start,int count) => Enumerable.Range(start,count)
                .Select(i => new TilePosition(layout.Tiles[i].Display.Id,boxes[i].X,boxes[i].Y)).ToArray();
            return new(Positions(0,layout.Here.Length),Positions(layout.Here.Length,layout.There.Length));
        }
    }
    public string Description => $"{names[Selected]}  ·  {boxes[Selected].Width} × {boxes[Selected].Height} px";
    RectangleF viewport;
    Point press, initial;
    bool dragging;
    public TileBoard(DeskLayout layout,string peer)
    {
        this.layout = layout; boxes = layout.Tiles.Select(t => t.Desk).ToArray();
        names = layout.Tiles.Select((t,i) => $"{(t.Here ? Environment.MachineName : peer)} · {ScreenNumber(t,i)}").ToArray();
        Dock = DockStyle.Fill; DoubleBuffered = true; ResizeRedraw = true; TabStop = true;
        AccessibleName = "Screen positions"; AccessibleRole = AccessibleRole.Diagram; Tag = Tone.Surface;
        SetStyle(ControlStyles.Selectable,true);
    }
    int ScreenNumber(DisplayTile t,int index)
    {
        string digits = new(t.Display.Id.Reverse().TakeWhile(char.IsAsciiDigit).Reverse().ToArray());
        return int.TryParse(digits,out int n) ? n : 1 + (t.Here ? index : index-layout.Here.Length);
    }
    public void SelectTile(int index)
    {
        if (index < 0 || index >= boxes.Length || Selected == index) return;
        Selected = index; Notify();
    }
    void Notify() { AccessibleDescription = Description; Invalidate(); Changed?.Invoke(); }
    public void Reset()
    {
        var plan = DeskLayout.Default(layout.Here,layout.There,layout.Right);
        layout.Validate(plan,out var tiles);
        for (int i=0;i<tiles.Length;i++) boxes[i] = tiles[i].Desk;
        Notify();
    }
    public void Place(int target,int side)
    {
        if (target < 0 || target == Selected) return;
        var a = boxes[Selected]; var b = boxes[target];
        a.Location = side switch { 0 => new(b.Left-a.Width,b.Top), 1 => new(b.Right,b.Top),
            2 => new(b.Left,b.Top-a.Height), _ => new(b.Left,b.Bottom) };
        // Open a slot along the selected edge when inserting the laptop between two existing screens.
        if (boxes.Where((_,i) => i != Selected && i != target).Any(r => r.IntersectsWith(a)))
            for (int i=0; i<boxes.Length; i++)
            {
                if (i == Selected || i == target) continue;
                var r = boxes[i];
                if (side <= 1 && r.Top < a.Bottom && r.Bottom > a.Top)
                {
                    if (side == 0 && r.Right <= b.Left) boxes[i].Offset(-a.Width,0);
                    if (side == 1 && r.Left >= b.Right) boxes[i].Offset(a.Width,0);
                }
                if (side >= 2 && r.Left < a.Right && r.Right > a.Left)
                {
                    if (side == 2 && r.Bottom <= b.Top) boxes[i].Offset(0,-a.Height);
                    if (side == 3 && r.Top >= b.Bottom) boxes[i].Offset(0,a.Height);
                }
            }
        boxes[Selected] = a; Notify();
    }
    RectangleF View()
    {
        var union = boxes.Aggregate(Rectangle.Union);
        float pad = LogicalToDeviceUnits(32);
        float scale = Math.Max(.0001f,Math.Min((Width-2*pad)/union.Width,(Height-2*pad)/union.Height));
        return new((Width-scale*union.Width)/2-union.X*scale,(Height-scale*union.Height)/2-union.Y*scale,scale,scale);
    }
    public override Size GetPreferredSize(Size proposedSize) => new(LogicalToDeviceUnits(160),LogicalToDeviceUnits(90));
    Rectangle Project(Rectangle box)
    {
        var v = dragging ? viewport : View();
        return Rectangle.Round(new RectangleF(v.X+box.X*v.Width,v.Y+box.Y*v.Width,box.Width*v.Width,box.Height*v.Width));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var p = DeskStyle.PaletteFor(this);
        for (int i=0; i<boxes.Length; i++)
        {
            var box = Project(boxes[i]); var fill = layout.Tiles[i].Here ? p.Primary : p.Complement;
            var face = BlockFace.Draw(e.Graphics,box,fill,p.Ink,LogicalToDeviceUnits(i == Selected ? 3 : 1),0);
            if (face.Width < 5 || face.Height < 5) continue;
            if (i == Selected) ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(face,-4,-4),p.OnAccent(fill),fill);
            TextRenderer.DrawText(e.Graphics,names[i],Font,face,p.OnAccent(fill),TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return;
        for (int i=boxes.Length-1;i>=0;i--)
            if (Project(boxes[i]).Contains(e.Location))
            {
                Focus(); SelectTile(i); viewport = View(); press = e.Location; initial = boxes[i].Location;
                dragging = true; Capture = true; Cursor = Cursors.SizeAll; return;
            }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if (!dragging) return;
        var box = boxes[Selected];
        box.Location = new(initial.X+(int)Math.Round((e.X-press.X)/viewport.Width),initial.Y+(int)Math.Round((e.Y-press.Y)/viewport.Width));
        int tolerance = Math.Max(1,(int)(LogicalToDeviceUnits(10)/viewport.Width));
        int dx=0,dy=0,bestX=tolerance+1,bestY=tolerance+1;
        void Snap(int delta,ref int best,ref int offset)
        { if (Math.Abs(delta) < best) { best = Math.Abs(delta); offset = delta; } }
        for (int i=0;i<boxes.Length;i++)
        {
            if (i == Selected) continue;
            var other = boxes[i];
            if (box.Top <= other.Bottom+tolerance && box.Bottom >= other.Top-tolerance)
            {
                Snap(other.Right-box.Left,ref bestX,ref dx); Snap(other.Left-box.Right,ref bestX,ref dx);
                Snap(other.Left-box.Left,ref bestX,ref dx); Snap(other.Right-box.Right,ref bestX,ref dx);
            }
            if (box.Left <= other.Right+tolerance && box.Right >= other.Left-tolerance)
            {
                Snap(other.Bottom-box.Top,ref bestY,ref dy); Snap(other.Top-box.Bottom,ref bestY,ref dy);
                Snap(other.Top-box.Top,ref bestY,ref dy); Snap(other.Bottom-box.Bottom,ref bestY,ref dy);
            }
        }
        box.Offset(dx,dy); boxes[Selected] = box; Notify();
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (dragging) Capture = false; }
    protected override void OnMouseCaptureChanged(EventArgs e)
    { base.OnMouseCaptureChanged(e); dragging = false; Cursor = Cursors.Default; Invalidate(); }
}
