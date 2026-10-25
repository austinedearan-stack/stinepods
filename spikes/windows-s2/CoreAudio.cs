using System.Runtime.InteropServices;

// Minimal Core Audio / KS COM declarations for the S2 spike.
// Vtable order matters: every method up to the last one we call must be declared, in order.

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorCom { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IMMNotificationClient client);
    void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    void GetCount(out uint count);
    void Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    void OpenPropertyStore(int access, out IPropertyStore store);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out int state);
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int newState);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultId);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    void GetCount(out uint count);
    void GetAt(uint index, out PropertyKey key);
    void GetValue(ref PropertyKey key, out PropVariant value);
}

[ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDeviceTopology
{
    void GetConnectorCount(out uint count);
    void GetConnector(uint index, out IConnector connector);
}

[ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IConnector
{
    void GetType(out int type);
    void GetDataFlow(out int flow);
    void ConnectTo(IConnector other);
    void Disconnect();
    void IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
    void GetConnectedTo(out IConnector other);
    void GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IKsControl
{
    [PreserveSig]
    int KsProperty(IntPtr property, uint propertyLength, IntPtr data, uint dataLength, out uint bytesReturned);
}

[StructLayout(LayoutKind.Sequential)]
struct PropertyKey(Guid fmtid, int pid)
{
    public Guid FmtId = fmtid;
    public int Pid = pid;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
struct PropVariant
{
    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public IntPtr Ptr;

    public readonly string? AsString() => Vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(Ptr) : null;
}

[StructLayout(LayoutKind.Sequential)]
struct KsProperty
{
    public Guid Set;
    public uint Id;
    public uint Flags;
}

static class CoreAudio
{
    public const int ClsctxAll = 0x17;
    public const int ERender = 0, ECapture = 1, EAll = 2;
    public const int EMultimedia = 1;
    public const int DeviceStateAll = 0xF;

    public static readonly PropertyKey FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    // KSPROPSETID_BtAudio (ksmedia.h): ONESHOT_RECONNECT = 0, ONESHOT_DISCONNECT = 1.
    public static readonly Guid KsPropSetIdBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    public const uint OneShotReconnect = 0, OneShotDisconnect = 1;
    public const uint KsPropertyTypeGet = 1;

    public static string StateName(int s) => s switch
    {
        1 => "ACTIVE",
        2 => "DISABLED",
        4 => "NOTPRESENT",
        8 => "UNPLUGGED",
        _ => s.ToString(),
    };
}
