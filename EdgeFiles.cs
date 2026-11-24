using System.Runtime.InteropServices;

namespace DeskLink;

record DropQuery(Guid Id, int X, int Y);
record DropReply(Guid Id, bool Accepted);

// The source Explorer owns its OLE drag until release. Its local drop target always
// reports None, so a cross-computer copy can never remove or move the original files.
sealed class EdgeFiles : IDisposable
{
    sealed class EdgeTarget : Form
    {
        public string[]? Files;
        public bool Crossing;
        public EdgeTarget()
        {
            StartPosition = FormStartPosition.Manual; FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true; Opacity = 0.05; AllowDrop = true;
            void Read(DragEventArgs e)
            {
                if (!Crossing) Files = e.Data?.GetData(DataFormats.FileDrop) as string[];
                e.Effect = DragDropEffects.None;
            }
            DragEnter += (_, e) => Read(e);
            DragOver += (_, e) => e.Effect = DragDropEffects.None;
            DragLeave += (_, _) => { if (!Crossing) Files = null; };
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000080; return p; } }
    }
    readonly EdgeTarget strip = new();
    readonly DeskLayout layout;
    readonly Peer peer;
    readonly Action<string> status;
    readonly SharedClipboard clipboard;
    readonly Dictionary<Guid, string[]> offered = [];
    readonly Dictionary<Guid, string> destinations = [];
    readonly System.Windows.Forms.Timer retire = new() { Interval = 130 };
    string[]? active;
    public bool Recognized => active != null || strip.Files is { Length: > 0 };
    public EdgeFiles(DeskLayout layout, Peer peer, SharedClipboard clipboard, Action<string> status)
    {
        this.layout = layout; this.peer = peer; this.clipboard = clipboard; this.status = status;
        retire.Tick += (_, _) => { retire.Stop(); Reset(); };
    }
    public void Track(Point point, bool pressed)
    {
        if (active != null || retire.Enabled) return;
        var bounds = pressed ? layout.FileStrip(point,60) : Rectangle.Empty;
        if (bounds.IsEmpty)
        { if (strip.Visible) { strip.Hide(); strip.Files = null; } return; }
        strip.Bounds = bounds;
        if (!strip.Visible) strip.Show();
    }
    public void Begin(Point anchor)
    {
        if (active != null || strip.Files is not { Length: > 0 } paths) return;
        active = paths; strip.Crossing = true;
        strip.Bounds = new Rectangle(anchor.X - 8, anchor.Y - 8, 17, 17);
    }
    public bool Route(Point p, int msg, bool outside)
    {
        if (active == null) return false;
        if (msg == Win.LeftUp)
        {
            if (outside && offered.Count < 4)
            {
                var id = Guid.NewGuid(); offered.Add(id, active);
                var q = layout.ToPeer(p);
                peer.Send(Kind.FileDropQuery, new DropQuery(id, q.X, q.Y));
            }
            // Release the original Shell drag over our inert target, even if the
            // physical release occurred on the peer.
            Win.mouse_event(Win.MouseFlag(Win.LeftUp), 0, 0, 0, Win.Tag);
            active = null; retire.Start();
        }
        return true;
    }
    public void Query(DropQuery request)
    {
        var point = layout.FromWire(new(request.X, request.Y));
        var dir = layout.Local.Contains(point) ? ExplorerFolder(point) : null;
        bool accepted = dir != null && destinations.Count < 4;
        if (accepted) destinations[request.Id] = dir!;
        else status("Drop files on the desktop or inside an open File Explorer folder.");
        peer.Send(Kind.FileDropReply, new DropReply(request.Id, accepted));
    }
    public void Reply(DropReply reply)
    {
        if (!offered.Remove(reply.Id, out var paths)) return;
        if (reply.Accepted) clipboard.SendFiles(paths, reply.Id);
        else status("File drop canceled. Drop on the other desktop or in an open Explorer folder.");
    }
    public string? TakeDestination(Guid id) => destinations.Remove(id, out var path) ? path : null;
    static string? ExplorerFolder(Point point)
    {
        nint root = Win.GetAncestor(Win.WindowFromPoint(point), 2);
        var cls = Win.Class(root);
        if (cls is "Progman" or "WorkerW") return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (cls is not ("CabinetWClass" or "ExploreWClass")) return null;
        // Match the Shell window under the pointer. Multiple distinct paths for the
        // same HWND are ambiguous on tabbed Explorer and must not receive a drop.
        object? shell = null, collection = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return null;
            shell = Activator.CreateInstance(type);
            collection = ((dynamic)shell!).Windows();
            dynamic entries = collection;
            string? match = null;
            for (int i = 0; i < (int)entries.Count; i++)
            {
                object? item = null, document = null, folder = null, self = null;
                try
                {
                    item = entries.Item(i);
                    if (item == null || (long)((dynamic)item).HWND != (long)root) continue;
                    document = ((dynamic)item).Document;
                    folder = ((dynamic)document).Folder; self = ((dynamic)folder).Self;
                    string path = (string)((dynamic)self).Path;
                    if (!Directory.Exists(path)) continue;
                    // Several tabs may share the same frame handle. An ambiguous folder must not receive a drop.
                    if (match != null && !string.Equals(match, path, StringComparison.OrdinalIgnoreCase)) return null;
                    match = path;
                }
                finally { Release(self); Release(folder); Release(document); Release(item); }
            }
            return match;
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return null; }
        finally { Release(collection); Release(shell); }
    }
    static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    public void Reset() { active = null; strip.Files = null; strip.Crossing = false; strip.Hide(); }
    public void CancelSharing() { retire.Stop(); Reset(); offered.Clear(); destinations.Clear(); }
    public void Dispose() { retire.Dispose(); strip.Dispose(); }
}
