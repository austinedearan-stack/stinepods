using System.ComponentModel;
using System.Runtime.InteropServices;

// Drops the Bluetooth link (ACL) to a remote device directly, instead of waiting for Windows'
// idle timer after the audio profiles disconnect. IOCTL_BTH_DISCONNECT_DEVICE from bthioctl.h.
static class BthRadio
{
    // CTL_CODE(FILE_DEVICE_BLUETOOTH = 0x41, BTH_IOCTL_BASE + 0x03, METHOD_BUFFERED, FILE_ANY_ACCESS)
    const uint IoctlBthDisconnectDevice = 0x41000C;

    [StructLayout(LayoutKind.Sequential)]
    struct FindRadioParams { public int Size; }

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    static extern IntPtr BluetoothFindFirstRadio(ref FindRadioParams p, out IntPtr radio);

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    static extern bool BluetoothFindRadioClose(IntPtr find);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, ref ulong inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    /// <summary>Returns null on success, or the Win32 error message.</summary>
    public static string? Disconnect(ulong address)
    {
        var p = new FindRadioParams { Size = Marshal.SizeOf<FindRadioParams>() };
        var find = BluetoothFindFirstRadio(ref p, out var radio);
        if (find == IntPtr.Zero) return "no radio: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
        try
        {
            return DeviceIoControl(radio, IoctlBthDisconnectDevice, ref address, sizeof(ulong), IntPtr.Zero, 0, out _, IntPtr.Zero)
                ? null
                : new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }
        finally
        {
            CloseHandle(radio);
            BluetoothFindRadioClose(find);
        }
    }
}
