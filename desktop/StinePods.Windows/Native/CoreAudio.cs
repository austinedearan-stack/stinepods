using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace StinePods.Windows.Native;

// Core Audio / KS / audio-session COM interfaces, source-generated so they work under NativeAOT.
// Vtable order matters: every slot up to the last one we call is declared, unused ones as placeholders (_n).

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    void GetDevice(string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IMMNotificationClient client);
    void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[GeneratedComInterface, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    void GetCount(out uint count);
    void Item(uint index, out IMMDevice device);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    void Activate(in Guid iid, int clsCtx, nint activationParams, out nint iface);
    void OpenPropertyStore(int access, out IPropertyStore store);
    void GetId(out string id);
    void GetState(out int state);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
internal partial interface IMMNotificationClient
{
    void OnDeviceStateChanged(string id, int newState);
    void OnDeviceAdded(string id);
    void OnDeviceRemoved(string id);
    void OnDefaultDeviceChanged(int flow, int role, string? defaultId);
    void OnPropertyValueChanged(string id, PropertyKey key);
}

[GeneratedComInterface, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    void GetCount(out uint count);
    void GetAt(uint index, out PropertyKey key);
    void GetValue(in PropertyKey key, out PropVariant value);
}

[GeneratedComInterface, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F")]
internal partial interface IDeviceTopology
{
    void GetConnectorCount(out uint count);
    void GetConnector(uint index, out IConnector connector);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E")]
internal partial interface IConnector
{
    void GetConnectorType(out int type);
    void GetDataFlow(out int flow);
    void _ConnectTo();
    void _Disconnect();
    void IsConnected(out int connected);
    void GetConnectedTo(out nint other);
    void GetConnectorIdConnectedTo(out string id);
    void GetDeviceIdConnectedTo(out string id);
}

[GeneratedComInterface, Guid("28F54685-06FD-11D2-B27A-00A0C9223196")]
internal partial interface IKsControl
{
    [PreserveSig]
    int KsProperty(in KsProperty property, uint propertyLength, nint data, uint dataLength, out uint bytesReturned);
}

[GeneratedComInterface, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
internal partial interface IAudioSessionManager2
{
    void _GetAudioSessionControl();
    void _GetSimpleAudioVolume();
    void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    void RegisterSessionNotification(IAudioSessionNotification notification);
    void UnregisterSessionNotification(IAudioSessionNotification notification);
}

[GeneratedComInterface, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
internal partial interface IAudioSessionNotification
{
    void OnSessionCreated(nint newSession);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8")]
internal partial interface IAudioSessionEvents
{
    void OnDisplayNameChanged(string name, in Guid context);
    void OnIconPathChanged(string path, in Guid context);
    void OnSimpleVolumeChanged(float volume, int mute, in Guid context);
    void OnChannelVolumeChanged(uint count, nint volumes, uint changed, in Guid context);
    void OnGroupingParamChanged(in Guid grouping, in Guid context);
    void OnStateChanged(int state);
    void OnSessionDisconnected(int reason);
}

[GeneratedComInterface, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
internal partial interface IAudioSessionEnumerator
{
    void GetCount(out int count);
    void GetSession(int index, out nint session);
}

[GeneratedComInterface, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
internal partial interface IAudioSessionControl2
{
    void GetState(out int state);   // 0 inactive, 1 active, 2 expired
    void _GetDisplayName();
    void _SetDisplayName();
    void _GetIconPath();
    void _SetIconPath();
    void _GetGroupingParam();
    void _SetGroupingParam();
    void RegisterAudioSessionNotification(IAudioSessionEvents events);
    void UnregisterAudioSessionNotification(IAudioSessionEvents events);
    void _GetSessionIdentifier();
    void _GetSessionInstanceIdentifier();
    [PreserveSig] int GetProcessId(out uint pid);   // AUDCLNT_S_NO_SINGLE_PROCESS is a success code
    [PreserveSig] int IsSystemSoundsSession();      // S_OK = system sounds, S_FALSE = not
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(Guid fmtid, int pid)
{
    public Guid FmtId = fmtid;
    public int Pid = pid;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public nint Ptr;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KsProperty
{
    public Guid Set;
    public uint Id;
    public uint Flags;
}

internal static partial class ComApi
{
    public const int ClsctxAll = 0x17;
    public const int ERender = 0, ECapture = 1, EAll = 2, EMultimedia = 1;
    public const int DeviceStateActive = 1, DeviceStateNotPresent = 4, DeviceStateUnplugged = 8, DeviceStateAll = 0xF;

    public static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly PropertyKey FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    // KSPROPSETID_BtAudio (ksmedia.h): ONESHOT_RECONNECT = 0, ONESHOT_DISCONNECT = 1.
    public static readonly Guid KsPropSetIdBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    public const uint OneShotReconnect = 0, OneShotDisconnect = 1, KsPropertyTypeGet = 1;

    static readonly StrategyBasedComWrappers Wrappers = new();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, int clsCtx, in Guid iid, out nint ppv);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    internal static partial int PropVariantClear(ref PropVariant pv);

    /// <summary>Joins the multithreaded apartment on the calling thread (no-op if it already did).</summary>
    public static void EnsureMta() => CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);

    public static T Create<T>(Guid clsid) where T : class
    {
        EnsureMta();
        Marshal.ThrowExceptionForHR(CoCreateInstance(clsid, 0, ClsctxAll, typeof(T).GUID, out var p));
        return Wrap<T>(p);
    }

    /// <summary>Wraps a raw interface pointer (taking over its reference) as a generated COM interface.</summary>
    public static T Wrap<T>(nint p) where T : class
    {
        try { return (T)Wrappers.GetOrCreateObjectForComInstance(p, CreateObjectFlags.None); }
        finally { Marshal.Release(p); }
    }

    public static T Activate<T>(IMMDevice device) where T : class
    {
        device.Activate(typeof(T).GUID, ClsctxAll, 0, out var p);
        return Wrap<T>(p);
    }
}
