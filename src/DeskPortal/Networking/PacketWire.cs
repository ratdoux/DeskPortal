using System.Buffers.Binary;
using System.Net.Security;

namespace DeskLink;

// One reader and one writer per TLS connection. Small control messages fit in a single TLS write.
sealed class PacketWire(SslStream stream, TrafficCounter sent, TrafficCounter received)
{
    const int MaxPayload = 16 * 1024 * 1024;
    readonly byte[] header = new byte[5], buffer = new byte[4096];
    public async ValueTask<Packet> Read(CancellationToken token)
    {
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var (kind, data) = DecodeHeader();
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
        return FinishRead(kind, data, began);
    }
    public Packet ReadBlocking()
    {
        stream.ReadExactly(header);
        var (kind, data) = DecodeHeader();
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        stream.ReadExactly(data);
        return FinishRead(kind, data, began);
    }
    (Kind, byte[]) DecodeHeader()
    {
        var kind = (Kind)header[0];
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (!Enum.IsDefined(kind) || length < 0 || length > MaxPayload)
            throw new InvalidDataException("Invalid peer message.");
        return (kind, new byte[length]);
    }
    Packet FinishRead(Kind kind, byte[] data, long began)
    {
        long arrived = System.Diagnostics.Stopwatch.GetTimestamp();
        received.Record(kind, header.Length + data.Length);
        return new(kind, data) { ReceivedAt = arrived, ArrivalTicks = arrived - began };
    }
    public async ValueTask Write(Packet packet, CancellationToken token)
    {
        buffer[0] = (byte)packet.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1), packet.Data.Length);
        if (packet.Data.Length <= buffer.Length - 5)
        {
            packet.Data.CopyTo(buffer, 5);
            await stream.WriteAsync(buffer.AsMemory(0, 5 + packet.Data.Length), token).ConfigureAwait(false);
        }
        else
        {
            await stream.WriteAsync(buffer.AsMemory(0, 5), token).ConfigureAwait(false);
            await stream.WriteAsync(packet.Data, token).ConfigureAwait(false);
        }
        sent.Record(packet.Kind, 5 + packet.Data.Length);
    }
}
