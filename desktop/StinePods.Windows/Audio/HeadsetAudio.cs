using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text.RegularExpressions;
using StinePods.Windows.Native;

namespace StinePods.Windows.Audio;

/// <summary>
/// Bluetooth headsets as Windows sees them through Core Audio (docs/spikes/S2-windows-connect.md):
/// presence via the A2DP stereo endpoint, take via the BT audio driver's KS one-shot reconnect, release via
/// IOCTL_BTH_DISCONNECT_DEVICE. All members must be called on the audio worker thread.
/// </summary>
internal sealed partial class HeadsetAudio
{
    readonly IMMDeviceEnumerator enumerator;
    readonly Notifications client;
    readonly Action<string, bool> onLink;
    readonly Dictionary<string, bool> here = [];
    HashSet<string> watched = [];

    readonly SessionMonitor sessions;

    /// <param name="onLink">(headset address, connected here) whenever a watched headset's state changes.</param>
    /// <param name="onActivePids">Processes currently producing sound, whenever that set changes.</param>
    /// <param name="post">Queues work onto the audio worker thread (COM callbacks must not call back in directly).</param>
    public HeadsetAudio(Action<string, bool> onLink, Action<IReadOnlySet<uint>> onActivePids, Action<Action> post)
    {
        this.onLink = onLink;
        enumerator = ComApi.Create<IMMDeviceEnumerator>(ComApi.ClsidMMDeviceEnumerator);
        sessions = new SessionMonitor(enumerator, onActivePids, post);
        client = new Notifications(() => post(Rescan));
        enumerator.RegisterEndpointNotificationCallback(client);
    }

    public void Watch(IEnumerable<string> headsets)
    {
        watched = [.. headsets.Select(h => h.ToUpperInvariant())];
        foreach (var h in here.Keys.Where(h => !watched.Contains(h)).ToList()) here.Remove(h);
        Rescan();
    }

    public bool IsHere(string h) => here.GetValueOrDefault(h);

    /// <summary>Re-reads endpoint states and reports watched headsets whose presence changed.</summary>
    public void Rescan()
    {
        sessions.Sync();
        var now = watched.ToDictionary(h => h, _ => false);
        foreach (var e in StereoEndpoints())
            if (now.ContainsKey(e.Address) && e.State == ComApi.DeviceStateActive) now[e.Address] = true;
        foreach (var (h, isHere) in now)
        {
            if (here.TryGetValue(h, out var was) && was == isHere) continue;
            here[h] = isHere;
            onLink(h, isHere);
        }
    }

    /// <summary>Starts connecting the headset to this PC (completion is reported through Rescan).</summary>
    public void Take(string h)
    {
        // Windows can re-create the endpoint under a new id, so resolve it fresh each time.
        var e = StereoEndpoints().FirstOrDefault(x => x.Address == h && x.State != ComApi.DeviceStateNotPresent)
            ?? throw new InvalidOperationException($"no stereo endpoint for {h}");
        var hr = OneShot(e.Device, ComApi.OneShotReconnect);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    }

