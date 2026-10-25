using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StinePods.Core;

/// <summary>Stores long-term pairing keys by peer device id.</summary>
public interface IKeyStore
{
    byte[]? Get(string peerId);
    void Put(string peerId, byte[] key);
}

public abstract record LinkEvent;
/// <summary>Show this 6-digit code; call <see cref="Session.ConfirmPairing"/> with the user's answer.</summary>
public sealed record PairingCode(int Code, string PeerName) : LinkEvent;
public sealed record LinkReady(string PeerId, string PeerName) : LinkEvent;
public sealed record Incoming(Msg M) : LinkEvent;
/// <summary>The link must be closed.</summary>
public sealed record LinkFailed(string Reason) : LinkEvent;
/// <summary>Round trip of a <see cref="Session.Ping"/>.</summary>
public sealed record LinkRtt(long Ms) : LinkEvent;

/// <summary>
/// One connection's handshake, pairing and message authentication (protocol/PROTOCOL.md). Pure: hand it each
/// received frame, write out the frames it returns. Not thread-safe.
/// </summary>
public sealed class Session(Role role, string myId, string myName, IKeyStore keys, Func<int, byte[]>? random = null)
{
    public const int MaxBody = 4096;
    const byte Plain = 0, Authed = 1;

    readonly Func<int, byte[]> rng = random ?? RandomNumberGenerator.GetBytes;
    readonly byte myDir = role == Role.Phone ? (byte)'P' : (byte)'C';
    readonly byte peerDir = role == Role.Phone ? (byte)'C' : (byte)'P';

    byte[]? myNonce, peerNonce, connKey;
    string? peerId, peerName;
    LinkCrypto.KeyPair? myPair;
    byte[]? peerPub;
    bool pairing, localOk, peerOk, ready;
    ulong sendSeq, recvSeq;

    public bool Ready => ready;
    public string? PeerId => peerId;
    public int DroppedFrames { get; private set; }

    /// <summary>Frames to send as soon as the transport is up.</summary>
    public List<byte[]> Start()
    {
        myNonce = rng(16);
        return [Frame(Plain, Json(w =>
        {
            w.WriteString("t", "hello");
            w.WriteNumber("v", 1);
            w.WriteString("id", myId);
            w.WriteString("name", myName);
            w.WriteString("nonce", Convert.ToBase64String(myNonce));
        }))];
    }

    public byte[] Send(Msg m)
    {
        if (!ready) throw new InvalidOperationException("link not ready");
        return AuthedFrame(MsgJson(m));
    }

    long pingStarted;

    /// <summary>A ping frame; the matching pong is reported as <see cref="LinkRtt"/>.</summary>
    public byte[] Ping()
    {
        if (!ready) throw new InvalidOperationException("link not ready");
        pingStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        return AuthedFrame(Json(w => w.WriteString("t", "ping")));
    }

    public (List<byte[]> Out, List<LinkEvent> Events) ConfirmPairing(bool yes)
    {
        var o = new List<byte[]>();
        var e = new List<LinkEvent>();
        if (!pairing || localOk) return (o, e);
        o.Add(Frame(Plain, Json(w => w.WriteString("t", yes ? "pair-ok" : "pair-no"))));
        if (!yes) { e.Add(new LinkFailed("pairing rejected here")); return (o, e); }
        localOk = true;
        FinishPairingIfBoth(o, e);
        return (o, e);
    }

    /// <summary>Handle one frame (the bytes after the length prefix).</summary>
    public (List<byte[]> Out, List<LinkEvent> Events) Receive(ReadOnlySpan<byte> frame)
    {
        var o = new List<byte[]>();
        var e = new List<LinkEvent>();
        try
        {
            if (frame.Length < 1) throw new FormatException("empty frame");
            if (frame[0] == Plain) OnPlain(frame[1..], o, e);
            else if (frame[0] == Authed) OnAuthed(frame[1..], o, e);
            else DroppedFrames++;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException or KeyNotFoundException or InvalidOperationException)
        {
            DroppedFrames++;
        }
        return (o, e);
    }

