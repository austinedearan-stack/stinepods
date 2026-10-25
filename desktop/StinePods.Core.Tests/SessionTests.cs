using StinePods.Core;

namespace StinePods.Core.Tests;

public class SessionTests
{
    sealed class MemKeys : IKeyStore
    {
        public readonly Dictionary<string, byte[]> Keys = [];
        public byte[]? Get(string peerId) => Keys.GetValueOrDefault(peerId);
        public void Put(string peerId, byte[] key) => Keys[peerId] = key;
    }

    /// <summary>A phone and a PC session wired back to back, delivering frames until both go quiet.</summary>
    sealed class Pair
    {
        public readonly Session Phone, Pc;
        public readonly List<LinkEvent> PhoneEvents = [], PcEvents = [];
        readonly FrameReader toPhone = new(), toPc = new();

        public Pair(MemKeys phoneKeys, MemKeys pcKeys)
        {
            Phone = new Session(Role.Phone, "phone-1", "Stine", phoneKeys);
            Pc = new Session(Role.Pc, "pc-1", "AUSTINE", pcKeys);
            Deliver(Phone.Start(), Pc.Start());
        }

        public void Deliver(List<byte[]> fromPhone, List<byte[]> fromPc)
        {
            while (fromPhone.Count + fromPc.Count > 0)
            {
                var nextFromPhone = new List<byte[]>();
                var nextFromPc = new List<byte[]>();
                foreach (var f in fromPhone)
                    foreach (var frame in toPc.Push(f)) { var (o, e) = Pc.Receive(frame); nextFromPc.AddRange(o); PcEvents.AddRange(e); }
                foreach (var f in fromPc)
                    foreach (var frame in toPhone.Push(f)) { var (o, e) = Phone.Receive(frame); nextFromPhone.AddRange(o); PhoneEvents.AddRange(e); }
                (fromPhone, fromPc) = (nextFromPhone, nextFromPc);
            }
        }

        public void ConfirmBoth()
        {
            var (po, pe) = Phone.ConfirmPairing(true);
            PhoneEvents.AddRange(pe);
            var (co, ce) = Pc.ConfirmPairing(true);
            PcEvents.AddRange(ce);
            Deliver(po, co);
        }
    }

    [Fact]
    public void FirstConnectionPairsWithMatchingCodesThenAuthenticates()
    {
        var p = new Pair(new MemKeys(), new MemKeys());
        var phoneCode = Assert.Single(p.PhoneEvents.OfType<PairingCode>());
        var pcCode = Assert.Single(p.PcEvents.OfType<PairingCode>());
        Assert.Equal(phoneCode.Code, pcCode.Code);
        Assert.Equal("AUSTINE", phoneCode.PeerName);
        Assert.False(p.Phone.Ready);

        p.ConfirmBoth();
        Assert.True(p.Phone.Ready && p.Pc.Ready);
        Assert.Single(p.PhoneEvents.OfType<LinkReady>());
        Assert.Single(p.PcEvents.OfType<LinkReady>());

        p.Deliver([p.Phone.Send(new Msg("claim", "AA:BB:CC:DD:EE:FF", 3))], []);
        var got = Assert.Single(p.PcEvents.OfType<Incoming>()).M;
        Assert.Equal(new Msg("claim", "AA:BB:CC:DD:EE:FF", 3), got);
    }

    [Fact]
    public void ReconnectUsesStoredKeysWithoutPairing()
    {
        MemKeys phoneKeys = new(), pcKeys = new();
        new Pair(phoneKeys, pcKeys).ConfirmBoth();
        Assert.Equal(phoneKeys.Keys["pc-1"], pcKeys.Keys["phone-1"]);

        var p = new Pair(phoneKeys, pcKeys);
        Assert.Empty(p.PhoneEvents.OfType<PairingCode>());
        Assert.True(p.Phone.Ready && p.Pc.Ready);
    }

    [Fact]
    public void TamperedAndReplayedFramesAreDropped()
    {
        var p = new Pair(new MemKeys(), new MemKeys());
        p.ConfirmBoth();

        var frame = p.Phone.Send(new Msg("claim", "AA", 1));
        var tampered = (byte[])frame.Clone();
        tampered[^2] ^= 0x01;   // flip a bit in the JSON body
        p.Deliver([tampered], []);
        Assert.Empty(p.PcEvents.OfType<Incoming>());

        p.Deliver([frame], []);
        Assert.Single(p.PcEvents.OfType<Incoming>());
        p.Deliver([frame], []);   // replay
        Assert.Single(p.PcEvents.OfType<Incoming>());
        Assert.Equal(2, p.Pc.DroppedFrames);
    }

    [Fact]
    public void RejectedPairingFailsTheLink()
    {
        var p = new Pair(new MemKeys(), new MemKeys());
        var (o, e) = p.Phone.ConfirmPairing(false);
        Assert.IsType<LinkFailed>(Assert.Single(e));
        p.Deliver(o, []);
        Assert.Single(p.PcEvents.OfType<LinkFailed>());
        Assert.False(p.Pc.Ready);
    }

    [Fact]
    public void ReinstalledPcTriggersRepairing()
    {
        MemKeys phoneKeys = new(), pcKeys = new();
        new Pair(phoneKeys, pcKeys).ConfirmBoth();

        var p = new Pair(phoneKeys, new MemKeys());   // PC lost its keys
        Assert.Single(p.PhoneEvents.OfType<PairingCode>());
        Assert.Single(p.PcEvents.OfType<PairingCode>());
        p.ConfirmBoth();
        Assert.True(p.Phone.Ready && p.Pc.Ready);
    }

    [Fact]
    public void MismatchedKeysTriggerRepairing()
    {
        MemKeys phoneKeys = new(), pcKeys = new();
        new Pair(phoneKeys, pcKeys).ConfirmBoth();
        pcKeys.Keys["phone-1"] = new byte[32];   // stale/different key

        var p = new Pair(phoneKeys, pcKeys);
        Assert.False(p.Pc.Ready);
        Assert.Single(p.PhoneEvents.OfType<PairingCode>());
        p.ConfirmBoth();
        Assert.True(p.Phone.Ready && p.Pc.Ready);
    }

    [Fact]
    public void MessagesRoundTripThroughJson()
    {
        var m = new Msg("config") { Headsets = ["AA", "BB"], Prefer = Prefer.Phone, ReturnOnStop = ReturnOnStop.Return };
        using var doc = System.Text.Json.JsonDocument.Parse(Session.MsgJson(m));
        var back = Session.ParseMsg(doc.RootElement);
        Assert.Equal(Describe.Of(m), Describe.Of(back));
    }
}
