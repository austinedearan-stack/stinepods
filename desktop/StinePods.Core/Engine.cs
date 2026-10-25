namespace StinePods.Core;

public enum Role { Phone, Pc }
public enum AppRule { Auto, Ask, Ignore }
public enum Prefer { Phone, Pc, None }
public enum ReturnOnStop { Stay, Return }

public sealed class Settings
{
    public required Role Role { get; init; }
    public List<string> Headsets { get; set; } = [];
    public Prefer Prefer { get; set; } = Prefer.Phone;
    public ReturnOnStop ReturnOnStop { get; set; } = ReturnOnStop.Stay;
    public bool CallsTakeHeadset { get; set; }
    public Dictionary<string, AppRule> Apps { get; set; } = [];

    public AppRule RuleFor(string app) => Apps.TryGetValue(app, out var r) ? r : AppRule.Auto;
}

/// <summary>A link-protocol message (see protocol/PROTOCOL.md). Only the fields a type uses are set.</summary>
public sealed record Msg(string T, string? H = null, int? Id = null, string? Why = null)
{
    /// <summary>`state`: whether the headset is connected to the sender.</summary>
    public bool? Here { get; init; }
    /// <summary>`log`: one diagnostic line (phone → PC).</summary>
    public string? Text { get; init; }
    public List<string>? Headsets { get; init; }
    public Prefer? Prefer { get; init; }
    public ReturnOnStop? ReturnOnStop { get; init; }
}

public abstract record Event;
public sealed record PeerLink(bool Up) : Event;
/// <summary>The headset's link to <em>this</em> device came up or went down.</summary>
public sealed record HeadsetLink(string H, bool Here) : Event;
/// <summary>Audible local playback of an app started or stopped.</summary>
public sealed record Playback(string App, bool Playing) : Event;
public sealed record Received(Msg M) : Event;
public sealed record Answer(int AskId, bool Yes) : Event;
public sealed record TimerFired(string Name) : Event;
public sealed record Call(bool Active) : Event;

public abstract record Action;
public sealed record Pause(string App) : Action;
public sealed record Resume(string App) : Action;
public sealed record Release(string H) : Action;
public sealed record Take(string H) : Action;
public sealed record Send(Msg M) : Action;
public sealed record Ask(int Id, string App) : Action;
public sealed record StartTimer(string Name, int Ms) : Action;
public sealed record CancelTimer(string Name) : Action;
public sealed record Notify(string Text) : Action;

/// <summary>
/// The hand-off coordinator. Pure and single-threaded: feed it events with the current time, perform the actions it
/// returns. Platform code owns all I/O, timers and threading. Behaviour is specified by protocol/vectors.
/// </summary>
public sealed class Engine(Settings settings)
{
    public const int ClaimTimeoutMs = 2500, TakeTimeoutMs = 8000, ReleaseTimeoutMs = 4000;
    public const int AskTimeoutMs = 15000, ReturnDelayMs = 10000, CooldownMs = 3000, ExpectWindowMs = 3000, RetryMs = 500;

    enum Phase { AskWait, AwaitRelease, AwaitConnect }

    sealed class Switch
    {
        public required string H;
        public required Phase Phase;
        public string? App;          // paused here, resumed once the headset is connected
        public int? ClaimId;
        public int AskId;
        public bool ViaPeer;         // taken from the peer (enables return-on-stop)
    }

    sealed record Releasing(string H, int? Id, string Why);

    public Settings Settings { get; } = settings;

    readonly HashSet<string> here = [];
    readonly HashSet<string> peerHas = [];   // headsets the peer says are connected to it (dual-connection headsets)
    readonly HashSet<string> playing = [];
    readonly Dictionary<string, (bool Playing, long Until)> expect = [];
    bool peerUp, callActive, returnArmed;
    string? deferred;   // app whose play arrived mid-switch / in cooldown; re-checked by the "retry" timer
    Switch? pending;
    Releasing? releasing;
    (string App, string H)? pausedForPeer;
    string? takenFrom;
    long cooldownUntil;
    int nextClaimId, nextAskId;

    public bool PeerUp => peerUp;
    public bool IsHere(string h) => here.Contains(h);
    public bool Busy => pending is not null;

    string? Headset => Settings.Headsets.Count > 0 ? Settings.Headsets[0] : null;

