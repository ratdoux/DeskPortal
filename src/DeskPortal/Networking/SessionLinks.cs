using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DeskLink;

sealed record SessionLink(TcpClient Socket, SslStream Stream) : IDisposable
{
    public void Dispose() { Socket.Dispose(); Stream.Dispose(); }
}

static class SessionLinks
{
    internal static readonly SslApplicationProtocol Protocol = new("DeskLink-pair-3");
    static readonly SslApplicationProtocol DataProtocol = new("DeskLink-data-1");

    // Both connections use the original dial direction and listening port, including
    // reverse connections. The paired control channel assigns a one-use ticket to each.
    public static async Task<Peer> Open(TcpClient primary, SslStream control, X509Certificate2? certificate,
        Func<CancellationToken, ValueTask<TcpClient>>? accept, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        var endpoint = (IPEndPoint)primary.Client.RemoteEndPoint!;
        if (endpoint.Address.IsIPv4MappedToIPv6) endpoint = new(endpoint.Address.MapToIPv4(), endpoint.Port);
        async Task<SessionLink> Attach()
        {
            byte[] ticket = new byte[32];
            TcpClient socket;
            if (certificate == null)
            {
                await control.ReadExactlyAsync(ticket, token).ConfigureAwait(false);
                socket = new TcpClient(endpoint.AddressFamily);
                // Keep all channels on the interface chosen for the confirmed connection.
                var local = ((IPEndPoint)primary.Client.LocalEndPoint!).Address;
                socket.Client.Bind(new IPEndPoint(local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local, 0));
            }
            else
            {
                RandomNumberGenerator.Fill(ticket);
                await control.WriteAsync(ticket, token).ConfigureAwait(false);
                socket = await accept!(token).ConfigureAwait(false);
            }
            SslStream? tls = null;
            try
            {
                socket.NoDelay = true;
                if (certificate == null)
                {
                    await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
                    byte[] pin = SHA256.HashData(control.RemoteCertificate!.GetRawCertData());
                    tls = new SslStream(socket.GetStream());
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                        TargetHost = "DeskLink", AllowTlsResume = false, ApplicationProtocols = [DataProtocol],
                        RemoteCertificateValidationCallback = (_, cert, _, _) => cert != null &&
                            CryptographicOperations.FixedTimeEquals(pin, SHA256.HashData(cert.GetRawCertData()))
                    }, token).ConfigureAwait(false);
                    await tls.WriteAsync(ticket, token).ConfigureAwait(false);
                    byte[] reply = new byte[1];
                    await tls.ReadExactlyAsync(reply, token).ConfigureAwait(false);
                    if (reply[0] != 1) throw new AuthenticationException("Session link declined.");
                }
                else
                {
                    tls = new SslStream(socket.GetStream());
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                        ServerCertificate = certificate, AllowTlsResume = false, ApplicationProtocols = [DataProtocol]
                    }, token).ConfigureAwait(false);
                    byte[] proof = new byte[ticket.Length];
                    await tls.ReadExactlyAsync(proof, token).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(ticket, proof))
                        throw new AuthenticationException("Invalid session link.");
                    await tls.WriteAsync(new byte[] { 1 }, token).ConfigureAwait(false);
                }
                return new(socket, tls);
            }
            catch { socket.Dispose(); tls?.Dispose(); throw; }
        }
        var video = await Attach().ConfigureAwait(false);
        SessionLink? transfer = null;
        try
        {
            transfer = await Attach().ConfigureAwait(false);
            return new Peer(primary, control, video, transfer);
        }
        catch { video.Dispose(); transfer?.Dispose(); throw; }
    }
}