    void OnPlain(ReadOnlySpan<byte> body, List<byte[]> o, List<LinkEvent> e)
    {
        using var doc = JsonDocument.Parse(body.ToArray());
        var j = doc.RootElement;
        switch (j.GetProperty("t").GetString())
        {
            case "hello" when peerId is null:
                peerId = j.GetProperty("id").GetString() ?? throw new FormatException("no id");
                peerName = j.TryGetProperty("name", out var n) ? n.GetString() : null;
                peerNonce = Convert.FromBase64String(j.GetProperty("nonce").GetString()!);
                if (peerNonce.Length != 16) throw new FormatException("bad nonce");
                if (keys.Get(peerId) is { } longKey)
                {
                    connKey = ConnKeyFrom(longKey);
                    if (role == Role.Phone) o.Add(AuthedFrame(Json(w => w.WriteString("t", "ready"))));
                }
                else
                {
                    BeginPairing(o, e);
                }
                break;
            case "pair" when !ready && peerId is not null:
                peerPub = Convert.FromBase64String(j.GetProperty("pub").GetString()!);
                BeginPairing(o, e);
                break;
            case "pair-ok" when pairing:
                peerOk = true;
                FinishPairingIfBoth(o, e);
                break;
            case "pair-no" when pairing:
                e.Add(new LinkFailed("pairing rejected on the other device"));
                break;
            default:
                DroppedFrames++;
                break;
        }
    }

    void BeginPairing(List<byte[]> o, List<LinkEvent> e)
    {
        if (!pairing)
        {
            pairing = true;
            connKey = null;
            myPair = new LinkCrypto.KeyPair();
            var pub = myPair.Public;
            o.Add(Frame(Plain, Json(w => { w.WriteString("t", "pair"); w.WriteString("pub", Convert.ToBase64String(pub)); })));
        }
        if (peerPub is not null && myPair is not null)
        {
            var (phonePub, pcPub) = role == Role.Phone ? (myPair.Public, peerPub) : (peerPub, myPair.Public);
            e.Add(new PairingCode(LinkCrypto.PairCode(myPair.Agree(peerPub), phonePub, pcPub), peerName ?? "?"));
        }
    }

    void FinishPairingIfBoth(List<byte[]> o, List<LinkEvent> e)
    {
        if (!localOk || !peerOk || myPair is null || peerPub is null || peerId is null) return;
        var longKey = LinkCrypto.LongKey(myPair.Agree(peerPub));
        keys.Put(peerId, longKey);
        myPair.Dispose();
        myPair = null;
        pairing = false;
        connKey = ConnKeyFrom(longKey);
        if (role == Role.Phone) o.Add(AuthedFrame(Json(w => w.WriteString("t", "ready"))));
    }

    void OnAuthed(ReadOnlySpan<byte> rest, List<byte[]> o, List<LinkEvent> e)
    {
        if (rest.Length < 8 + LinkCrypto.TagLength) throw new FormatException("short frame");
        var seq = BinaryPrimitives.ReadUInt64BigEndian(rest);
        var tag = rest.Slice(8, LinkCrypto.TagLength);
        var body = rest[(8 + LinkCrypto.TagLength)..];
        if (connKey is null)
        {
            // We have no key for this peer (e.g. it was re-installed); pairing is the only way forward.
            DroppedFrames++;
            return;
        }
        if (seq <= recvSeq || !CryptographicOperations.FixedTimeEquals(tag, LinkCrypto.Tag(connKey, peerDir, seq, body)))
        {
            DroppedFrames++;
            // During the handshake a bad tag means the stored keys don't match: re-pair (the user must confirm a code).
            if (!ready && !pairing) BeginPairing(o, e);
            return;
        }
        recvSeq = seq;

        using var doc = JsonDocument.Parse(body.ToArray());
        var j = doc.RootElement;
        var t = j.GetProperty("t").GetString();
        if (!ready)
        {
            if (t != "ready") { DroppedFrames++; return; }
            ready = true;
            if (role == Role.Pc) o.Add(AuthedFrame(Json(w => w.WriteString("t", "ready"))));
            e.Add(new LinkReady(peerId!, peerName ?? "?"));
            return;
        }
        switch (t)
        {
            case "ping": o.Add(AuthedFrame(Json(w => w.WriteString("t", "pong")))); break;
            case "pong" when pingStarted != 0:
                e.Add(new LinkRtt((long)System.Diagnostics.Stopwatch.GetElapsedTime(pingStarted).TotalMilliseconds));
                pingStarted = 0;
                break;
            case "pong" or "ready": break;
            default: e.Add(new Incoming(ParseMsg(j))); break;
        }
    }