    public List<Action> Handle(Event e, long now)
    {
        var a = new List<Action>();
        switch (e)
        {
            case PeerLink p: OnPeerLink(p.Up, a); break;
            case HeadsetLink h: OnHeadsetLink(h.H, h.Here, now, a); break;
            case Playback p when p.Playing: OnPlay(p.App, now, a); break;
            case Playback p: OnStop(p.App, now, a); break;
            case Received r: OnMessage(r.M, now, a); break;
            case Answer x: OnAnswer(x.AskId, x.Yes, now, a); break;
            case TimerFired t: OnTimer(t.Name, now, a); break;
            case Call c: OnCall(c.Active, a); break;
        }
        return a;
    }

    void OnPeerLink(bool up, List<Action> a)
    {
        peerUp = up;
        if (up)
        {
            if (Settings.Role == Role.Phone)
            {
                a.Add(new Send(new Msg("config") { Headsets = [.. Settings.Headsets], Prefer = Settings.Prefer, ReturnOnStop = Settings.ReturnOnStop }));
                SendStates(a);
            }
            return;
        }
        peerHas.Clear();
        // Nobody left to answer a claim: take the headset on our own.
        if (pending is { Phase: Phase.AwaitRelease } p)
        {
            a.Add(new CancelTimer("claim"));
            BeginTake(p, a);
        }
        releasing = null;
    }

    void OnPlay(string app, long now, List<Action> a)
    {
        if (Expected(app, true, now)) return;
        // We just paused this app and are waiting for its pause to show up: a "playing" report now is a late
        // report from before our pause (detection can lag), not a new play. Acting on it would claim the headset
        // back on its own.
        if (expect.TryGetValue(app, out var x) && !x.Playing && now <= x.Until) return;
        playing.Add(app);
        DisarmReturn(a);
        Decide(app, now, a);
    }

    /// <summary>Should this app's playback move the headset here? (Also re-run for a deferred play.)</summary>
    void Decide(string app, long now, List<Action> a)
    {
        var h = Headset;
        if (h is null) return;
        // Already connected here does nothing, unless the peer holds a second connection to the same headset:
        // then its stream may be the one being heard, so ask it to let go.
        if (pending is null && releasing is null && here.Contains(h) && !peerHas.Contains(h)) return;
        var rule = Settings.RuleFor(app);
        if (rule == AppRule.Ignore) return;
        // Mid-switch or just after one: don't act now (that is where echoes and ping-pong come from), but don't
        // drop the user's play either. Look again once things have settled, if the app is still playing.
        if (pending is not null || releasing is not null || now < cooldownUntil)
        {
            deferred = app;
            a.Add(new StartTimer("retry", (int)Math.Max(cooldownUntil - now, RetryMs)));
            return;
        }
        deferred = null;

        // Pause straight away so nothing is missed while the headset moves; resumed once it's connected here.
        PauseApp(app, now, a);
        if (rule == AppRule.Ask)
        {
            pending = new Switch { H = h, Phase = Phase.AskWait, App = app, AskId = ++nextAskId };
            a.Add(new Ask(pending.AskId, app));
            a.Add(new StartTimer("ask", AskTimeoutMs));
            return;
        }
        BeginClaim(new Switch { H = h, Phase = Phase.AwaitRelease, App = app }, a);
    }

    void OnStop(string app, long now, List<Action> a)
    {
        if (Expected(app, false, now)) return;
        playing.Remove(app);
        if (deferred == app)
        {
            deferred = null;
            a.Add(new CancelTimer("retry"));
        }
        if (Settings.ReturnOnStop == ReturnOnStop.Return && playing.Count == 0 && peerUp
            && takenFrom is { } h && here.Contains(h) && pending is null)
        {
            a.Add(new StartTimer("return", ReturnDelayMs));
            returnArmed = true;
        }
    }

    void DisarmReturn(List<Action> a)
    {
        if (!returnArmed) return;
        a.Add(new CancelTimer("return"));
        returnArmed = false;
    }

    void OnAnswer(int askId, bool yes, long now, List<Action> a)
    {
        if (pending is not { Phase: Phase.AskWait } p || p.AskId != askId) return;
        a.Add(new CancelTimer("ask"));
        if (yes)
        {
            p.Phase = Phase.AwaitRelease;
            BeginClaim(p, a);
        }
        else
        {
            pending = null;
            if (p.App is { } app) ResumeApp(app, now, a);
        }
    }

