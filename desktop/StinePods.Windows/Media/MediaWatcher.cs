using System.Diagnostics;
using Windows.Media.Control;

namespace StinePods.Windows.Media;

/// <summary>
/// Local media playback via the system media sessions (GSMTC, docs/spikes/S3-S4-media-detection.md).
/// Reports an app as playing only once it is audible here: GSMTC also says "Playing" for Spotify Connect playback
/// that is actually coming out of the phone.
/// </summary>
internal sealed class MediaWatcher
{
    const int AudibleChecks = 10, AudibleCheckMs = 150;

    readonly GlobalSystemMediaTransportControlsSessionManager manager;
    readonly Func<IReadOnlySet<uint>, Task<bool>> isAudible;
    readonly Action<string, bool> onPlayback;
    readonly System.Action onLikelyStart;
    readonly Lock gate = new();
    readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> sessions = [];
    readonly Dictionary<string, bool> reported = [];
    readonly Dictionary<string, int> generation = [];

    MediaWatcher(GlobalSystemMediaTransportControlsSessionManager manager, Func<IReadOnlySet<uint>, Task<bool>> isAudible,
        Action<string, bool> onPlayback, System.Action onLikelyStart)
    {
        this.manager = manager;
        this.isAudible = isAudible;
        this.onPlayback = onPlayback;
        this.onLikelyStart = onLikelyStart;
    }

    /// <param name="onPlayback">(app id, playing): de-duplicated, audible-confirmed.</param>
    /// <param name="onLikelyStart">An app just said Playing; fires before the (slower) audible confirmation.</param>
    public static async Task<MediaWatcher> StartAsync(Func<IReadOnlySet<uint>, Task<bool>> isAudible, Action<string, bool> onPlayback, System.Action onLikelyStart)
    {
        var w = new MediaWatcher(await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), isAudible, onPlayback, onLikelyStart);
        w.manager.SessionsChanged += (_, _) => w.Sync();
        w.Sync();
        return w;
    }

    /// <summary>Apps whose media session says Playing right now (before any audible check).</summary>
    public HashSet<string> SaysPlaying()
    {
        List<GlobalSystemMediaTransportControlsSession> all;
        lock (gate) all = [.. sessions.Values];
        return all.Where(s => s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            .Select(s => s.SourceAppUserModelId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public Task<bool> PauseAsync(string app) => Control(app, s => s.TryPauseAsync().AsTask());
    public Task<bool> PlayAsync(string app) => Control(app, s => s.TryPlayAsync().AsTask());

    async Task<bool> Control(string app, Func<GlobalSystemMediaTransportControlsSession, Task<bool>> op)
    {
        GlobalSystemMediaTransportControlsSession? s;
        lock (gate) sessions.TryGetValue(app, out s);
        return s is not null && await op(s).WaitAsync(TimeSpan.FromSeconds(3));
    }

    void Sync()
    {
        var current = manager.GetSessions().ToDictionary(s => s.SourceAppUserModelId, s => s);
        List<string> gone;
        lock (gate)
        {
            gone = [.. sessions.Keys.Where(k => !current.ContainsKey(k))];
            foreach (var k in gone) sessions.Remove(k);
            foreach (var (id, s) in current)
            {
                if (sessions.ContainsKey(id)) continue;
                sessions[id] = s;
                s.PlaybackInfoChanged += (x, _) => Update(x.SourceAppUserModelId, x);
            }
        }
        foreach (var id in gone) Report(id, false);
        foreach (var (id, s) in current) Update(id, s);
    }

    void Update(string app, GlobalSystemMediaTransportControlsSession s)
    {
        var playing = s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        int gen;
        bool wasPlaying;
        lock (gate)
        {
            // Every status change starts a new generation, cancelling any audible check still in flight.
            gen = generation[app] = generation.GetValueOrDefault(app) + 1;
            wasPlaying = reported.GetValueOrDefault(app);
            if (wasPlaying == playing && !playing) return;
        }
        if (!playing) { Report(app, false); return; }
        if (!wasPlaying) onLikelyStart();
        _ = ConfirmAudibleAsync(app, gen);
    }

    async Task ConfirmAudibleAsync(string app, int gen)
    {
        for (var i = 0; i < AudibleChecks; i++)
        {
            lock (gate) if (generation.GetValueOrDefault(app) != gen) return;
            if (await AudibleAsync(app)) { Report(app, true); return; }
            await Task.Delay(AudibleCheckMs);
        }
        Log.Info($"media: {app} says Playing but isn't audible here (remote playback?)");
    }

    Task<bool> AudibleAsync(string app)
    {
        // Packaged apps report an AUMID instead of an exe name; there is no cheap pid mapping, so trust GSMTC.
        if (!app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(true);
        var pids = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app)).Select(p => (uint)p.Id).ToHashSet();
        return isAudible(pids);
    }

    void Report(string app, bool playing)
    {
        lock (gate)
        {
            if (reported.GetValueOrDefault(app) == playing) return;
            reported[app] = playing;
        }
        onPlayback(app, playing);
    }
}
