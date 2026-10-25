using StinePods.Core;
using Action = StinePods.Core.Action;

namespace StinePods.Core.Tests;

/// <summary>
/// End-to-end simulation: a phone and a PC engine joined by a simulated link (latency, drops), sharing a simulated
/// headset (single-point or dual-connection) and simulated media apps, driven by thousands of random user actions.
/// Checks the properties that matter in front of a user: the last thing you pressed play on ends up with the
/// headphones and playing, nothing is left stuck paused, and devices never ping-pong the headset.
/// </summary>
public class SimulationTests
{
    const string H = "HS";

    sealed class Device(Role role, Sim sim)
    {
        public readonly Role Role = role;
        public Engine Engine = null!;
        public readonly Dictionary<string, bool> Playing = [];   // app → actually playing (user intent + our pauses)
        public readonly Dictionary<string, long> Timers = [];
        public int Releases, Takes;

        public void Handle(Event e)
        {
            foreach (var a in Engine.Handle(e, sim.Now)) sim.Do(this, a);
        }
    }

    sealed class Sim
    {
        readonly Random rng;
        readonly bool dual;
        public long Now;
        public bool LinkUp = true;
        public readonly Device Phone, Pc;
        public readonly HashSet<Device> Connected = [];          // devices the headset is connected to
        readonly List<(long At, System.Action Run)> queue = [];
        public readonly List<string> Trace = [];
        public int AskPrompts;

        public Sim(int seed, bool dual, string? phoneRule = null, string? pcRule = null)
        {
            rng = new Random(seed);
            this.dual = dual;
            Phone = new Device(Role.Phone, this);
            Pc = new Device(Role.Pc, this);
            Phone.Engine = new Engine(new Settings { Role = Role.Phone, Headsets = [H], Apps = Rules(phoneRule) });
            Pc.Engine = new Engine(new Settings { Role = Role.Pc, Headsets = [H], Apps = Rules(pcRule) });
            Pc.Handle(new PeerLink(true));
            Phone.Handle(new PeerLink(true));
        }

        static Dictionary<string, AppRule> Rules(string? r) => r is null ? [] : new() { ["app"] = Enum.Parse<AppRule>(r, true) };

        Device Other(Device d) => d == Phone ? Pc : Phone;

        public void At(long delay, System.Action run) => queue.Add((Now + delay, run));

        // One app's play/pause reports arrive in the order they happened (as on a real device), even with jitter.
        readonly Dictionary<(Device, string), long> lastReport = [];

        void Report(Device d, string app, bool playing, long delay)
        {
            var at = Math.Max(Now + delay, lastReport.GetValueOrDefault((d, app)) + 1);
            lastReport[(d, app)] = at;
            queue.Add((at, () => d.Handle(new Playback(app, playing))));
        }

        public void Do(Device d, Action a)
        {
            Trace.Add($"{Now,7} {d.Role,-5} {Describe.Of(a)}");
            switch (a)
            {
                case Pause p:
                    if (d.Playing.GetValueOrDefault(p.App)) { d.Playing[p.App] = false; Report(d, p.App, false, rng.Next(20, 400)); }
                    break;
                case Resume r:
                    if (!d.Playing.GetValueOrDefault(r.App)) { d.Playing[r.App] = true; Report(d, r.App, true, rng.Next(20, 400)); }
                    break;
                case Release:
                    d.Releases++;
                    At(rng.Next(80, 1300), () => { if (Connected.Remove(d)) d.Handle(new HeadsetLink(H, false)); });
                    break;
                case Take:
                    d.Takes++;
                    // A single-point headset only accepts a new host once it is free.
                    At(rng.Next(300, 2500), () =>
                    {
                        if (Connected.Contains(d)) return;
                        if (!dual && Connected.Count > 0) return;   // page fails; the engine's take timeout copes
                        Connected.Add(d);
                        d.Handle(new HeadsetLink(H, true));
                    });
                    break;
                case Send s:
                    if (LinkUp) At(rng.Next(10, 800), () => { if (LinkUp) Other(d).Handle(new Received(s.M)); });
                    break;
                case Ask k:
                    AskPrompts++;
                    // The user answers yes most of the time, sometimes no, sometimes ignores it.
                    var roll = rng.Next(10);
                    if (roll < 7) At(rng.Next(300, 4000), () => d.Handle(new Answer(k.Id, true)));
                    else if (roll < 9) At(rng.Next(300, 4000), () => d.Handle(new Answer(k.Id, false)));
                    break;
                case StartTimer t:
                    d.Timers[t.Name] = Now + t.Ms;
                    var due = Now + t.Ms;
                    At(t.Ms, () => { if (d.Timers.GetValueOrDefault(t.Name) == due) { d.Timers.Remove(t.Name); d.Handle(new TimerFired(t.Name)); } });
                    break;
                case CancelTimer c:
                    d.Timers.Remove(c.Name);
                    break;
            }
        }

