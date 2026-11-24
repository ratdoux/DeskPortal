namespace DeskLink;

// Encoders produce dependent frames. Only the capture worker for a window at
// its credit limit waits. Other windows can continue using the video connection.
sealed class WindowFrameFlow
{
    const int Limit = 2;
    sealed class Pending
    {
        public int Count;
        public TaskCompletionSource Space = Signal();
        // When each unacknowledged frame was handed to the video connection, oldest first.
        public readonly List<long> Sent = new(Limit);
    }
    readonly Dictionary<Guid, Pending> sending = [];
    readonly Dictionary<Guid, (Task Tail, int Count)> decoding = [];
    bool closed;
    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long Reserve(Guid id, CancellationToken token, bool take = true)
    {
        long started = 0;
        while (true)
        {
            Task wait;
            lock (sending)
            {
                if (closed) throw new OperationCanceledException();
                if (!sending.TryGetValue(id, out var window) || window.Count < Limit)
                {
                    if (take)
                    {
                        if (window == null) sending.Add(id, window = new());
                        window.Count++;
                        window.Sent.Add(System.Diagnostics.Stopwatch.GetTimestamp());
                    }
                    return started == 0 ? 0 : System.Diagnostics.Stopwatch.GetTimestamp() - started;
                }
                wait = window.Space.Task;
            }
            if (started == 0) started = System.Diagnostics.Stopwatch.GetTimestamp();
            wait.WaitAsync(token).GetAwaiter().GetResult();
        }
    }
    // Returns how long the acknowledged frame was outstanding: its trip to the peer, the
    // decode and display there, and the acknowledgement's trip back. A frame that was never
    // sent gives up the newest reservation instead.
    public long Release(Guid id, bool unsent = false)
    {
        lock (sending)
        {
            if (!sending.TryGetValue(id, out var window)) return 0;
            long outstanding = 0;
            if (window.Sent.Count > 0)
            {
                int index = unsent ? window.Sent.Count - 1 : 0;
                if (!unsent) outstanding = System.Diagnostics.Stopwatch.GetTimestamp() - window.Sent[index];
                window.Sent.RemoveAt(index);
            }
            window.Space.TrySetResult();
            if (--window.Count == 0) sending.Remove(id);
            else window.Space = Signal();
            return outstanding;
        }
    }
    public void Close()
    {
        lock (sending)
        {
            closed = true;
            foreach (var window in sending.Values) window.Space.TrySetCanceled();
            sending.Clear();
        }
    }

    public void Dispatch(Packet packet, Func<Packet, Task> receive, Action<Guid> consumed,
        TaskCompletionSource<Exception> failure, CancellationToken token)
    {
        var id = packet.Id;
        var finished = Signal();
        Task? previous;
        lock (decoding)
        {
            bool exists = decoding.TryGetValue(id, out var queue);
            if (queue.Count >= Limit || (!exists && decoding.Count >= 16))
                throw new InvalidDataException("Incoming video exceeded its window limit.");
            previous = queue.Tail;
            decoding[id] = (finished.Task, queue.Count + 1);
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if (previous != null) await previous.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await receive(packet).WaitAsync(token).ConfigureAwait(false);
                lock (decoding)
                {
                    var queue = decoding[id];
                    if (queue.Count == 1) decoding.Remove(id);
                    else decoding[id] = (queue.Tail, queue.Count - 1);
                }
                consumed(id);
            }
            catch (Exception ex) { failure.TrySetResult(ex); }
            finally { finished.TrySetResult(); }
        });
    }
}
