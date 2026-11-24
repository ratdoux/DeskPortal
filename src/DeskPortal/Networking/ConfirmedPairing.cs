using System.Buffers.Binary;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace DeskLink;

static class ConfirmedPairing
{
    static readonly SslApplicationProtocol Protocol = new("DeskLink-pair-2");
    public delegate Task<bool> Confirm(string computer, string code, CancellationToken stop);

    // The returned Peer owns this exact TLS connection. No sharing packets are
    // processed during the handshake or while either confirmation is outstanding.
    public static async Task<Peer> Pair(TcpClient socket, bool client, Guid id, string name, NearbyComputer? selected,
        Confirm confirm, CancellationToken stop, Func<CancellationToken, ValueTask<TcpClient>>? acceptLink = null)
    {
        var tls = new SslStream(socket.GetStream(), false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        try
        {
            using var cert = client ? null : Invitation.CreateCertificate();
            if (client)
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                    TargetHost = "DeskLink", AllowTlsResume = false, ApplicationProtocols = [SessionLinks.Protocol, Protocol],
                    RemoteCertificateValidationCallback = (_, presented, _, _) => presented != null
                }, token);
            else
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate = cert, AllowTlsResume = false, ApplicationProtocols = acceptLink == null ? [Protocol] : [SessionLinks.Protocol, Protocol]
                }, token);
            bool split = tls.NegotiatedApplicationProtocol == SessionLinks.Protocol;
            if (!split && tls.NegotiatedApplicationProtocol != Protocol) throw new AuthenticationException("Incompatible DeskPortal version.");
            byte[] certHash = SHA256.HashData((client ? tls.RemoteCertificate! : cert!).GetRawCertData());
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            byte[] identity = [.. id.ToByteArray(), checked((byte)nameBytes.Length), .. nameBytes];
            await tls.WriteAsync(identity, token);
            byte[] header = new byte[17];
            await tls.ReadExactlyAsync(header, token);
            byte[] remoteName = new byte[header[16]];
            await tls.ReadExactlyAsync(remoteName, token);
            string otherName = Encoding.UTF8.GetString(remoteName);
            var otherId = new Guid(header.AsSpan(0, 16));
            if (otherId == Guid.Empty || otherId == id || !NearbyComputers.ValidName(otherName) ||
                (selected != null && (otherId != selected.Id || otherName != selected.Name)))
                throw new AuthenticationException("The nearby computer changed. Select it again.");

            byte[] otherIdentity = [.. header, .. remoteName];
            byte[] context = [.. tls.NegotiatedApplicationProtocol.Protocol.Span, .. certHash, .. (client ? identity : otherIdentity),
                .. (client ? otherIdentity : identity)];
            // Commit before revealing either nonce. Neither endpoint can tune its
            // nonce after learning its peer's to force a particular display code.
            byte[] Commit(bool initiator, byte[] nonce) =>
                SHA256.HashData([.. context, initiator ? (byte)1 : (byte)0, .. nonce]);
            byte[] localNonce = RandomNumberGenerator.GetBytes(32);
            byte[] remoteCommit = new byte[32], remoteNonce = new byte[32];
            await tls.WriteAsync(Commit(client, localNonce), token);
            await tls.ReadExactlyAsync(remoteCommit, token);
            await tls.WriteAsync(localNonce, token);
            await tls.ReadExactlyAsync(remoteNonce, token);
            if (!CryptographicOperations.FixedTimeEquals(remoteCommit, Commit(!client, remoteNonce)))
                throw new AuthenticationException("Pairing verification failed.");
            byte[] comparison = SHA256.HashData([.. context, .. (client ? localNonce : remoteNonce),
                .. (client ? remoteNonce : localNonce)]);
            uint number = BinaryPrimitives.ReadUInt32BigEndian(comparison) % 1_000_000;
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            await Decide(tls, confirm, otherName, number.ToString("D6", CultureInfo.InvariantCulture), token);
            return split ? await SessionLinks.Open(socket, tls, cert, acceptLink, token) : new Peer(socket, tls);
        }
        catch (Exception error)
        {
            tls.Dispose();
            socket.Dispose();
            if (error is OperationCanceledException && !stop.IsCancellationRequested)
                throw new TimeoutException("Pairing timed out. Select the computer to try again.");
            throw;
        }
        finally { deadline.Cancel(); }
    }

    static async Task Decide(SslStream tls, Confirm confirm, string name, string code, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task ReadApproval()
        {
            byte[] reply = new byte[1];
            await tls.ReadExactlyAsync(reply, wait.Token);
            if (reply[0] != 1) throw new AuthenticationException("The other computer declined pairing.");
        }
        var remote = ReadApproval();
        try
        {
            var local = confirm(name, code, wait.Token);
            if (await Task.WhenAny(local, remote) == remote) await remote;
            bool approved = await local.WaitAsync(wait.Token);
            await tls.WriteAsync(new byte[] { approved ? (byte)1 : (byte)0 }, wait.Token);
            if (!approved) throw new AuthenticationException("Pairing declined.");
            await remote;
        }
        finally
        {
            wait.Cancel();
            // Observe a pending read if the local user declined or canceled first.
            try { await remote; } catch { }
        }
    }
}