        /// <summary>The user presses play on <paramref name="d"/>: that app starts on whatever output that device has.</summary>
        public void UserPlays(Device d)
        {
            Trace.Add($"{Now,7} USER  play on {d.Role}");
            d.Playing["app"] = true;
            Report(d, "app", true, rng.Next(20, 300));
        }

        public void UserStops(Device d)
        {
            if (!d.Playing.GetValueOrDefault("app")) return;
            Trace.Add($"{Now,7} USER  stop on {d.Role}");
            d.Playing["app"] = false;
            Report(d, "app", false, rng.Next(20, 300));
        }

        public void SetLink(bool up)
        {
            if (LinkUp == up) return;
            Trace.Add($"{Now,7} LINK  {(up ? "up" : "down")}");
            LinkUp = up;
            Phone.Handle(new PeerLink(up));
            Pc.Handle(new PeerLink(up));
        }

        /// <summary>Runs queued events in time order until nothing is due before <paramref name="until"/>.</summary>
        public void RunUntil(long until)
        {
            while (true)
            {
                var next = queue.Where(q => q.At <= until).OrderBy(q => q.At).Cast<(long At, System.Action Run)?>().FirstOrDefault();
                if (next is null) break;
                queue.Remove(next.Value);
                Now = Math.Max(Now, next.Value.At);
                next.Value.Run();
            }
            Now = until;
        }

        public void Settle() => RunUntil(Now + 30_000);

        public string Dump() => string.Join("\n", Trace.TakeLast(60));
    }

    public static TheoryData<int, bool> Seeds()
    {
        var d = new TheoryData<int, bool>();
        for (var s = 1; s <= 150; s++) { d.Add(s, false); d.Add(s, true); }
        return d;
    }

    /// <summary>Random play/stop on both sides with a stable link: after each action settles, whoever played last has it.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void LastPlayWins(int seed, bool dual)
    {
        var sim = new Sim(seed, dual);
        var rng = new Random(seed * 7919);
        // Start with the headset on the PC.
        sim.Connected.Add(sim.Pc);
        sim.Pc.Handle(new HeadsetLink(H, true));
        sim.Settle();

        Device? lastQuick = null;
        for (var step = 0; step < 25; step++)
        {
            var d = rng.Next(2) == 0 ? sim.Phone : sim.Pc;
            var other = d == sim.Phone ? sim.Pc : sim.Phone;
            if (rng.Next(4) == 0) { sim.UserStops(d); sim.Settle(); lastQuick = null; continue; }
            sim.UserPlays(d);
            // The user sometimes acts again quickly, before the switch completes.
            if (rng.Next(5) == 0) { sim.RunUntil(sim.Now + rng.Next(50, 1500)); lastQuick = d; continue; }
            sim.Settle();

            if (!dual) Assert.True(sim.Connected.Count == 1, $"seed {seed} step {step}: single-point headset on {sim.Connected.Count} devices\n{sim.Dump()}");
            if (lastQuick is not null && lastQuick != d)
            {
                // Presses on two devices under ~2 s apart can't be ordered across the link; either may win, but
                // the outcome must be coherent: whoever holds the headset is playing.
                var holder = sim.Connected.Contains(d) ? d : other;
                Assert.True(sim.Connected.Count > 0, $"seed {seed} step {step}: nobody has the headset after near-simultaneous plays\n{sim.Dump()}");
                Assert.True(holder.Playing.GetValueOrDefault("app") || !sim.Connected.Contains(holder),
                    $"seed {seed} dual={dual} step {step}: {holder.Role} has the headset but its media is paused\n{sim.Dump()}");
            }
            else
            {
                Assert.True(sim.Connected.Contains(d), $"seed {seed} dual={dual} step {step}: {d.Role} played last but has no headset\n{sim.Dump()}");
                Assert.True(d.Playing.GetValueOrDefault("app"), $"seed {seed} dual={dual} step {step}: {d.Role}'s media was left paused\n{sim.Dump()}");
                Assert.False(other.Playing.GetValueOrDefault("app") && sim.Connected.Contains(other),
                    $"seed {seed}: both devices still playing into a dual headset\n{sim.Dump()}");
            }
            lastQuick = null;
        }
    }

