using System.Runtime.InteropServices;

namespace StinePods.Windows.Native;

/// <summary>Bluetooth radio, Winsock RFCOMM, shell tray, user32 and DPAPI imports.</summary>
internal static unsafe partial class Win32
{
    // ---- Bluetooth radio ----

    // CTL_CODE(FILE_DEVICE_BLUETOOTH = 0x41, BTH_IOCTL_BASE + 0x03, METHOD_BUFFERED, FILE_ANY_ACCESS)
    public const uint IoctlBthDisconnectDevice = 0x41000C;

    [StructLayout(LayoutKind.Sequential)]
    public struct FindRadioParams { public int Size; }

    [LibraryImport("bluetoothapis.dll", SetLastError = true)]
    public static partial nint BluetoothFindFirstRadio(ref FindRadioParams p, out nint radio);

    [LibraryImport("bluetoothapis.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BluetoothFindRadioClose(nint find);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(nint h, uint code, ref ulong inBuf, int inSize, nint outBuf, int outSize, out int returned, nint overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint h);

    // ---- Winsock (AF_BTH / RFCOMM) ----

    public const ushort AfBth = 32;
    public const int SockStream = 1, BthProtoRfcomm = 3, NsBth = 16;
    public const uint BtPortAny = uint.MaxValue;
    public static readonly nint InvalidSocket = -1;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SockaddrBth
    {
        public ushort AddressFamily;
        public ulong BtAddr;
        public Guid ServiceClassId;
        public uint Port;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SocketAddress { public nint Sockaddr; public int Length; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CsAddrInfo
    {
        public SocketAddress LocalAddr;
        public SocketAddress RemoteAddr;
        public int SocketType;
        public int Protocol;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WsaQuerySet
    {
        public int Size;
        public nint ServiceInstanceName;
        public nint ServiceClassId;
        public nint Version;
        public nint Comment;
        public int NameSpace;
        public nint NsProviderId;
        public nint Context;
        public int NumberOfProtocols;
        public nint AfpProtocols;
        public nint QueryString;
        public int NumberOfCsAddrs;
        public nint CsaBuffer;
        public int OutputFlags;
        public nint Blob;
    }

    [LibraryImport("ws2_32.dll")]
    public static partial int WSAStartup(ushort version, byte* data);

    [LibraryImport("ws2_32.dll")]
    public static partial int WSAGetLastError();

    [LibraryImport("ws2_32.dll", SetLastError = true)]
    public static partial nint socket(int af, int type, int protocol);

    [LibraryImport("ws2_32.dll")]
    public static partial int bind(nint s, ref SockaddrBth addr, int len);

    [LibraryImport("ws2_32.dll")]
    public static partial int getsockname(nint s, ref SockaddrBth addr, ref int len);

    [LibraryImport("ws2_32.dll")]
    public static partial int listen(nint s, int backlog);

    [LibraryImport("ws2_32.dll")]
    public static partial nint accept(nint s, ref SockaddrBth addr, ref int len);

    [LibraryImport("ws2_32.dll")]
    public static partial int recv(nint s, byte* buf, int len, int flags);

    [LibraryImport("ws2_32.dll")]
    public static partial int send(nint s, byte* buf, int len, int flags);

    [LibraryImport("ws2_32.dll")]
    public static partial int closesocket(nint s);

    [LibraryImport("ws2_32.dll", EntryPoint = "WSASetServiceW")]
    public static partial int WSASetService(ref WsaQuerySet qs, int op, int flags);

    // ---- Shell tray icon ----

    public const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    public const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10, NifShowTip = 0x80;
    public const uint NiifInfo = 1, NiifWarning = 2;
    public const uint NotifyIconVersion4 = 4;
    public const int NinBalloonUserClick = 0x0405, NinBalloonTimeout = 0x0404;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public int Size;
        public nint Wnd;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint VersionOrTimeout;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    public static void Copy(string s, char* dst, int capacity)
    {
        var n = Math.Min(s.Length, capacity - 1);
        for (var i = 0; i < n; i++) dst[i] = s[i];
        dst[n] = '\0';
    }

    // ---- user32 ----

    public const uint WmDestroy = 0x0002, WmCommand = 0x0111, WmApp = 0x8000, WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmContextMenu = 0x007B;
    public const uint MfString = 0, MfSeparator = 0x800, MfGrayed = 1, MfChecked = 8;
    public const uint TpmRightButton = 2, TpmReturnCmd = 0x100;
    public const uint MbYesNo = 4, MbIconQuestion = 0x20, MbTopMost = 0x40000, MbSetForeground = 0x10000, MbOk = 0, MbIconInfo = 0x40;
    public const int IdYes = 6;
    public const int IdiApplication = 32512;

    [StructLayout(LayoutKind.Sequential)]
    public struct WndClassEx
    {
        public int Size;
        public uint Style;
        public nint WndProc;
        public int ClsExtra;
        public int WndExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
        public nint IconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public nint Wnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X, Y; }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    public static partial ushort RegisterClassEx(ref WndClassEx wc);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint wnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out Msg msg, nint wnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in Msg msg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(in Msg msg);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint wnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int code);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll")]
    public static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(nint menu, uint flags, nint id, string? text);

    [LibraryImport("user32.dll")]
    public static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint wnd, nint rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point p);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint wnd);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    public static partial nint LoadIcon(nint instance, nint name);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(nint wnd, string text, string caption, uint type);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    public static partial nint GetModuleHandle(nint name);

    // ---- DPAPI ----

    [StructLayout(LayoutKind.Sequential)]
    public struct DataBlob { public int Size; public nint Data; }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptProtectData(in DataBlob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out DataBlob output);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptUnprotectData(in DataBlob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out DataBlob output);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint mem);

    /// <summary>DPAPI-protects data for the current Windows user.</summary>
    public static byte[] Protect(byte[] data, bool protect)
    {
        fixed (byte* p = data)
        {
            var input = new DataBlob { Size = data.Length, Data = (nint)p };
            var ok = protect
                ? CryptProtectData(input, 0, 0, 0, 0, 1 /* UI_FORBIDDEN */, out var output)
                : CryptUnprotectData(input, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            try { return new ReadOnlySpan<byte>((void*)output.Data, output.Size).ToArray(); }
            finally { LocalFree(output.Data); }
        }
    }
}