    void OnCall(bool active, List<Action> a)
    {
        callActive = active;
        if (active && Settings.Role == Role.Phone && Settings.CallsTakeHeadset
            && Headset is { } h && !here.Contains(h) && pending is null)
            BeginClaim(new Switch { H = h, Phase = Phase.AwaitRelease }, a);
    }

    void BeginClaim(Switch s, List<Action> a)
    {
        pending = s;
        if (!peerUp)
        {
            BeginTake(s, a);
            return;
        }
        s.Phase = Phase.AwaitRelease;
        s.ClaimId = ++nextClaimId;
        s.ViaPeer = true;
        a.Add(new Send(new Msg("claim", s.H, s.ClaimId)));
        a.Add(new StartTimer("claim", ClaimTimeoutMs));
    }

    void BeginTake(Switch s, List<Action> a)
    {
        pending = s;
        s.Phase = Phase.AwaitConnect;
        a.Add(new Take(s.H));
        a.Add(new StartTimer("take", TakeTimeoutMs));
    }

    void OnMessage(Msg m, long now, List<Action> a)
    {
        switch (m.T)
        {
            case "config" when Settings.Role == Role.Pc:
                if (m.Headsets is { } hs) Settings.Headsets = [.. hs];
                if (m.Prefer is { } pr) Settings.Prefer = pr;
                if (m.ReturnOnStop is { } rs) Settings.ReturnOnStop = rs;
                SendStates(a);
                break;
            case "state" when m.H is { } h && m.Here is { } isHere:
                if (isHere) peerHas.Add(h); else peerHas.Remove(h);
                break;
            case "claim" when m.H is { } h:
                OnClaim(h, m.Id, now, a);
                break;
            case "released" when m.H is { } h:
                OnReleased(h, m.Why, now, a);
                break;
            case "deny" when pending is { Phase: Phase.AwaitRelease } p && p.H == m.H && p.ClaimId == m.Id:
                a.Add(new CancelTimer("claim"));
                a.Add(new Notify(m.Why == "call" ? "Headset is busy: phone call in progress" : "Headset switch refused"));
                pending = null;
                break;
        }
    }

    void OnClaim(string h, int? id, long now, List<Action> a)
    {
        if (callActive && Settings.Role == Role.Phone)
        {
            a.Add(new Send(new Msg("deny", h, id, "call")));
            return;
        }
        if (pending is { } p && p.H == h)
        {
            // Both sides claimed at once: the phone wins, the PC yields (its app stays paused).
            if (Settings.Role == Role.Phone && p.Phase == Phase.AwaitRelease) return;
            a.Add(new CancelTimer(p.Phase switch { Phase.AskWait => "ask", Phase.AwaitRelease => "claim", _ => "take" }));
            pending = null;
        }
        foreach (var app in playing.ToList())
        {
            PauseApp(app, now, a);
            pausedForPeer = (app, h);
        }
        playing.Clear();
        takenFrom = null;
        DisarmReturn(a);
        if (here.Contains(h))
        {
            releasing = new Releasing(h, id, "claim");
            a.Add(new Release(h));
            a.Add(new StartTimer("release", ReleaseTimeoutMs));
        }
        else
        {
            a.Add(new Send(new Msg("released", h, id, "claim")));
        }
    }

    void OnReleased(string h, string? why, long now, List<Action> a)
    {
        if (pending is { Phase: Phase.AwaitRelease } p && p.H == h)
        {
            a.Add(new CancelTimer("claim"));
            TakeOrComplete(p, now, a);
            return;
        }
        if (pending is not null || here.Contains(h)) return;
        var mine = Settings.Role == Role.Phone ? Prefer.Phone : Prefer.Pc;
        if (why == "return" || (why == "prefer" && Settings.Prefer == mine))
        {
            var app = why == "return" && pausedForPeer is { } pf && pf.H == h ? pf.App : null;
            pausedForPeer = null;
            BeginTake(new Switch { H = h, Phase = Phase.AwaitConnect, App = app }, a);
        }
    }