    /// <summary>Drops the headset's Bluetooth link to this PC immediately (both A2DP and hands-free).</summary>
    public static void Release(string h)
    {
        var p = new Win32.FindRadioParams { Size = Marshal.SizeOf<Win32.FindRadioParams>() };
        var find = Win32.BluetoothFindFirstRadio(ref p, out var radio);
        if (find == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "no Bluetooth radio");
        try
        {
            var addr = Convert.ToUInt64(h.Replace(":", ""), 16);
            if (!Win32.DeviceIoControl(radio, Win32.IoctlBthDisconnectDevice, ref addr, sizeof(ulong), 0, 0, out _, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "disconnect failed");
        }
        finally
        {
            Win32.CloseHandle(radio);
            Win32.BluetoothFindRadioClose(find);
        }
    }

    /// <summary>Whether any of these processes has an active audio session on any active output right now.</summary>
    /// <remarks>Not just the default output: apps (browsers especially) can play to another device.</remarks>
    public bool IsAudible(IReadOnlySet<uint> pids)
    {
        if (pids.Count == 0) return false;
        enumerator.EnumAudioEndpoints(ComApi.ERender, ComApi.DeviceStateActive, out var col);
        col.GetCount(out var count);
        for (uint d = 0; d < count; d++)
        {
            col.Item(d, out var device);
            var manager = ComApi.Activate<IAudioSessionManager2>(device);
            manager.GetSessionEnumerator(out var sessions);
            sessions.GetCount(out var n);
            for (var i = 0; i < n; i++)
            {
                sessions.GetSession(i, out var raw);
                var s = ComApi.Wrap<IAudioSessionControl2>(raw);
                s.GetState(out var state);
                if (state != 1) continue;
                s.GetProcessId(out var pid);
                if (pids.Contains(pid)) return true;
            }
        }
        return false;
    }

    /// <summary>Friendly name of the headset's stereo endpoint, e.g. "Headphones (ABJ-TW3 Stereo)".</summary>
    public string? NameOf(string h) => StereoEndpoints().FirstOrDefault(e => e.Address == h)?.Name;

    sealed record Endpoint(string Address, int State, string Name, IMMDevice Device);

    // A2DP sink filter path: ...bthenum#{0000110b-...}_...&414283b993b1_c00000000#...
    [GeneratedRegex(@"bthenum#\{0000110b-[^}]+\}[^#]*#[^#]*&([0-9a-f]{12})_c0", RegexOptions.IgnoreCase)]
    private static partial Regex StereoFilter();

    IEnumerable<Endpoint> StereoEndpoints()
    {
        enumerator.EnumAudioEndpoints(ComApi.ERender, ComApi.DeviceStateAll, out var col);
        col.GetCount(out var n);
        for (uint i = 0; i < n; i++)
        {
            col.Item(i, out var d);
            var filter = FilterId(d);
            if (filter is null || StereoFilter().Match(filter) is not { Success: true } m) continue;
            d.GetState(out var state);
            var hex = m.Groups[1].Value.ToUpperInvariant();
            var address = string.Join(':', Enumerable.Range(0, 6).Select(k => hex.Substring(k * 2, 2)));
            yield return new Endpoint(address, state, Name(d) ?? "?", d);
        }
    }

    static string? FilterId(IMMDevice d)
    {
        try
        {
            var topo = ComApi.Activate<IDeviceTopology>(d);
            topo.GetConnector(0, out var c);
            c.GetDeviceIdConnectedTo(out var id);
            return id;
        }
        catch (COMException) { return null; }
    }

    static string? Name(IMMDevice d)
    {
        // Property reads can fail while an endpoint is being torn down mid-switch.
        try
        {
            d.OpenPropertyStore(0, out var store);
            store.GetValue(ComApi.FriendlyName, out var v);
            try { return v.Vt == 31 ? Marshal.PtrToStringUni(v.Ptr) : null; }
            finally { ComApi.PropVariantClear(ref v); }
        }
        catch (COMException) { return null; }
    }

    int OneShot(IMMDevice endpoint, uint id)
    {
        // The endpoint's connector 0 is wired to the BT audio KS filter; IKsControl lives on that filter device.
        var topo = ComApi.Activate<IDeviceTopology>(endpoint);
        topo.GetConnector(0, out var connector);
        connector.GetDeviceIdConnectedTo(out var filterId);
        enumerator.GetDevice(filterId, out var filter);
        var ks = ComApi.Activate<IKsControl>(filter);
        var prop = new KsProperty { Set = ComApi.KsPropSetIdBtAudio, Id = id, Flags = ComApi.KsPropertyTypeGet };
        return ks.KsProperty(prop, (uint)Marshal.SizeOf<KsProperty>(), 0, 0, out _);
    }

    [GeneratedComClass]
    internal sealed partial class Notifications(System.Action changed) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string id, int newState) => changed();
        public void OnDeviceAdded(string id) => changed();
        public void OnDeviceRemoved(string id) => changed();
        public void OnDefaultDeviceChanged(int flow, int role, string? defaultId) { }
        public void OnPropertyValueChanged(string id, PropertyKey key) { }
    }
}
