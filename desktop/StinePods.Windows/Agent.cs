using System.Collections.Concurrent;
using System.Diagnostics;
using StinePods.Core;
using StinePods.Windows.Audio;
using StinePods.Windows.Link;
using StinePods.Windows.Media;
using StinePods.Windows.Native;
using Action = StinePods.Core.Action;

namespace StinePods.Windows;

/// <summary>
/// Wires the platform pieces to the <see cref="Engine"/>. Everything that touches the engine, the session or the
/// timers runs on the agent loop (one thread); native work goes to the audio worker with timeouts, because
/// Windows Bluetooth/audio calls can hang (docs/spikes/S2-windows-connect.md).
/// </summary>
internal sealed class Agent(Store store, Tray tray)
{
    static readonly TimeSpan NativeTimeout = TimeSpan.FromSeconds(10);

    readonly BlockingCollection<System.Action> loop = [];
    readonly Engine engine = new(store.Settings);
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Dictionary<string, Timer> timers = [];
    readonly Worker audio = new("audio");

    HeadsetAudio? headsets;
    MediaWatcher? media;
    RfcommServer.Connection? conn;
    Session? session;
    FrameReader? reader;
    string peerName = "";

    public void Run()
    {
        tray.Enabled = store.Enabled;
        tray.ToggleRequested += () => Post(Toggle);
        tray.ExitRequested += () => Post(Exit);

        audio.Run(() => headsets = new HeadsetAudio(
            (h, here) => Post(() => Feed(new HeadsetLink(h, here))),
            pids => Post(() => { activePids = pids; RecomputeOther(); }),
            audio.Post)).Wait();
        audio.Run(() => headsets!.Watch(store.Settings.Headsets)).Wait();
        media = MediaWatcher.StartAsync(
            pids => audio.Run(() => headsets!.IsAudible(pids)),
            (app, playing) => Post(() => OnPlayback(app, playing)),
            onLikelyStart: () => Post(Ping)).GetAwaiter().GetResult();
        StartServer();
        UpdateStatus();
        Log.Info($"agent: started (id {store.DeviceId}, headsets [{string.Join(", ", store.Settings.Headsets)}])");

        foreach (var work in loop.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex) { Log.Warn($"agent: {ex}"); }
        }
    }

    void Post(System.Action work)
    {
        if (!loop.IsAddingCompleted) loop.Add(work);
    }

    RfcommServer? server;

    /// <summary>The Bluetooth radio is off or unusable: don't prompt or try to switch, just say so.</summary>
    bool bluetoothDown;

    void StartServer()
    {
        server = new RfcommServer(c => Post(() => OnConnected(c)), () => Post(RetryServer));
        try
        {
            server.Start();
            if (bluetoothDown) Log.Info("link: Bluetooth is back");
            bluetoothDown = false;
            UpdateStatus();
        }
        catch (Exception ex)
        {
            if (!bluetoothDown) Log.Warn($"link: can't listen ({ex.Message}); retrying every 10 s");
            RetryServer();
        }
    }

    void RetryServer()
    {
        server = null;
        bluetoothDown = true;
        UpdateStatus();
        _ = Task.Delay(10_000).ContinueWith(_ => Post(StartServer));
    }

    // ---- engine ----

    void Feed(Event e)
    {
        var actions = engine.Handle(e, clock.ElapsedMilliseconds);
        Log.Info($"> {e}" + (actions.Count > 0 ? $"  < {string.Join(" | ", actions.Select(Describe.Of))}" : ""));
        foreach (var a in actions) Execute(a);
        UpdateStatus();
    }

    // ---- apps without media controls ("other") ----

    /// <summary>Engine app id for sound from apps that don't use the system media controls.</summary>
    public const string Other = "other";
    const int OtherConfirmMs = 1000;
    IReadOnlySet<uint> activePids = new HashSet<uint>();
    bool otherReported;
    Timer? otherTimer;

    /// <summary>
    /// "Other" audio: a process producing sound that isn't a media-controls app saying Playing (those are handled
    /// by name). It must keep going for a second, so short sounds don't count.
    /// </summary>
    void RecomputeOther()
    {
        var media = this.media?.SaysPlaying() ?? [];
        var otherNow = activePids.Any(pid => IsOther(pid, media));
        if (otherNow && !otherReported && otherTimer is null)
        {
            otherTimer = new Timer(_ => Post(() =>
            {
                otherTimer?.Dispose();
                otherTimer = null;
                var media2 = this.media?.SaysPlaying() ?? [];
                if (activePids.Count > 0 && !otherReported && activePids.Any(pid => IsOther(pid, media2)))
                {
                    otherReported = true;
                    OnPlayback(Other, true);
                }
            }), null, OtherConfirmMs, Timeout.Infinite);
        }
        else if (!otherNow)
        {
            otherTimer?.Dispose();
            otherTimer = null;
            if (otherReported) { otherReported = false; OnPlayback(Other, false); }
        }
    }

    static bool IsOther(uint pid, HashSet<string> media)
    {
        try { return !media.Contains(System.Diagnostics.Process.GetProcessById((int)pid).ProcessName + ".exe"); }
        catch (ArgumentException) { return false; }   // already exited
    }

    void OnPlayback(string app, bool playing)
    {
        if (playing) playingHere.Add(app); else playingHere.Remove(app);
        UpdateKeepAlive();
        if (app != Other) RecomputeOther();
        if (playing && bluetoothDown)
        {
            Log.Info($"Bluetooth is off: not switching for {app}");
            return;
        }
        if (playing && !store.Enabled)
        {
            Log.Info($"switching off: ignoring {app} playing");
            return;
        }
        Feed(new Playback(app, playing));
    }

    void Execute(Action a)
    {
        switch (a)
        {
            // Apps without media controls can't be paused or resumed; they simply keep playing.
            case Pause p when p.App != Other: Background($"pause {p.App}", () => media!.PauseAsync(p.App)); break;
            case Resume r when r.App != Other: Background($"resume {r.App}", () => media!.PlayAsync(r.App)); break;
            case Release r: Native($"release {r.H}", () => HeadsetAudio.Release(r.H)); break;
            // A take that fails outright is reported like its timeout, so paused media resumes straight away.
            case Take t: Native($"take {t.H}", () => headsets!.Take(t.H), onFail: () => FireNow("take")); break;
            case Send s:
                if (session is { Ready: true } && conn is not null) conn.Send(session.Send(s.M));
                break;
            case Ask k:
                tray.Balloon("Switch headphones to this PC?",
                    (k.App == Other ? "Audio started on this PC." : $"{k.App} started playing.") + " Click here to move your headphones.",
                    () => Post(() => Feed(new Answer(k.Id, true))));
                break;
            case StartTimer t: StartTimer(t.Name, t.Ms); break;
            case CancelTimer c:
                if (timers.Remove(c.Name, out var old)) old.Dispose();
                break;
            case Notify n: tray.Balloon("StinePods", n.Text, warning: true); break;
        }
    }

    void StartTimer(string name, int ms)
    {
        if (timers.Remove(name, out var old)) old.Dispose();
        Timer? timer = null;
        timer = new Timer(_ => Post(() =>
        {
            // A timer cancelled or replaced after it fired must not reach the engine.
            if (!timers.TryGetValue(name, out var current) || current != timer) return;
            timers.Remove(name);
            current.Dispose();
            Feed(new TimerFired(name));
        }), null, ms, Timeout.Infinite);
        timers[name] = timer;
    }

    void Native(string what, System.Action work, System.Action? onFail = null) =>
        _ = audio.Run(work).WaitAsync(NativeTimeout).ContinueWith(t =>
        {
            if (!t.IsFaulted) return;
            Log.Warn($"{what} failed: {t.Exception!.InnerException?.Message}");
            if (onFail is not null) Post(onFail);
        });

    /// <summary>Fires a pending engine timer early (on the agent loop).</summary>
    void FireNow(string name)
    {
        if (!timers.Remove(name, out var t)) return;
        t.Dispose();
        Feed(new TimerFired(name));
    }

    static void Background(string what, Func<Task<bool>> work) =>
        _ = Task.Run(work).ContinueWith(t =>
        {
            if (t.IsFaulted || !t.Result) Log.Warn($"{what}: {(t.IsFaulted ? t.Exception!.InnerException?.Message : "not accepted by the app")}");
        });

    // ---- phone link ----

    void OnConnected(RfcommServer.Connection c)
    {
        conn?.Close();
        conn = c;
        reader = new FrameReader();
        session = new Session(Role.Pc, store.DeviceId, Environment.MachineName, store);
        c.Run(data => Post(() => OnData(c, data)), () => Post(() => OnClosed(c)));
        foreach (var f in session.Start()) c.Send(f);
    }

    void OnData(RfcommServer.Connection c, byte[] data)
    {
        if (c != conn || session is null || reader is null) return;
        List<byte[]> frames;
        try { frames = reader.Push(data); }
        catch (InvalidDataException) { Log.Warn("link: malformed stream, closing"); c.Close(); return; }
        foreach (var frame in frames) Apply(c, session, session.Receive(frame));
    }

    void Apply(RfcommServer.Connection c, Session s, (List<byte[]> Out, List<LinkEvent> Events) step)
    {
        foreach (var f in step.Out) c.Send(f);
        foreach (var e in step.Events)
        {
            switch (e)
            {
                case PairingCode p: AskPairing(c, s, p); break;
                case LinkReady r:
                    peerName = r.PeerName;
                    Log.Info($"link: ready with {r.PeerName}");
                    Feed(new PeerLink(true));
                    UpdateKeepAlive();
                    break;
                case LinkRtt rtt:
                    if (rtt.Ms > 300) Log.Info($"link: slow round trip {rtt.Ms} ms (link was asleep)");
                    break;
                case Incoming m:
                    Feed(new Received(m.M));
                    if (m.M.T == "config") OnConfig();
                    break;
                case LinkFailed f:
                    Log.Warn($"link: {f.Reason}");
                    tray.Balloon("StinePods", $"Pairing failed: {f.Reason}", warning: true);
                    c.Close();
                    break;
            }
        }
    }

    // The link sleeps (Bluetooth sniff) after ~5-8 s of silence and then needs up to ~1.2 s to deliver a message
    // (docs/spikes/S7-link-latency.md). While something plays here a switch is likely, so keep the link awake.
    const int KeepAliveMs = 4000;
    readonly HashSet<string> playingHere = [];
    Timer? keepAlive;

    void UpdateKeepAlive()
    {
        var want = playingHere.Count > 0 && session is { Ready: true };
        if (want && keepAlive is null) keepAlive = new Timer(_ => Post(Ping), null, KeepAliveMs, KeepAliveMs);
        else if (!want && keepAlive is not null) { keepAlive.Dispose(); keepAlive = null; }
    }

    /// <summary>Also sent the moment playback looks likely to start, so the link is awake by the time we claim.</summary>
    void Ping()
    {
        if (session is { Ready: true } s && conn is { } c) c.Send(s.Ping());
    }

    void AskPairing(RfcommServer.Connection c, Session s, PairingCode p)
    {
        Log.Info("link: pairing requested");
        // MessageBox blocks, so it gets its own thread; the answer comes back through the loop.
        new Thread(() =>
        {
            var text = $"Pair with {p.PeerName}?\n\nCheck that your phone shows the same code:\n\n{p.Code / 1000:D3} {p.Code % 1000:D3}";
            var yes = Win32.MessageBox(0, text, "StinePods pairing", Win32.MbYesNo | Win32.MbIconQuestion | Win32.MbTopMost | Win32.MbSetForeground) == Win32.IdYes;
            Post(() => { if (s == session && c == conn) Apply(c, s, s.ConfirmPairing(yes)); });
        }) { IsBackground = true }.Start();
    }

    void OnConfig()
    {
        store.Save();
        var hs = store.Settings.Headsets.ToList();
        audio.Post(() => headsets!.Watch(hs));
        Log.Info($"config: headsets [{string.Join(", ", hs)}], prefer {store.Settings.Prefer}, return {store.Settings.ReturnOnStop}");
    }

    void OnClosed(RfcommServer.Connection c)
    {
        if (c != conn) return;
        var wasReady = session?.Ready == true;
        conn = null;
        session = null;
        reader = null;
        Log.Info("link: phone disconnected");
        UpdateKeepAlive();
        if (wasReady) Feed(new PeerLink(false));
        UpdateStatus();
    }

    // ---- tray ----

    void Toggle()
    {
        store.Enabled = !store.Enabled;
        tray.Enabled = store.Enabled;
        store.Save();
        UpdateStatus();
    }

    void Exit()
    {
        Log.Info("agent: exiting");
        conn?.Close();
        server?.Stop();
        tray.Remove();
        loop.CompleteAdding();
    }

    void UpdateStatus()
    {
        if (bluetoothDown) { tray.SetStatus("Bluetooth is off"); return; }
        var phone = session is { Ready: true } ? $"Phone: {peerName}" : "Waiting for phone";
        var here = store.Settings.Headsets.Where(engine.IsHere).ToList();
        var hs = store.Settings.Headsets.Count == 0 ? "no headphones set up" : here.Count > 0 ? "headphones here" : "headphones elsewhere";
        tray.SetStatus($"{(store.Enabled ? "" : "Paused · ")}{phone} · {hs}");
    }

    /// <summary>A dedicated MTA thread for Core Audio / Bluetooth calls, processed in order.</summary>
    sealed class Worker
    {
        readonly BlockingCollection<System.Action> queue = [];

        public Worker(string name) => new Thread(() =>
        {
            ComApi.EnsureMta();
            foreach (var work in queue.GetConsumingEnumerable())
            {
                try { work(); }
                catch (Exception ex) { Log.Warn($"{name}: {ex.Message}"); }
            }
        }) { IsBackground = true, Name = name }.Start();

        public void Post(System.Action work) => queue.Add(work);

        public Task Run(System.Action work) => Run<object?>(() => { work(); return null; });

        public Task<T> Run<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Add(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }
    }
}