    /// <summary>The headset changes hands only in response to a user's play, never by itself (no ping-pong).</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void NoPingPong(int seed, bool dual)
    {
        var sim = new Sim(seed, dual);
        var rng = new Random(seed * 104729);
        sim.Connected.Add(sim.Phone);
        sim.Phone.Handle(new HeadsetLink(H, true));
        sim.Settle();

        for (var step = 0; step < 20; step++)
        {
            sim.UserPlays(rng.Next(2) == 0 ? sim.Phone : sim.Pc);
            sim.Settle();
            var takes = sim.Phone.Takes + sim.Pc.Takes;
            var releases = sim.Phone.Releases + sim.Pc.Releases;
            // Leave it alone for a long time: nothing may move.
            sim.RunUntil(sim.Now + 120_000);
            Assert.True(takes == sim.Phone.Takes + sim.Pc.Takes && releases == sim.Phone.Releases + sim.Pc.Releases,
                $"seed {seed} dual={dual}: the headset moved without any user action\n{sim.Dump()}");
        }
    }

    /// <summary>Link drops mid-switch: the claimer falls back to taking the headset itself; nothing stays paused.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void LinkDropsNeverLeaveMediaPaused(int seed, bool dual)
    {
        var sim = new Sim(seed, dual);
        var rng = new Random(seed * 15485863);
        sim.Connected.Add(sim.Pc);
        sim.Pc.Handle(new HeadsetLink(H, true));
        sim.Settle();

        for (var step = 0; step < 15; step++)
        {
            var d = rng.Next(2) == 0 ? sim.Phone : sim.Pc;
            sim.UserPlays(d);
            sim.RunUntil(sim.Now + rng.Next(0, 2500));
            sim.SetLink(rng.Next(3) != 0);
            sim.Settle();
            sim.SetLink(true);
            sim.Settle();
            Assert.True(d.Playing.GetValueOrDefault("app"), $"seed {seed} dual={dual} step {step}: {d.Role}'s media was left paused after a link drop\n{sim.Dump()}");
        }
    }

    /// <summary>Ask rule: whatever the user answers (or ignores), media ends up playing somewhere, never stuck paused.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void AskNeverStrandsMedia(int seed, bool dual)
    {
        var sim = new Sim(seed, dual, phoneRule: "ask", pcRule: "ask");
        var rng = new Random(seed * 32452843);
        sim.Connected.Add(sim.Pc);
        sim.Pc.Handle(new HeadsetLink(H, true));
        sim.Settle();

        for (var step = 0; step < 15; step++)
        {
            var d = rng.Next(2) == 0 ? sim.Phone : sim.Pc;
            sim.UserPlays(d);
            sim.Settle();
            Assert.True(d.Playing.GetValueOrDefault("app"), $"seed {seed} dual={dual} step {step}: {d.Role}'s media stuck paused after an Ask\n{sim.Dump()}");
        }
        Assert.True(sim.AskPrompts > 0, "the Ask rule never prompted");
    }
}
