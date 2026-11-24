using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace DeskLink;

// Monitor rectangles are physical pixels relative to the PC's virtual-desktop origin on the wire.
sealed record DisplayInfo(string Id, Rectangle Bounds, bool Primary = false)
{
    public static DisplayInfo[] Read(Rectangle desktop) => Screen.AllScreens.Select(s => new DisplayInfo(s.DeviceName,
        new Rectangle(s.Bounds.X - desktop.X, s.Bounds.Y - desktop.Y, s.Bounds.Width, s.Bounds.Height), s.Primary)).ToArray();
    public static bool Valid(DisplayInfo[]? screens, Size size)
    {
        if (screens is not { Length: > 0 and <= 64 }) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in screens)
            if (s == null || string.IsNullOrEmpty(s.Id) || s.Id.Length > 128 || !ids.Add(s.Id) ||
                s.Bounds.X < 0 || s.Bounds.Y < 0 || s.Bounds.Width < 1 || s.Bounds.Height < 1 ||
                (long)s.Bounds.X + s.Bounds.Width > size.Width || (long)s.Bounds.Y + s.Bounds.Height > size.Height) return false;
        return true;
    }
}
sealed record TilePosition(string Id, int X, int Y);
sealed record ScreenPlan(TilePosition[] Here, TilePosition[] There)
{
    public ScreenPlan Reverse() => new(There, Here);
    static string PathFor(string peer) => Path.Combine(AppStorage.Root, "layouts",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(peer.ToUpperInvariant()))) + ".json");
    public static ScreenPlan? Load(string peer)
    {
        try { return JsonSerializer.Deserialize<ScreenPlan>(File.ReadAllText(PathFor(peer))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public void Save(string peer)
    {
        string path = PathFor(peer);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(this)); File.Move(temp, path, true); }
        finally { File.Delete(temp); }
    }
}

sealed record DisplayTile(bool Here, DisplayInfo Display, Rectangle Native, Rectangle Desk)
{
    public Point ToDesk(Point native) => new(native.X - Native.X + Desk.X, native.Y - Native.Y + Desk.Y);
    public Point ToNative(Point desk) => new(desk.X - Desk.X + Native.X, desk.Y - Desk.Y + Native.Y);
}

