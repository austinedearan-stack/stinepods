// Phase 0 spike S2: how fast can Windows release/take a Bluetooth headset, and what does
// auto-reconnect look like? Uses the BT audio driver's KS one-shot properties.
//
// usage: s2 list
//        s2 release <name-part> [seconds]   ONESHOT_DISCONNECT on every BT endpoint of the headset
//        s2 take    <name-part> [seconds]   ONESHOT_RECONNECT on the stereo endpoint
//        s2 watch   <name-part> [seconds]   just log events (observe auto-reconnect)

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;

var cmd = args.ElementAtOrDefault(0) ?? "list";
var match = args.ElementAtOrDefault(1) ?? "";
var seconds = int.TryParse(args.ElementAtOrDefault(2), out var s) ? s : 6;

// Watchdog: native BT/audio calls can hang indefinitely; never let a spike run wedge a test batch.
var watchdog = new Timer(_ => { Console.WriteLine("WATCHDOG: hung, exiting"); Environment.Exit(3); }, null, TimeSpan.FromSeconds(seconds + 10), Timeout.InfiniteTimeSpan);

var sw = Stopwatch.StartNew();
void Log(string m) => Console.WriteLine($"+{sw.ElapsedMilliseconds,5}ms {m}");

var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
var all = Endpoints().ToList();
var bt = all.Where(e => e.FilterId.Contains("bthenum", StringComparison.OrdinalIgnoreCase)
                     || e.FilterId.Contains("bthhfenum", StringComparison.OrdinalIgnoreCase)).ToList();

if (cmd == "list")
{
    foreach (var e in bt) Console.WriteLine($"{(e.Flow == CoreAudio.ERender ? "out" : "in "),-3} {CoreAudio.StateName(e.State),-10} {e.Name}\n    filter={e.FilterId}");
    Console.WriteLine($"default output: {DefaultOutputName()}");
    return;
}

// NOTPRESENT endpoints are stale leftovers from earlier pairings.
var mine = bt.Where(e => e.State != 4 && e.Name.Contains(match, StringComparison.OrdinalIgnoreCase)).ToList();
if (mine.Count == 0) { Console.WriteLine($"no BT endpoint matches '{match}'"); return; }

// Bluetooth link (ACL) status, from the stereo filter path: ...&414283b993b1_c00000000...
var mac = mine.Select(e => Regex.Match(e.FilterId, @"&([0-9a-f]{12})_c0", RegexOptions.IgnoreCase)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
BluetoothDevice? device = mac is null ? null : await BluetoothDevice.FromBluetoothAddressAsync(Convert.ToUInt64(mac, 16));
// Completes when the operation's goal is reached: link down for release, stereo ACTIVE for take.
var goal = new TaskCompletionSource();
if (device is not null)
{
    Log($"BT link initially {device.ConnectionStatus} ({device.Name} {mac})");
    device.ConnectionStatusChanged += (d, _) =>
    {
        Log($"BT link -> {d.ConnectionStatus}");
        if (cmd.StartsWith("release") && d.ConnectionStatus == BluetoothConnectionStatus.Disconnected) goal.TrySetResult();
    };
}

// Windows can re-create a headset's endpoint under a new id on reconnect, so match events by name, not id.
string? Mine(string id)
{
    try
    {
        enumerator.GetDevice(id, out var d);
        var n = Name(d);
        return n.Contains(match, StringComparison.OrdinalIgnoreCase) ? n : null;
    }
    catch (COMException) { return null; }
}
var client = new Notifications(Log, Mine, DefaultOutputName, (name, state) =>
{
    if (cmd.StartsWith("take") && state == 1 && name.Contains("Stereo")) goal.TrySetResult();
});
enumerator.RegisterEndpointNotificationCallback(client);
foreach (var e in mine) Log($"{e.Name}: {CoreAudio.StateName(e.State)}");

sw.Restart();
switch (cmd)
{
    case "release":          // hands-free first, then stereo
    case "release-stereo":   // stereo first, then hands-free
    case "release-stereo-only":
        var stereoFirst = cmd != "release";
        var order = mine.Where(e => cmd != "release-stereo-only" || !IsHandsFree(e))
                        .OrderBy(e => IsHandsFree(e) ^ stereoFirst ? 0 : 1)
                        .DistinctBy(e => e.FilterId);
        foreach (var e in order)
            Log($"ONESHOT_DISCONNECT via {e.Name} -> 0x{OneShot(e, CoreAudio.OneShotDisconnect):X8}");
        break;
    case "release-hci":        // drop the link only
    case "release-fast":       // graceful profile disconnects, then drop the link right away
        if (mac is null) { Console.WriteLine("no BT address for this headset"); return; }
        if (cmd == "release-fast")
            foreach (var e in mine.OrderBy(e => IsHandsFree(e) ? 1 : 0).DistinctBy(e => e.FilterId))
                Log($"ONESHOT_DISCONNECT via {e.Name} -> 0x{OneShot(e, CoreAudio.OneShotDisconnect):X8}");
        Log($"IOCTL_BTH_DISCONNECT_DEVICE -> {BthRadio.Disconnect(Convert.ToUInt64(mac, 16)) ?? "ok"}");
        break;
    case "take-sdp":
        // An uncached SDP query pages the headset through a different path; the link then exists for the reconnect.
        if (device is null) { Console.WriteLine("no BT device"); return; }
        _ = device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached).AsTask()
                  .ContinueWith(t => Log($"SDP query finished: {(t.IsFaulted ? t.Exception!.InnerException!.Message : t.Result.Error.ToString())}"));
        Log("SDP query started");
        goto case "take";
    case "take":
        var stereo = mine.FirstOrDefault(e => e.Flow == CoreAudio.ERender && e.FilterId.Contains("bthenum", StringComparison.OrdinalIgnoreCase)) ?? mine[0];
        Log($"ONESHOT_RECONNECT via {stereo.Name} -> 0x{OneShot(stereo, CoreAudio.OneShotReconnect):X8}");
        break;
    case "watch":
        Log("watching...");
        break;
    default:
        Console.WriteLine($"unknown command {cmd}");
        return;
}

