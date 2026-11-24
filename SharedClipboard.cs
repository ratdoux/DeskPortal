using System.Collections.Specialized;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DeskLink;

record Clip(Guid Id, long Clock, string Origin, string Format, string Label, string? Text = null, byte[]? Image = null);
record FileItem(string Name, long Length);
record FileOffer(Clip Clip, FileItem[] Files, Guid? Drop = null, bool FromClipboard = false);
sealed record HistoryItem(Clip Clip, string[]? Paths = null)
{
    public override string ToString() => $"{Clip.Origin}   ·   {Clip.Label}";
}
interface IClipboardAccess
{
    IDataObject? Read();
    void Write(DataObject data);
}
sealed class WindowsClipboard : IClipboardAccess
{
    public IDataObject? Read() => Clipboard.GetDataObject();
    public void Write(DataObject data) => Clipboard.SetDataObject(data, true, 4, 20);
}

sealed class SharedClipboard : IDisposable
{
    const string Marker = "DeskLink.SharedItem";
    const int Limit = 8 * 1024 * 1024;
    readonly Peer peer;
    readonly Action<string> status;
    readonly Action changed;
    readonly string root;
    readonly IClipboardAccess system;
    // File data is written on the transfer connection's reader thread, away from the UI
    // thread and its input hooks. This gate keeps a batch whole between the two.
    readonly object fileGate = new();
    readonly Dictionary<Guid, IncomingFiles> incoming = [];
    sealed class Outgoing(CancellationTokenSource cancel, bool automatic)
    {
        public readonly CancellationTokenSource Cancel = cancel;
        // Started by a copy in Explorer, not by an explicit send.
        public readonly bool Automatic = automatic;
        public volatile bool Started, Refused, Superseded;
    }
    // Copying files usually means pasting them on the same PC. Selections up to this size
    // cross straight away so they can be pasted on the other one. Larger ones wait in history.
    internal long AutoSendLimit { get; set; } = 1L << 30;
    static readonly TimeSpan CopiesKept = TimeSpan.FromHours(24);
    readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Outgoing> outgoing = new();
    readonly SemaphoreSlim fileSender = new(1);
    public List<HistoryItem> History { get; } = [];
    public Func<Guid, string?>? TakeDropDestination { get; set; }
    long clock;
    (long Clock, Guid Id) current;
    volatile bool disposed;
    volatile bool clipboardEnabled = true, filesEnabled = true;
    CancellationTokenSource fileTransfers, clipboardTransfers;
    public SharedClipboard(Peer peer, Action<string> status, Action changed, string? directory = null, IClipboardAccess? system = null)
    {
        this.peer = peer; this.status = status; this.changed = changed; this.system = system ?? new WindowsClipboard();
        fileTransfers = CancellationTokenSource.CreateLinkedTokenSource(peer.Token);
        clipboardTransfers = CancellationTokenSource.CreateLinkedTokenSource(peer.Token);
        root = directory ?? Path.Combine(AppStorage.Root, "Received");
        if (directory == null) { string received = root; _ = Task.Run(() => Sweep(received)); }
    }
    // Files received for a paste are scratch copies. Remove those older than a day, and any
    // unfinished batch a crash left behind. A batch whose folder the user was shown stays.
    internal static void Sweep(string root)
    {
        try
        {
            var stale = new List<string>();
            string copies = Path.Combine(root, IncomingFiles.ClipboardFolder);
            if (Directory.Exists(copies)) stale.AddRange(Directory.EnumerateDirectories(copies));
            if (Directory.Exists(root)) stale.AddRange(Directory.EnumerateDirectories(root, "*.partial"));
            var cutoff = DateTime.UtcNow - CopiesKept;
            foreach (var folder in stale)
            {
                try { if (Directory.GetLastWriteTimeUtc(folder) < cutoff) Directory.Delete(folder, true); }
                catch (Exception ex) when (StorageFailure(ex)) { }
            }
        }
        catch (Exception ex) when (StorageFailure(ex)) { }
    }
    public void SetSharing(Sharing features)
    {
        bool files = features.HasFlag(Sharing.Files), clips = features.HasFlag(Sharing.Clipboard);
        void Cancel(ref CancellationTokenSource transfers)
        {
            transfers.Cancel(); transfers.Dispose();
            transfers = CancellationTokenSource.CreateLinkedTokenSource(peer.Token);
        }
        if (filesEnabled && !files) Cancel(ref fileTransfers);
        if ((filesEnabled && !files) || (clipboardEnabled && !clips)) Cancel(ref clipboardTransfers);
        filesEnabled = files; clipboardEnabled = clips;
        lock (fileGate)
            foreach (var (id, batch) in incoming.ToArray())
                if (!files || (!clips && batch.Offer.FromClipboard)) { incoming.Remove(id); Discard(batch); }
        if (History.RemoveAll(h => !clips || (!files && h.Paths != null)) > 0) changed();
    }
    Clip New(string type, string label, string? text = null, byte[]? image = null) =>
        new(Guid.NewGuid(), ++clock, Environment.MachineName, type, label, text, image);
    static bool Disabled(IDataObject data, string flag)
    {
        if (!data.GetDataPresent(flag, false)) return false;
        return data.GetData(flag, false) switch
        {
            int n => n == 0,
            byte[] b => b.Length < 4 || BitConverter.ToInt32(b) == 0,
            MemoryStream m => m.Length < 4 || BitConverter.ToInt32(m.ToArray()) == 0,
            _ => true
        };
    }
    public void Observe()
    {
        if (disposed || !clipboardEnabled) return;
        var data = system.Read();
        if (data == null || data.GetDataPresent(Marker, false)) return;
        // An earlier copy still crossing is no longer what a paste would produce.
        foreach (var earlier in outgoing.Values)
            if (earlier.Automatic)
            {
                earlier.Superseded = true;
                try { earlier.Cancel.Cancel(); } catch (ObjectDisposedException) { }
            }
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false) ||
            Disabled(data, "CanIncludeInClipboardHistory") || Disabled(data, "CanUploadToCloudClipboard"))
        { current = (++clock, Guid.Empty); return; }
        if (data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) { CopiedFiles(paths); return; }
        Clip? item = null;
        if (data.GetData(DataFormats.UnicodeText) is string text && text.Length > 0 && text.Length <= 1_000_000)
        {
            var label = text.ReplaceLineEndings(" ");
            item = New("text", label[..Math.Min(label.Length, 80)], text);
        }
        else if (data.GetData(DataFormats.Bitmap) is Bitmap bitmap)
        {
            // The clock reserved here keeps this copy in order with anything copied while it compresses.
            current = (++clock, Guid.NewGuid());
            _ = SendImage(bitmap, current.Clock, current.Id);
            return;
        }
        if (item == null) { current = (++clock, Guid.Empty); return; }
        Remember(new(item)); current = (item.Clock, item.Id);
        peer.Send(Kind.Clip, item);
    }
    // Compressing a large screenshot takes hundreds of milliseconds. Doing it on the
    // UI thread would hold up the input hooks and every incoming frame for that long.
    async Task SendImage(Bitmap bitmap, long stamp, Guid id)
    {
        try
        {
            var (png, size) = await Task.Run(() =>
            {
                using (bitmap)
                using (var bytes = new MemoryStream())
                {
                    bitmap.Save(bytes, ImageFormat.Png);
                    return (bytes.Length <= Limit ? bytes.ToArray() : null, bitmap.Size);
                }
            });
            if (png == null || disposed || !clipboardEnabled) return;
            var item = new Clip(id, stamp, Environment.MachineName, "image", $"Image {size.Width} × {size.Height}", Image: png);
            Remember(new(item));
            peer.Send(Kind.Clip, item);
        }
        catch (ExternalException ex) { if (!disposed) status($"Could not share the copied image. {ex.Message}"); }
    }
    void CopiedFiles(string[] paths)
    {
        long total = 0;
        try { foreach (var path in paths) if (File.Exists(path)) total += new FileInfo(path).Length; }
        catch (Exception ex) when (StorageFailure(ex)) { total = 0; }
        if (total <= AutoSendLimit) { SendFiles(paths, fromClipboard: true, automatic: true); return; }
        if (!filesEnabled) return;
        string label = paths.Length == 1 ? Path.GetFileName(paths[0]) : $"{paths.Length} files";
        var clip = New("files", label);
        Remember(new(clip, paths)); current = (clip.Clock, clip.Id);
        status($"{label} ({total / 1073741824d:F1} GiB) is too large to copy across automatically. Double-click it in the clipboard history to send it.");
    }
    public async void SendFiles(string[] paths, Guid? drop = null, bool fromClipboard = false, bool automatic = false)
    {
        if (disposed || !filesEnabled || (fromClipboard && !clipboardEnabled)) return;
        var feature = (fromClipboard ? clipboardTransfers : fileTransfers).Token;
        Outgoing? sending = null;
        Guid id = Guid.Empty;
        try
        {
            if (paths.Length is < 1 or > 128 || paths.Any(p => !File.Exists(p))) throw new IOException("Select up to 128 individual files.");
            var items = paths.Select(p => new FileItem(Path.GetFileName(p), new FileInfo(p).Length)).ToArray();
            var clip = New("files", items.Length == 1 ? items[0].Name : $"{items.Length} files");
            var offer = new FileOffer(clip, items, drop, fromClipboard);
            IncomingFiles.Validate(offer);
            if (clipboardEnabled) { Remember(new(clip, paths)); if (drop == null) current = (clip.Clock, clip.Id); }
            id = clip.Id;
            // The receiving PC cancels this token when it cannot store the batch.
            sending = new Outgoing(CancellationTokenSource.CreateLinkedTokenSource(feature), automatic);
            outgoing[id] = sending;
            var transfer = sending.Cancel.Token;
            await fileSender.WaitAsync(transfer);
            try
            {
                sending.Started = true;
                // Read and queue the chunks on a worker. A large batch is many thousands of them.
                await Task.Run(async () =>
                {
                    await peer.Bulk(Packet.Make(Kind.FileStart, offer), transfer).ConfigureAwait(false);
                    byte[] buffer = new byte[64 * 1024];
                    for (int i = 0; i < paths.Length; i++)
                    {
                        using var file = new FileStream(paths[i], FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true);
                        if (file.Length != items[i].Length) throw new IOException("The file changed before transfer.");
                        int n;
                        while ((n = await file.ReadAsync(buffer, transfer).ConfigureAwait(false)) > 0)
                            await peer.Bulk(Packet.Binary(Kind.FileChunk, id, buffer.AsSpan(0, n)), transfer).ConfigureAwait(false);
                    }
                    await peer.Bulk(Packet.Make(Kind.FileEnd, id), transfer).ConfigureAwait(false);
                }, transfer);
            }
            finally { fileSender.Release(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidDataException)
        {
            if (!disposed && !feature.IsCancellationRequested)
            {
                // A receiver that refused the batch has already discarded it.
                if (sending is { Started: true, Refused: false })
                {
                    try { await peer.Bulk(Packet.Make(Kind.FileCancel, id)); } catch (OperationCanceledException) { }
                }
                if (sending?.Refused == true) status("The other PC could not save the files. Check the destination folder and its free space.");
                else if (sending?.Superseded != true) status(ex.Message);
            }
        }
        finally
        {
            if (sending != null) { outgoing.TryRemove(id, out _); sending.Cancel.Dispose(); }
        }
    }
    static bool StorageFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;
    static void Discard(IncomingFiles batch)
    {
        try { batch.Dispose(); }
        catch (Exception ex) when (StorageFailure(ex)) { /* Leave what the file system will not remove. */ }
    }
    // A folder that cannot be written, or a full disk, fails that batch only.
    // FileCancel tells the sending PC to stop.
    void Refuse(Guid id, string label, Exception reason)
    {
        lock (fileGate)
            if (incoming.Remove(id, out var batch)) Discard(batch);
        peer.Send(Kind.FileCancel, id);
        status($"Could not save {label}. {reason.Message}");
    }
    void Remember(HistoryItem item)
    {
        History.RemoveAll(i => i.Clip.Id == item.Clip.Id);
        History.Insert(0, item);
        History.Sort((a, b) => (b.Clip.Clock, b.Clip.Id).CompareTo((a.Clip.Clock, a.Clip.Id)));
        long bytes = 0;
        int keep = 0;
        foreach (var h in History)
        {
            bytes += h.Clip.Image?.Length ?? (h.Clip.Text?.Length ?? 0) * 2L;
            if (keep >= 20 || bytes > 32 * 1024 * 1024) break;
            keep++;
        }
        if (keep < History.Count) History.RemoveRange(keep, History.Count - keep);
        changed();
    }
    public void Receive(Clip clip)
    {
        if (!clipboardEnabled || disposed) return;
        if (clip.Format == "text" && (clip.Text == null || clip.Text.Length > 1_000_000) ||
            clip.Format == "image" && (clip.Image == null || clip.Image.Length > Limit) ||
            clip.Format is not ("text" or "image")) throw new InvalidDataException("Invalid clipboard item.");
        Accept(new(clip));
    }
    void Accept(HistoryItem item)
    {
        clock = Math.Max(clock, item.Clip.Clock);
        Remember(item);
        if ((item.Clip.Clock, item.Clip.Id).CompareTo(current) < 0) return;
        current = (item.Clip.Clock, item.Clip.Id);
        Write(item);
    }
    public void Select(HistoryItem item)
    {
        if (!clipboardEnabled || disposed) return;
        if (item.Paths is { } paths) { if (filesEnabled) { Write(item); SendFiles(paths, fromClipboard: true); } return; }
        var selected = item.Clip with { Clock = ++clock, Id = Guid.NewGuid(), Origin = Environment.MachineName };
        Accept(new(selected)); peer.Send(Kind.Clip, selected);
    }
    void Write(HistoryItem item)
    {
        if (item.Clip.Image is { } png) { _ = WriteImage(item, png); return; }
        try
        {
            var data = new DataObject();
            data.SetData(Marker, false, item.Clip.Id.ToString());
            if (item.Paths != null) { var files = new StringCollection(); files.AddRange(item.Paths); data.SetFileDropList(files); }
            else data.SetText(item.Clip.Text!);
            system.Write(data);
        }
        catch (ExternalException) { status("Clipboard is busy. The item is available in history."); }
    }
    // Decode on a worker. Only placing the finished bitmap on the clipboard needs the UI thread.
    async Task WriteImage(HistoryItem item, byte[] png)
    {
        Bitmap? image = null;
        try
        {
            image = await Task.Run(() =>
            {
                using var bytes = new MemoryStream(png);
                using var decoded = new Bitmap(bytes);
                return new Bitmap(decoded);
            });
            // A newer copy may have arrived while this one was decoding.
            if (disposed || !clipboardEnabled || current.Id != item.Clip.Id) return;
            var data = new DataObject();
            data.SetData(Marker, false, item.Clip.Id.ToString());
            data.SetImage(image);
            system.Write(data);
        }
        catch (ExternalException) { if (!disposed) status("Clipboard is busy. The item is available in history."); }
        catch (ArgumentException) { if (!disposed) status("The copied image could not be read."); }
        finally { image?.Dispose(); }
    }
    // File chunks may arrive on the transfer connection's reader thread. Every other kind arrives on the UI thread.
    public void FilePacket(Packet p)
    {
        if (!filesEnabled || disposed) return;
        if (p.Kind == Kind.FileStart)
        {
            var offer = p.Read<FileOffer>();
            if (offer.FromClipboard && !clipboardEnabled) return;
            lock (fileGate)
            {
                if (incoming.Count >= 2 || incoming.ContainsKey(offer.Clip.Id)) throw new InvalidDataException("Unexpected file offer.");
                var target = offer.Drop is { } drop ? TakeDropDestination?.Invoke(drop) : null;
                if (offer.Drop.HasValue && target == null) return;
                try { incoming.Add(offer.Clip.Id, new IncomingFiles(root, offer, target)); }
                catch (Exception ex) when (StorageFailure(ex)) { Refuse(offer.Clip.Id, offer.Clip.Label, ex); return; }
            }
            clock = Math.Max(clock, offer.Clip.Clock);
            status($"Receiving {offer.Clip.Label}…");
            return;
        }
        Guid id = p.Kind == Kind.FileChunk ? p.Id : p.Read<Guid>();
        if (p.Kind == Kind.FileCancel && outgoing.TryGetValue(id, out var sending))
        {
            sending.Refused = true;
            try { sending.Cancel.Cancel(); } catch (ObjectDisposedException) { }
            return;
        }
        if (p.Kind == Kind.FileChunk)
        {
            lock (fileGate)
            {
                // Chunks already on the wire can arrive after the user cancels a feature.
                if (!incoming.TryGetValue(id, out var open)) return;
                try { open.Append(p.Data.AsSpan(16)); }
                catch (Exception ex) when (StorageFailure(ex)) { Refuse(id, open.Offer.Clip.Label, ex); }
            }
            return;
        }
        HistoryItem? item = null;
        IncomingFiles? batch;
        bool paste;
        lock (fileGate)
        {
            if (!incoming.Remove(id, out batch)) return;
            // A batch that goes onto the clipboard is a scratch copy. One the user is shown the folder of is theirs to keep.
            paste = batch.Offer.Drop == null && clipboardEnabled;
            try { if (p.Kind == Kind.FileEnd) item = new HistoryItem(batch.Offer.Clip, batch.Finish(paste)); }
            catch (Exception ex) when (StorageFailure(ex)) { Refuse(id, batch.Offer.Clip.Label, ex); }
            finally { Discard(batch); }
        }
        if (item == null) return;
        if (paste) { Accept(item); status($"Received {item.Clip.Label}. Ready to paste."); }
        else
        {
            if (clipboardEnabled) Remember(item);
            status($"Copied {item.Clip.Label} to {Path.GetDirectoryName(item.Paths![0])}.");
        }
    }
    public void Dispose()
    {
        disposed = true;
        fileTransfers.Cancel(); clipboardTransfers.Cancel();
        fileTransfers.Dispose(); clipboardTransfers.Dispose();
        lock (fileGate)
        {
            foreach (var batch in incoming.Values) Discard(batch);
            incoming.Clear();
        }
    }
}

sealed class IncomingFiles : IDisposable
{
    public FileOffer Offer { get; }
    readonly string partial, root, folder;
    readonly string? dropFolder;
    readonly List<string> created = [];
    FileStream? file;
    int index;
    long remaining;
    bool committed;
    public static void Validate(FileOffer offer)
    {
        if (offer.Clip.Format != "files" || offer.Files.Length is < 1 or > 128) throw new InvalidDataException("Invalid file list.");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in offer.Files)
        {
            string stem = f.Name.Split('.')[0].TrimEnd().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(f.Name) || f.Name.Length > 200 || f.Name.EndsWith('.') || f.Name.EndsWith(' ') ||
                f.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !names.Add(f.Name) ||
                stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[0-9¹²³]$") ||
                f.Length < 0 || f.Length > 8L * 1024 * 1024 * 1024 - total)
                throw new InvalidDataException("The transfer needs distinct Windows filenames and must fit within 8 GiB.");
            total += f.Length;
        }
    }
    // Batches received for a paste end up here and are swept after a day.
    public const string ClipboardFolder = "Clipboard";
    public IncomingFiles(string root, FileOffer offer, string? dropFolder = null)
    {
        Validate(offer);
        Offer = offer;
        this.root = root;
        this.dropFolder = dropFolder;
        folder = Guid.NewGuid().ToString("N");
        partial = Path.Combine(dropFolder ?? root, folder + ".partial");
        Directory.CreateDirectory(partial);
    }
    void OpenNext()
    {
        while (file == null && index < Offer.Files.Length)
        {
            var entry = Offer.Files[index++];
            var path = Path.Combine(partial, entry.Name);
            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created.Add(path); remaining = entry.Length;
            if (remaining == 0) { file.Dispose(); file = null; }
        }
    }
    public void Append(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            OpenNext();
            if (file == null) throw new InvalidDataException("More file data than offered.");
            int n = (int)Math.Min(remaining, data.Length);
            file.Write(data[..n]); remaining -= n; data = data[n..];
            if (remaining == 0) { file.Dispose(); file = null; }
        }
    }
    public string[] Finish(bool scratch = false)
    {
        OpenNext();
        if (file != null) throw new InvalidDataException("File transfer was incomplete.");
        if (dropFolder != null)
        {
            var placed = new List<string>();
            try
            {
                foreach (var path in created)
                {
                    var name = Path.GetFileName(path);
                    for (int copy = 1; ; copy++)
                    {
                        var candidate = copy == 1 ? name : $"{Path.GetFileNameWithoutExtension(name)} ({copy}){Path.GetExtension(name)}";
                        var dest = Path.Combine(dropFolder, candidate);
                        try { File.Move(path, dest, false); placed.Add(dest); break; }
                        catch (IOException) when (File.Exists(dest) || Directory.Exists(dest)) { }
                    }
                }
                Directory.Delete(partial); committed = true;
                return placed.ToArray();
            }
            catch
            {
                for (int i = 0; i < placed.Count; i++) File.Move(placed[i], created[i]);
                throw;
            }
        }
        string home = scratch ? Path.Combine(root, ClipboardFolder) : root;
        Directory.CreateDirectory(home);
        string final = Path.Combine(home, folder);
        Directory.Move(partial, final); committed = true;
        return Offer.Files.Select(i => Path.Combine(final, i.Name)).ToArray();
    }
    public void Dispose()
    {
        file?.Dispose();
        if (committed) return;
        foreach (var path in created) File.Delete(path);
        if (Directory.Exists(partial)) Directory.Delete(partial);
    }
}