sealed class DeskLayout
{
    public Rectangle Local { get; }
    public Size Other { get; }
    public bool Right { get; }
    public bool Editable { get; }
    public DisplayInfo[] Here { get; }
    public DisplayInfo[] There { get; }
    public DisplayTile[] Tiles { get; private set; } = [];
    public ScreenPlan Plan { get; private set; } = new([], []);
    // Legacy fixtures use one rectangular display per PC and ordinary native coordinates on the local side.
    public Rectangle Remote => Tiles[Here.Length].Desk;
    public DeskLayout(Rectangle local, Size other, bool right) : this(local, other, right,
        [new("local", new Rectangle(Point.Empty, local.Size))], [new("peer", new Rectangle(Point.Empty, other))], false)
    {
        Apply(new([new("local", local.X, local.Y)], [new("peer", right ? local.Right : local.Left - other.Width, local.Top)]));
    }
    public DeskLayout(Rectangle local, Size other, bool right, DisplayInfo[] here, DisplayInfo[] there, bool editable = true)
    {
        Local = local; Other = other; Right = right; Here = here; There = there; Editable = editable;
        Apply(Default(here, there, right));
    }
    public static ScreenPlan Default(DisplayInfo[] first, DisplayInfo[] second, bool right)
    {
        var a = right ? first.MaxBy(s => s.Bounds.Right)! : first.MinBy(s => s.Bounds.Left)!;
        var b = right ? second.MinBy(s => s.Bounds.Left)! : second.MaxBy(s => s.Bounds.Right)!;
        int dx = right ? a.Bounds.Right - b.Bounds.Left : a.Bounds.Left - b.Bounds.Right;
        int dy = a.Bounds.Top - b.Bounds.Top;
        var plan = new ScreenPlan(first.Select(s => new TilePosition(s.Id,s.Bounds.X,s.Bounds.Y)).ToArray(),
            second.Select(s => new TilePosition(s.Id,s.Bounds.X+dx,s.Bounds.Y+dy)).ToArray());
        var boxes = first.Select(s => s.Bounds).Concat(second.Select(s => new Rectangle(s.Bounds.X+dx,s.Bounds.Y+dy,s.Bounds.Width,s.Bounds.Height))).ToArray();
        if (GeometryError(boxes) == null) return plan;
        // Some drivers expose detached islands or corner-only contacts. Give every screen a reachable edge.
        int x = 0;
        TilePosition[] Row(DisplayInfo[] screens) => screens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).Select(s =>
        { var pos = new TilePosition(s.Id,x,0); x += s.Bounds.Width; return pos; }).ToArray();
        if (right) return new(Row(first), Row(second));
        var left = Row(second); return new(Row(first), left);
    }
    static bool Touch(Rectangle a, Rectangle b) =>
        ((a.Right == b.Left || b.Right == a.Left) && a.Top < b.Bottom && b.Top < a.Bottom) ||
        ((a.Bottom == b.Top || b.Bottom == a.Top) && a.Left < b.Right && b.Left < a.Right);
    static string? GeometryError(Rectangle[] boxes)
    {
        for (int i = 0; i < boxes.Length; i++)
            for (int j = 0; j < i; j++)
                if (boxes[i].IntersectsWith(boxes[j])) return "Move overlapping screens apart.";
        var seen = new bool[boxes.Length]; var queue = new Queue<int>(); queue.Enqueue(0); seen[0] = true;
        while (queue.TryDequeue(out int i))
            for (int j = 0; j < boxes.Length; j++)
                if (!seen[j] && Touch(boxes[i], boxes[j])) { seen[j] = true; queue.Enqueue(j); }
        return seen.All(hit => hit) ? null : "Connect every screen along an edge. Corners alone cannot carry the pointer.";
    }
    public string? Validate(ScreenPlan? plan, out DisplayTile[] tiles)
    {
        tiles = [];
        if (plan?.Here?.Length != Here.Length || plan.There?.Length != There.Length) return "The connected displays have changed.";
        var built = new List<DisplayTile>();
        bool Add(DisplayInfo[] screens, TilePosition[] positions, bool here)
        {
            foreach (var s in screens)
            {
                var pos = Array.Find(positions, p => p != null && p.Id == s.Id);
                if (pos == null || Math.Abs((long)pos.X) > 8_000_000 || Math.Abs((long)pos.Y) > 8_000_000) return false;
                var native = s.Bounds;
                if (here) native.Offset(Local.Location);
                built.Add(new(here, s, native, new Rectangle(pos.X,pos.Y,native.Width,native.Height)));
            }
            return true;
        }
        if (!Add(Here, plan.Here, true) || !Add(There, plan.There, false)) return "The layout does not match these displays.";
        tiles = built.ToArray(); return GeometryError(tiles.Select(t => t.Desk).ToArray());
    }
    public void Apply(ScreenPlan plan)
    {
        if (Validate(plan, out var tiles) is { } error) throw new InvalidDataException(error);
        Tiles = tiles; Plan = plan;
    }
    public Point Clamp(Rectangle r, Point p) => new(Math.Clamp(p.X,r.Left,r.Right-1),Math.Clamp(p.Y,r.Top,r.Bottom-1));
    DisplayTile Nearest(Point p, bool here, bool native)
    {
        long best = long.MaxValue; DisplayTile? found = null;
        foreach (var t in Tiles)
        {
            if (t.Here != here) continue;
            var box = native ? t.Native : t.Desk;
            if (box.Contains(p)) return t;
            var q = Clamp(box,p); long dx = (long)p.X-q.X, dy = (long)p.Y-q.Y, distance = dx*dx+dy*dy;
            if (distance < best) { best = distance; found = t; }
        }
        return found!;
    }
    public DisplayTile LocalTile(Point physical) => Nearest(physical,true,true);
    public DisplayTile HomeTile(Point desk) => Nearest(desk,true,false);
    public Point ToWorld(Point physical) => LocalTile(physical).ToDesk(physical);
    public Point LocalPoint(Point desk, Point? anchor = null) => HomeTile(anchor ?? desk).ToNative(desk);
    public Point ToPeer(Point desk, Point? anchor = null) => Nearest(anchor ?? desk,false,false).ToNative(desk);
    public Point FromWire(Point p) => new(p.X + Local.X, p.Y + Local.Y);
    public bool LocalVisible(Rectangle box) => Tiles.Any(t => t.Here && t.Desk.IntersectsWith(box));
    public bool PeerVisible(Rectangle box) => Tiles.Any(t => !t.Here && t.Desk.IntersectsWith(box));
    public bool NativeVisible(Rectangle box) => Tiles.Any(t => t.Here && t.Native.IntersectsWith(box));
    public bool ReachesPeer(Rectangle box, int reach)
    {
        foreach (var t in Tiles)
        {
            if (t.Here) continue;
            var hit = Rectangle.Intersect(box,t.Desk);
            if (hit.Width >= reach && hit.Height >= reach) return true;
        }
        return false;
    }
    public Rectangle FileStrip(Point desk, int proximity)
    {
        var local = HomeTile(desk);
        var a = local.Desk;
        if (!a.Contains(desk)) return Rectangle.Empty;
        foreach (var t in Tiles)
        {
            if (t.Here || !Touch(a,t.Desk)) continue;
            var b = t.Desk; Rectangle strip;
            if (a.Right == b.Left || a.Left == b.Right)
            {
                int y = Math.Max(a.Top,b.Top);
                strip = new(a.Right == b.Left ? a.Right-1 : a.Left,y,1,Math.Min(a.Bottom,b.Bottom)-y);
                if (desk.Y < y || desk.Y >= strip.Bottom || Math.Abs(desk.X-strip.X) > proximity) continue;
                strip.Width = Math.Min(a.Width,12); if (a.Right == b.Left) strip.X = a.Right-strip.Width;
            }
            else
            {
                int x = Math.Max(a.Left,b.Left);
                strip = new(x,a.Bottom == b.Top ? a.Bottom-1 : a.Top,Math.Min(a.Right,b.Right)-x,1);
                if (desk.X < x || desk.X >= strip.Right || Math.Abs(desk.Y-strip.Y) > proximity) continue;
                strip.Height = Math.Min(a.Height,12); if (a.Bottom == b.Top) strip.Y = a.Bottom-strip.Height;
            }
            return new(local.ToNative(strip.Location),strip.Size);
        }
        return Rectangle.Empty;
    }
    public bool AtSeam(Point desk) => !FileStrip(desk,0).IsEmpty;
    // Trace the actual edge crossed. A fast diagonal motion cannot jump a gap or a corner onto another display.
    public (DisplayTile Tile, Point Point) Walk(DisplayTile tile, Point from, Point to)
    {
        for (int i = 0; i < Tiles.Length; i++)
        {
            var r = tile.Desk;
            if (r.Contains(to)) break;
            double dx = to.X-from.X, dy = to.Y-from.Y;
            double tx = to.X >= r.Right ? (r.Right-.5-from.X)/dx : to.X < r.Left ? (r.Left-.5-from.X)/dx : double.PositiveInfinity;
            double ty = to.Y >= r.Bottom ? (r.Bottom-.5-from.Y)/dy : to.Y < r.Top ? (r.Top-.5-from.Y)/dy : double.PositiveInfinity;
            var edge = Clamp(r,new Point((int)Math.Round(from.X+dx*Math.Min(tx,ty)),(int)Math.Round(from.Y+dy*Math.Min(tx,ty))));
            Point entry = tx <= ty ? new(dx > 0 ? r.Right : r.Left-1,edge.Y) : new(edge.X,dy > 0 ? r.Bottom : r.Top-1);
            DisplayTile? next = null;
            foreach (var t in Tiles) if (t.Desk.Contains(entry)) { next = t; break; }
            if (next == null) return (tile,Clamp(r,to));
            tile = next; from = entry;
        }
        return (tile,Clamp(tile.Desk,to));
    }
}
