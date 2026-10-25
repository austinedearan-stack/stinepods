using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using StinePods.Windows.Native;

namespace StinePods.Windows.Audio;

/// <summary>
/// Which processes are producing sound right now, on any active output: event-driven via Core Audio session
/// notifications, no polling. Used to notice playback from apps without media controls ("other" apps).
/// System sounds are ignored. All members run on the audio worker thread.
/// </summary>
internal sealed partial class SessionMonitor(IMMDeviceEnumerator enumerator, Action<IReadOnlySet<uint>> onChange, Action<System.Action> post)
{
    static readonly StrategyBasedComWrappers Wrappers = new();
    static readonly uint OwnPid = (uint)Environment.ProcessId;

    sealed class Endpoint(IAudioSessionManager2 manager, Created created)
    {
        public readonly IAudioSessionManager2 Manager = manager;
        public readonly Created Created = created;
    }

    sealed class Tracked(string endpoint, IAudioSessionControl2 control, uint pid)
    {
        public readonly string Endpoint = endpoint;
        public readonly IAudioSessionControl2 Control = control;   // must stay referenced or its events stop
        public readonly uint Pid = pid;
        public Events? Events;
        public bool Active;
    }

    readonly Dictionary<string, Endpoint> endpoints = [];
    readonly List<Tracked> sessions = [];
    HashSet<uint> reported = [];

    /// <summary>Follows the set of active outputs (call whenever devices change).</summary>
    public void Sync()
    {
        enumerator.EnumAudioEndpoints(ComApi.ERender, ComApi.DeviceStateActive, out var col);
        col.GetCount(out var n);
        var live = new HashSet<string>();
        for (uint i = 0; i < n; i++)
        {
            col.Item(i, out var device);
            device.GetId(out var id);
            live.Add(id);
            if (endpoints.ContainsKey(id)) continue;
            try
            {
                var manager = ComApi.Activate<IAudioSessionManager2>(device);
                var created = new Created(raw =>
                {
                    // Runs on a Core Audio thread with a borrowed pointer: take our own reference, then hop over.
                    var c = (IAudioSessionControl2)Wrappers.GetOrCreateObjectForComInstance(raw, CreateObjectFlags.None);
                    post(() => Track(id, c));
                });
                manager.RegisterSessionNotification(created);
                // Notifications only start once the enumerator has been fetched; it also gives the existing sessions.
                manager.GetSessionEnumerator(out var existing);
                existing.GetCount(out var count);
                for (var k = 0; k < count; k++)
                {
                    existing.GetSession(k, out var raw);
                    Track(id, ComApi.Wrap<IAudioSessionControl2>(raw));
                }
                endpoints[id] = new Endpoint(manager, created);
            }
            catch (COMException ex) { Log.Warn($"sessions: can't watch an output: {ex.Message}"); }
        }

        foreach (var gone in endpoints.Keys.Where(k => !live.Contains(k)).ToList())
        {
            var e = endpoints[gone];
            endpoints.Remove(gone);
            try { e.Manager.UnregisterSessionNotification(e.Created); } catch (COMException) { }
            foreach (var t in sessions.Where(s => s.Endpoint == gone).ToList()) Untrack(t);
        }
        Report();
    }

    void Track(string endpoint, IAudioSessionControl2 c)
    {
        try
        {
            if (c.IsSystemSoundsSession() == 0) return;
            c.GetProcessId(out var pid);
            if (pid == 0 || pid == OwnPid || sessions.Any(s => ReferenceEquals(s.Control, c))) return;
            c.GetState(out var state);
            var t = new Tracked(endpoint, c, pid) { Active = state == 1 };
            t.Events = new Events(s => post(() => OnState(t, s)));
            c.RegisterAudioSessionNotification(t.Events);
            sessions.Add(t);
            Report();
        }
        catch (COMException) { }
    }

    void OnState(Tracked t, int state)
    {
        if (!sessions.Contains(t)) return;
        if (state == 2 /* expired */) { Untrack(t); Report(); return; }
        t.Active = state == 1;
        Report();
    }

    void Untrack(Tracked t)
    {
        sessions.Remove(t);
        if (t.Events is not null) try { t.Control.UnregisterAudioSessionNotification(t.Events); } catch (COMException) { }
    }

    void Report()
    {
        var active = sessions.Where(s => s.Active).Select(s => s.Pid).ToHashSet();
        if (active.SetEquals(reported)) return;
        reported = active;
        onChange(active);
    }

    [GeneratedComClass]
    internal sealed partial class Created(Action<nint> onCreated) : IAudioSessionNotification
    {
        public void OnSessionCreated(nint newSession) => onCreated(newSession);
    }

    [GeneratedComClass]
    internal sealed partial class Events(Action<int> onState) : IAudioSessionEvents
    {
        public void OnStateChanged(int state) => onState(state);
        public void OnSessionDisconnected(int reason) => onState(2);
        public void OnDisplayNameChanged(string name, in Guid context) { }
        public void OnIconPathChanged(string path, in Guid context) { }
        public void OnSimpleVolumeChanged(float volume, int mute, in Guid context) { }
        public void OnChannelVolumeChanged(uint count, nint volumes, uint changed, in Guid context) { }
        public void OnGroupingParamChanged(in Guid grouping, in Guid context) { }
    }
}