    void OnHeadsetLink(string h, bool isHere, long now, List<Action> a)
    {
        if (peerUp && Settings.Headsets.Contains(h)) a.Add(new Send(new Msg("state", h) { Here = isHere }));
        if (!isHere)
        {
            here.Remove(h);
            if (releasing is { } r && r.H == h)
            {
                a.Add(new CancelTimer("release"));
                if (peerUp) a.Add(new Send(new Msg("released", h, r.Id, r.Why)));
                releasing = null;
                // Apps may auto-resume on the local speaker when the headset leaves; don't claim it straight back.
                cooldownUntil = now + CooldownMs;
            }
            if (takenFrom == h) takenFrom = null;
            return;
        }

        here.Add(h);
        if (pending is { Phase: Phase.AwaitConnect } p && p.H == h)
        {
            a.Add(new CancelTimer("take"));
            Complete(p, now, a);
            return;
        }
        if (pending is not null || releasing is not null) return;

        // Picked up on its own (e.g. headset just powered on): hand it to the preferred device if we're idle.
        var other = Settings.Role == Role.Phone ? Prefer.Pc : Prefer.Phone;
        if (Settings.Prefer == other && peerUp && playing.Count == 0 && Settings.Headsets.Contains(h))
        {
            releasing = new Releasing(h, null, "prefer");
            a.Add(new Release(h));
            a.Add(new StartTimer("release", ReleaseTimeoutMs));
        }
    }

    void OnTimer(string name, long now, List<Action> a)
    {
        switch (name)
        {
            case "claim" when pending is { Phase: Phase.AwaitRelease } p:
                TakeOrComplete(p, now, a);   // peer didn't answer: carry on without it
                break;
            case "take" when pending is { Phase: Phase.AwaitConnect } p:
                a.Add(new Notify("Couldn't connect the headset"));
                pending = null;
                // Don't leave the user's media paused: let it carry on where it is (speaker or wherever).
                if (p.App is { } paused) ResumeApp(paused, now, a);
                break;
            case "ask" when pending is { Phase: Phase.AskWait } p:
                pending = null;
                if (p.App is { } app) ResumeApp(app, now, a);
                break;
            case "release" when releasing is { } r:
                if (peerUp) a.Add(new Send(new Msg("released", r.H, r.Id, r.Why)));
                releasing = null;
                break;
            case "retry":
                if (deferred is { } d && playing.Contains(d)) Decide(d, now, a);
                else deferred = null;
                break;
            case "return":
                returnArmed = false;
                if (takenFrom is not { } h || !here.Contains(h) || playing.Count > 0 || pending is not null || !peerUp) break;
                releasing = new Releasing(h, null, "return");
                takenFrom = null;
                a.Add(new Release(h));
                a.Add(new StartTimer("release", ReleaseTimeoutMs));
                break;
        }
    }

    /// <summary>The peer let go: connect, or if we already hold a connection (dual-connection headset) we're done.</summary>
    void TakeOrComplete(Switch p, long now, List<Action> a)
    {
        if (here.Contains(p.H)) Complete(p, now, a);
        else BeginTake(p, a);
    }

    void Complete(Switch p, long now, List<Action> a)
    {
        if (p.App is { } app) ResumeApp(app, now, a);
        takenFrom = p.ViaPeer ? p.H : null;
        cooldownUntil = now + CooldownMs;
        pending = null;
    }

    void SendStates(List<Action> a)
    {
        foreach (var h in Settings.Headsets) a.Add(new Send(new Msg("state", h) { Here = here.Contains(h) }));
    }

    void PauseApp(string app, long now, List<Action> a)
    {
        a.Add(new Pause(app));
        expect[app] = (false, now + ExpectWindowMs);
        playing.Remove(app);
    }

    void ResumeApp(string app, long now, List<Action> a)
    {
        a.Add(new Resume(app));
        expect[app] = (true, now + ExpectWindowMs);
        playing.Add(app);
    }

    /// <summary>Swallows the playback event our own pause/resume command caused, so it can't trigger a switch.</summary>
    bool Expected(string app, bool isPlaying, long now)
    {
        if (!expect.TryGetValue(app, out var x)) return false;
        if (now > x.Until) { expect.Remove(app); return false; }
        if (x.Playing != isPlaying) return false;
        expect.Remove(app);
        return true;
    }
}
