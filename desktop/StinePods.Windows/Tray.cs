using System.Runtime.InteropServices;
using StinePods.Windows.Native;

namespace StinePods.Windows;

/// <summary>
/// Notification-area icon on its own UI thread: status tooltip, a small menu and balloons. Plain Win32, so no UI
/// framework is ever loaded (docs/DECISIONS.md D2).
/// </summary>
internal sealed unsafe class Tray
{
    const uint CallbackMsg = Win32.WmApp + 1, InvokeMsg = Win32.WmApp + 2;
    const int CmdToggle = 1, CmdOpenLog = 2, CmdExit = 3, CmdStartup = 4, CmdOtherAsk = 10, CmdOtherAuto = 11, CmdOtherIgnore = 12;

    static Tray? instance;

    readonly Queue<System.Action> invokes = new();
    readonly Lock gate = new();
    nint wnd, icon;
    uint taskbarCreated;
    string status = "Starting…";
    System.Action? balloonClick;

    public bool Enabled { get; set; } = true;
    public bool StartWithWindows { get; set; }
    /// <summary>Rule for apps without media controls: "ask", "auto" or "ignore".</summary>
    public string OtherRule { get; set; } = "ask";
    public event System.Action? ToggleRequested, ExitRequested, StartupToggled;
    public event Action<string>? OtherRuleChosen;

    public Tray() => instance = this;

    /// <summary>Runs the message loop on a new thread; returns once the icon is up.</summary>
    public void Start()
    {
        using var ready = new ManualResetEventSlim();
        new Thread(() =>
        {
            CreateWindow();
            Add();
            ready.Set();
            while (Win32.GetMessage(out var msg, 0, 0, 0) > 0)
            {
                Win32.TranslateMessage(msg);
                Win32.DispatchMessage(msg);
            }
        }) { IsBackground = true, Name = "tray" }.Start();
        ready.Wait();
    }

    public void SetStatus(string text) => Invoke(() =>
    {
        status = text;
        var d = Data(Win32.NifTip | Win32.NifShowTip);
        Win32.Copy("StinePods: " + text, d.Tip, 128);
        Win32.ShellNotifyIcon(Win32.NimModify, ref d);
    });

    /// <summary>Shows a balloon; <paramref name="onClick"/> runs (on the tray thread) if the user clicks it.</summary>
    public void Balloon(string title, string text, System.Action? onClick = null, bool warning = false) => Invoke(() =>
    {
        balloonClick = onClick;
        var d = Data(Win32.NifInfo);
        Win32.Copy(text, d.Info, 256);
        Win32.Copy(title, d.InfoTitle, 64);
        d.InfoFlags = warning ? Win32.NiifWarning : Win32.NiifInfo;
        Win32.ShellNotifyIcon(Win32.NimModify, ref d);
    });

    public void Remove() => Invoke(() =>
    {
        var d = Data(0);
        Win32.ShellNotifyIcon(Win32.NimDelete, ref d);
        Win32.PostQuitMessage(0);
    });

    void Invoke(System.Action a)
    {
        lock (gate) invokes.Enqueue(a);
        Win32.PostMessage(wnd, InvokeMsg, 0, 0);
    }

    void CreateWindow()
    {
        var hinst = Win32.GetModuleHandle(0);
        icon = Win32.LoadIcon(0, Win32.IdiApplication);
        var cls = Marshal.StringToHGlobalUni("StinePodsTray");
        var wc = new Win32.WndClassEx
        {
            Size = Marshal.SizeOf<Win32.WndClassEx>(),
            WndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
            Instance = hinst,
            ClassName = cls,
        };
        Win32.RegisterClassEx(ref wc);
        wnd = Win32.CreateWindowEx(0, "StinePodsTray", "StinePods", 0, 0, 0, 0, 0, 0, 0, hinst, 0);
        taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
    }