    byte[] ConnKeyFrom(byte[] longKey)
    {
        var (phoneNonce, pcNonce) = role == Role.Phone ? (myNonce!, peerNonce!) : (peerNonce!, myNonce!);
        return LinkCrypto.ConnKey(longKey, phoneNonce, pcNonce);
    }

    byte[] AuthedFrame(byte[] body)
    {
        var seq = ++sendSeq;
        var tag = LinkCrypto.Tag(connKey!, myDir, seq, body);
        var payload = new byte[8 + tag.Length + body.Length];
        BinaryPrimitives.WriteUInt64BigEndian(payload, seq);
        tag.CopyTo(payload, 8);
        body.CopyTo(payload, 8 + tag.Length);
        return Frame(Authed, payload);
    }

    /// <summary>Length-prefixed frame: u16 length, kind, payload.</summary>
    static byte[] Frame(byte kind, byte[] payload)
    {
        if (payload.Length + 1 > MaxBody) throw new InvalidOperationException("frame too large");
        var f = new byte[2 + 1 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(f, (ushort)(1 + payload.Length));
        f[2] = kind;
        payload.CopyTo(f, 3);
        return f;
    }

    static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    public static byte[] MsgJson(Msg m) => Json(w =>
    {
        w.WriteString("t", m.T);
        if (m.H is not null) w.WriteString("h", m.H);
        if (m.Id is { } id) w.WriteNumber("id", id);
        if (m.Why is not null) w.WriteString("why", m.Why);
        if (m.Here is { } here) w.WriteBoolean("here", here);
        if (m.Text is not null) w.WriteString("text", m.Text);
        if (m.Headsets is { } hs)
        {
            w.WriteStartArray("headsets");
            foreach (var h in hs) w.WriteStringValue(h);
            w.WriteEndArray();
        }
        if (m.Prefer is { } p) w.WriteString("prefer", Describe.Name<Prefer>(p));
        if (m.ReturnOnStop is { } r) w.WriteString("returnOnStop", Describe.Name<ReturnOnStop>(r));
    });

    public static Msg ParseMsg(JsonElement j)
    {
        string? Str(string k) => j.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new Msg(
            j.GetProperty("t").GetString() ?? throw new FormatException("no type"),
            Str("h"),
            j.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt32() : null,
            Str("why"))
        {
            Here = j.TryGetProperty("here", out var hr) && hr.ValueKind is JsonValueKind.True or JsonValueKind.False ? hr.GetBoolean() : null,
            Text = Str("text"),
            Headsets = j.TryGetProperty("headsets", out var hs) && hs.ValueKind == JsonValueKind.Array
                ? [.. hs.EnumerateArray().Select(x => x.GetString() ?? "")] : null,
            Prefer = Str("prefer") is { } p && Enum.TryParse<Prefer>(p, true, out var pv) ? pv : null,
            ReturnOnStop = Str("returnOnStop") is { } r && Enum.TryParse<ReturnOnStop>(r, true, out var rv) ? rv : null,
        };
    }
}

/// <summary>Splits a byte stream into frames (the bytes after each u16 length prefix).</summary>
public sealed class FrameReader
{
    byte[] buf = new byte[2 + Session.MaxBody];
    int len;

    /// <summary>Appends received bytes; returns complete frames. Throws on an oversized frame (close the link).</summary>
    public List<byte[]> Push(ReadOnlySpan<byte> data)
    {
        var frames = new List<byte[]>();
        while (!data.IsEmpty)
        {
            var n = Math.Min(data.Length, buf.Length - len);
            data[..n].CopyTo(buf.AsSpan(len));
            len += n;
            data = data[n..];
            while (len >= 2)
            {
                var size = BinaryPrimitives.ReadUInt16BigEndian(buf);
                if (size == 0 || size > Session.MaxBody) throw new InvalidDataException("bad frame length");
                if (len < 2 + size) break;
                frames.Add(buf[2..(2 + size)]);
                Buffer.BlockCopy(buf, 2 + size, buf, 0, len - 2 - size);
                len -= 2 + size;
            }
        }
        return frames;
    }
}
