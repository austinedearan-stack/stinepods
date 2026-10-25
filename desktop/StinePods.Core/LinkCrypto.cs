using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace StinePods.Core;

/// <summary>Key derivation and message tags for the link (protocol/PROTOCOL.md §Handshake, §Framing).</summary>
public static class LinkCrypto
{
    public const int TagLength = 16;

    /// <summary>6-digit comparison code both devices show during pairing.</summary>
    public static int PairCode(byte[] shared, byte[] phonePub, byte[] pcPub)
    {
        var h = SHA256.HashData([.. "stinepods-pair-v1"u8, .. shared, .. phonePub, .. pcPub]);
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(h) % 1_000_000);
    }

    public static byte[] LongKey(byte[] shared) => HMACSHA256.HashData(shared, "stinepods-key-v1"u8);

    public static byte[] ConnKey(byte[] longKey, byte[] phoneNonce, byte[] pcNonce)
    {
        byte[] data = [.. "stinepods-conn-v1"u8, .. phoneNonce, .. pcNonce];
        return HMACSHA256.HashData(longKey, data);
    }

    /// <summary>HMAC-SHA256(connKey, dir || seq || body) truncated; dir is 'P' (phone→PC) or 'C' (PC→phone).</summary>
    public static byte[] Tag(byte[] connKey, byte dir, ulong seq, ReadOnlySpan<byte> body)
    {
        var data = new byte[1 + 8 + body.Length];
        data[0] = dir;
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(1), seq);
        body.CopyTo(data.AsSpan(9));
        return HMACSHA256.HashData(connKey, data)[..TagLength];
    }

    /// <summary>A P-256 key pair whose public key is exported as an uncompressed point (0x04 || X || Y).</summary>
    public sealed class KeyPair : IDisposable
    {
        readonly ECDiffieHellman ecdh;

        public KeyPair() => ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        /// <summary>Fixed key, for test vectors.</summary>
        public KeyPair(byte[] d, byte[] uncompressedPublic)
        {
            ecdh = ECDiffieHellman.Create();
            ecdh.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = d,
                Q = new ECPoint { X = uncompressedPublic[1..33], Y = uncompressedPublic[33..65] },
            });
        }

        public byte[] Public
        {
            get
            {
                var q = ecdh.ExportParameters(false).Q;
                return [0x04, .. q.X!, .. q.Y!];
            }
        }

        public byte[] PrivateScalar => ecdh.ExportParameters(true).D!;

        /// <summary>Raw ECDH shared secret (the x coordinate, 32 bytes).</summary>
        public byte[] Agree(byte[] peerUncompressedPublic)
        {
            if (peerUncompressedPublic.Length != 65 || peerUncompressedPublic[0] != 0x04) throw new CryptographicException("bad public key");
            using var peer = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = peerUncompressedPublic[1..33], Y = peerUncompressedPublic[33..65] },
            });
            return ecdh.DeriveRawSecretAgreement(peer.PublicKey);
        }

        public void Dispose() => ecdh.Dispose();
    }

    public static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(b);
    public static byte[] Unhex(string s) => Convert.FromHexString(s);
    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