await Task.WhenAny(goal.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
if (goal.Task.IsCompleted) Log("goal reached");
Log($"done. default output: {DefaultOutputName()}; BT link {device?.ConnectionStatus}");
enumerator.UnregisterEndpointNotificationCallback(client);
GC.KeepAlive(watchdog);

static bool IsHandsFree(Endpoint e) => e.FilterId.Contains("bthhfenum", StringComparison.OrdinalIgnoreCase);

int OneShot(Endpoint e, uint id)
{
    // The endpoint's connector 0 is wired to the BT audio KS filter; IKsControl lives on that filter device.
    var topoIid = typeof(IDeviceTopology).GUID;
    e.Device.Activate(ref topoIid, CoreAudio.ClsctxAll, IntPtr.Zero, out var topoObj);
    ((IDeviceTopology)topoObj).GetConnector(0, out var connector);
    connector.GetDeviceIdConnectedTo(out var filterDeviceId);
    enumerator.GetDevice(filterDeviceId, out var filter);
    var ksIid = typeof(IKsControl).GUID;
    filter.Activate(ref ksIid, CoreAudio.ClsctxAll, IntPtr.Zero, out var ksObj);

    var prop = new KsProperty { Set = CoreAudio.KsPropSetIdBtAudio, Id = id, Flags = CoreAudio.KsPropertyTypeGet };
    var size = Marshal.SizeOf<KsProperty>();
    var p = Marshal.AllocHGlobal(size);
    try
    {
        Marshal.StructureToPtr(prop, p, false);
        return ((IKsControl)ksObj).KsProperty(p, (uint)size, IntPtr.Zero, 0, out _);
    }
    finally { Marshal.FreeHGlobal(p); }
}

IEnumerable<Endpoint> Endpoints()
{
    enumerator.EnumAudioEndpoints(CoreAudio.EAll, CoreAudio.DeviceStateAll, out var col);
    col.GetCount(out var n);
    for (uint i = 0; i < n; i++)
    {
        col.Item(i, out var d);
        d.GetId(out var id);
        d.GetState(out var state);
        var name = Name(d);
        var flow = id.StartsWith("{0.0.0.") ? CoreAudio.ERender : CoreAudio.ECapture;
        string filter = "";
        try
        {
            var iid = typeof(IDeviceTopology).GUID;
            d.Activate(ref iid, CoreAudio.ClsctxAll, IntPtr.Zero, out var t);
            ((IDeviceTopology)t).GetConnector(0, out var c);
            c.GetDeviceIdConnectedTo(out filter);
        }
        catch (COMException) { }
        yield return new Endpoint(id, name, flow, state, filter, d);
    }
}

string Name(IMMDevice d)
{
    // Endpoints being torn down or re-created mid-switch can fail property reads.
    try
    {
        d.OpenPropertyStore(0, out var store);
        var key = CoreAudio.FriendlyName;
        store.GetValue(ref key, out var v);
        return v.AsString() ?? "?";
    }
    catch (COMException) { return "?"; }
}

string DefaultOutputName()
{
    try
    {
        enumerator.GetDefaultAudioEndpoint(CoreAudio.ERender, CoreAudio.EMultimedia, out var d);
        return Name(d);
    }
    catch (COMException) { return "(none)"; }
}

record Endpoint(string Id, string Name, int Flow, int State, string FilterId, IMMDevice Device);

[ComVisible(true)]
class Notifications(Action<string> log, Func<string, string?> mine, Func<string> defaultName, Action<string, int> onState) : IMMNotificationClient
{
    public void OnDeviceStateChanged(string id, int newState)
    {
        if (mine(id) is not { } name) return;
        log($"{name} [{id[^8..]}] -> {CoreAudio.StateName(newState)}");
        onState(name, newState);
    }

    public void OnDefaultDeviceChanged(int flow, int role, string? defaultId)
    {
        if (flow == CoreAudio.ERender && role == CoreAudio.EMultimedia) log($"default output -> {defaultName()}");
    }

    public void OnDeviceAdded(string id) { }
    public void OnDeviceRemoved(string id) { }
    public void OnPropertyValueChanged(string id, PropertyKey key) { }
}
