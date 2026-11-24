using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DeskLink;

sealed class Invitation : IDisposable
{
    public const int Port = 47831;
    readonly X509Certificate2 certificate;
    readonly byte[] secret = RandomNumberGenerator.GetBytes(16);
    readonly TcpListener listener;
    public string Code { get; }
    public int ListeningPort => ((IPEndPoint)listener.LocalEndpoint).Port;

    public Invitation(IPAddress address, int port = Port)
    {
        certificate = CreateCertificate();
        var pin = SHA256.HashData(certificate.RawData)[..16];
        Code = Convert.ToBase64String([.. pin, .. secret]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        listener = new TcpListener(address, port);
        listener.Start(2);
    }

    internal static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=DeskLink", key, HashAlgorithmName.SHA256);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return new X509Certificate2(generated.Export(X509ContentType.Pfx));
    }

    public async Task<Peer> Accept(CancellationToken stop)
    {
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false);
            var tls = new SslStream(client.GetStream());
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate,
                    AllowTlsResume = false, ApplicationProtocols = [SessionLinks.Protocol] }, deadline.Token);
                var proof = new byte[secret.Length];
                await tls.ReadExactlyAsync(proof, deadline.Token);
                if (!CryptographicOperations.FixedTimeEquals(secret, proof)) throw new AuthenticationException();
                await tls.WriteAsync(new byte[] { 1 }, deadline.Token);
                var peer = tls.NegotiatedApplicationProtocol == SessionLinks.Protocol
                    ? await SessionLinks.Open(client, tls, certificate, listener.AcceptTcpClientAsync, deadline.Token)
                    : new Peer(client, tls);
                listener.Stop();
                return peer;
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException)
            {
                tls.Dispose();
                client.Dispose();
                stop.ThrowIfCancellationRequested();
            }
        }
    }

    public static async Task<Peer> Join(string host, string code, CancellationToken stop, int port = Port)
    {
        code = code.Trim().Replace('-', '+').Replace('_', '/');
        if (code.Length != 43) throw new FormatException("Paste the entire invitation code from the other PC.");
        var invite = Convert.FromBase64String(code + "=");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
            var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert != null &&
                CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()).AsSpan(0, 16), invite.AsSpan(0, 16)));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "DeskLink",
                AllowTlsResume = false, ApplicationProtocols = [SessionLinks.Protocol] }, deadline.Token);
            await tls.WriteAsync(invite.AsMemory(16), deadline.Token);
            var ack = new byte[1];
            await tls.ReadExactlyAsync(ack, deadline.Token);
            if (ack[0] != 1) throw new AuthenticationException("Pairing was declined.");
            return tls.NegotiatedApplicationProtocol == SessionLinks.Protocol
                ? await SessionLinks.Open(client, tls, null, null, deadline.Token) : new Peer(client, tls);
        }
        catch { client.Dispose(); throw; }
    }

    public void Dispose() { listener.Stop(); certificate.Dispose(); }
}