    void Add()
    {
        var d = Data(Win32.NifMessage | Win32.NifIcon | Win32.NifTip | Win32.NifShowTip);
        d.CallbackMessage = CallbackMsg;
        d.Icon = icon;
        Win32.Copy("StinePods: " + status, d.Tip, 128);
        Win32.ShellNotifyIcon(Win32.NimAdd, ref d);
        d.VersionOrTimeout = Win32.NotifyIconVersion4;
        Win32.ShellNotifyIcon(Win32.NimSetVersion, ref d);
    }

    Win32.NotifyIconData Data(uint flags) => new() { Size = sizeof(Win32.NotifyIconData), Wnd = wnd, Id = 1, Flags = flags };

    void ShowMenu()
    {
        var m = Win32.CreatePopupMenu();
        Win32.AppendMenu(m, Win32.MfString | Win32.MfGrayed, 0, status);
        Win32.AppendMenu(m, Win32.MfSeparator, 0, null);
        Win32.AppendMenu(m, Win32.MfString | (Enabled ? Win32.MfChecked : 0), CmdToggle, "Automatic switching");
        Win32.AppendMenu(m, Win32.MfSeparator, 0, null);
        Win32.AppendMenu(m, Win32.MfString | Win32.MfGrayed, 0, "Apps without media controls (VLC, games…):");
        Win32.AppendMenu(m, Win32.MfString | (OtherRule == "ask" ? Win32.MfChecked : 0), CmdOtherAsk, "    Ask me first");
        Win32.AppendMenu(m, Win32.MfString | (OtherRule == "auto" ? Win32.MfChecked : 0), CmdOtherAuto, "    Switch automatically");
        Win32.AppendMenu(m, Win32.MfString | (OtherRule == "ignore" ? Win32.MfChecked : 0), CmdOtherIgnore, "    Never switch");
        Win32.AppendMenu(m, Win32.MfSeparator, 0, null);
        Win32.AppendMenu(m, Win32.MfString | (StartWithWindows ? Win32.MfChecked : 0), CmdStartup, "Start with Windows");
        Win32.AppendMenu(m, Win32.MfString, CmdOpenLog, "Open log");
        Win32.AppendMenu(m, Win32.MfString, CmdExit, "Exit StinePods");
        Win32.GetCursorPos(out var pt);
        Win32.SetForegroundWindow(wnd);
        var cmd = Win32.TrackPopupMenu(m, Win32.TpmRightButton | Win32.TpmReturnCmd, pt.X, pt.Y, 0, wnd, 0);
        Win32.DestroyMenu(m);
        switch (cmd)
        {
            case CmdToggle: ToggleRequested?.Invoke(); break;
            case CmdStartup: StartupToggled?.Invoke(); break;
            case CmdOtherAsk: OtherRuleChosen?.Invoke("ask"); break;
            case CmdOtherAuto: OtherRuleChosen?.Invoke("auto"); break;
            case CmdOtherIgnore: OtherRuleChosen?.Invoke("ignore"); break;
            case CmdOpenLog: System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.Path) { UseShellExecute = true }); break;
            case CmdExit: ExitRequested?.Invoke(); break;
        }
    }

    [UnmanagedCallersOnly]
    static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var t = instance;
        try
        {
            if (t is null) return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
            if (msg == InvokeMsg)
            {
                while (true)
                {
                    System.Action? a;
                    lock (t.gate) if (!t.invokes.TryDequeue(out a)) break;
                    a();
                }
                return 0;
            }
            if (msg == CallbackMsg)
            {
                switch ((int)(lParam & 0xFFFF))
                {
                    case (int)Win32.WmContextMenu or (int)Win32.WmRButtonUp or (int)Win32.WmLButtonUp: t.ShowMenu(); break;
                    case Win32.NinBalloonUserClick: var c = t.balloonClick; t.balloonClick = null; c?.Invoke(); break;
                    case Win32.NinBalloonTimeout: t.balloonClick = null; break;
                }
                return 0;
            }
            if (msg == t.taskbarCreated && msg != 0) { t.Add(); return 0; }   // Explorer restarted
        }
        catch (Exception ex) { Log.Warn($"tray: {ex}"); }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
