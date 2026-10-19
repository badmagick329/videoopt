using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VideoOptimiser.Infrastructure.Features.Worker;

/// <summary>
/// The SSH host key a new worker is created with. Its public half goes in the worker's known-hosts file before the first
/// connection, so strict host-key checking holds from that connection on and whatever answers at a fresh address first is
/// never trusted in the worker's place (VOPT-152).
/// </summary>
/// <remarks>
/// ECDSA P-256 because .NET generates and exports it in-process, Native AOT included. Ed25519 would need ssh-keygen as a
/// child process writing the private half to a temp file on this PC. OpenSSH has taken ECDSA host keys since 5.7, and a
/// client prefers the algorithm of a key its known-hosts file already holds, so it picks this key over the RSA and Ed25519
/// keys cloud-init still generates beside it. The private half lives only in memory and in the create request's user data: never logged, never
/// written here.
/// </remarks>
internal sealed class WorkerHostKey
{
    private const string Algorithm = "ecdsa-sha2-nistp256";
    private const string CurveName = "nistp256";

    private WorkerHostKey(string privateKeyPem, string publicKey)
    {
        PrivateKeyPem = privateKeyPem;
        PublicKey = publicKey;
    }

    /// <summary>The private half as SEC1 PEM (<c>BEGIN EC PRIVATE KEY</c>), a format sshd reads as a host key.</summary>
    public string PrivateKeyPem { get; }

    /// <summary>The public half as OpenSSH writes it: the algorithm, a space, then the base64 key blob.</summary>
    public string PublicKey { get; }

    public static WorkerHostKey Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new WorkerHostKey(key.ExportECPrivateKeyPem(), PublicKeyOf(key));
    }

    /// <summary>The known-hosts line for the worker at <paramref name="address"/>, the host name ssh is given.</summary>
    public string KnownHostsLine(string address) => $"{address} {PublicKey}";

    /// <summary>The OpenSSH public key of <paramref name="key"/> (RFC 5656: name, curve, uncompressed point).</summary>
    internal static string PublicKeyOf(ECDsa key)
    {
        var point = key.ExportParameters(includePrivateParameters: false).Q;
        var uncompressed = new byte[1 + point.X!.Length + point.Y!.Length];
        uncompressed[0] = 0x04;
        point.X.CopyTo(uncompressed, 1);
        point.Y.CopyTo(uncompressed, 1 + point.X.Length);

        using var blob = new MemoryStream();
        WriteString(blob, Encoding.ASCII.GetBytes(Algorithm));
        WriteString(blob, Encoding.ASCII.GetBytes(CurveName));
        WriteString(blob, uncompressed);
        return $"{Algorithm} {Convert.ToBase64String(blob.ToArray())}";
    }

    // An SSH wire-format string (RFC 4251): a big-endian uint32 length, then the bytes.
    private static void WriteString(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        stream.Write(length);
        stream.Write(value);
    }
}
