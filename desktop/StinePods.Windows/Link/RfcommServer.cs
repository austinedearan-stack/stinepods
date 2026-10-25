using System.ComponentModel;
using System.Runtime.InteropServices;
using StinePods.Windows.Native;

namespace StinePods.Windows.Link;

/// <summary>
/// Bluetooth RFCOMM server for the phone link (protocol/PROTOCOL.md), on plain Winsock so it stays light and
/// NativeAOT-friendly. Registers an SDP record for the StinePods service UUID; accepts one phone at a time.
/// </summary>
internal sealed unsafe class RfcommServer
{
    public static readonly Guid ServiceUuid = new("8198aff7-3480-44df-af7f-bf9bd001588c");
    const uint PreferredChannel = 12;

    readonly Action<Connection> onConnected;
    readonly System.Action onStopped;
    nint listener = Win32.InvalidSocket;
    Win32.SockaddrBth registered;
    bool isRegistered;

    /// <param name="onStopped">The server died (e.g. Bluetooth was switched off); start a new one later.</param>
    public RfcommServer(Action<Connection> onConnected, System.Action onStopped)
    {
        this.onConnected = onConnected;
        this.onStopped = onStopped;
    }

    public void Start()
    {
        var wsa = stackalloc byte[512];
        if (Win32.WSAStartup(0x0202, wsa) != 0) throw new Win32Exception("WSAStartup failed");
        try
        {
            listener = Win32.socket(Win32.AfBth, Win32.SockStream, Win32.BthProtoRfcomm);
            if (listener == Win32.InvalidSocket) throw new Win32Exception(Win32.WSAGetLastError(), "socket failed (Bluetooth off?)");
            // A fixed channel keeps working even if a phone still holds a stale SDP record from an earlier run
            // (seen after an agent restart: the new run got a new channel and the phone kept dialling the old one).
            var addr = new Win32.SockaddrBth { AddressFamily = Win32.AfBth, Port = PreferredChannel };
            var len = Marshal.SizeOf<Win32.SockaddrBth>();
            if (Win32.bind(listener, ref addr, len) != 0)
            {
                Log.Warn($"link: channel {PreferredChannel} unavailable ({Win32.WSAGetLastError()}), trying any free channel");
                addr.Port = Win32.BtPortAny;
                Check(Win32.bind(listener, ref addr, len), "bind");
            }
            Check(Win32.getsockname(listener, ref addr, ref len), "getsockname");
            Check(Win32.listen(listener, 1), "listen");
            SetService(addr, 0 /* RNRSERVICE_REGISTER */);
            registered = addr;
            isRegistered = true;
            Log.Info($"link: listening on RFCOMM channel {addr.Port}");
        }
        catch
        {
            Stop();   // never leave a half-made socket behind (it can block the next attempt)
            throw;
        }
        new Thread(AcceptLoop) { IsBackground = true, Name = "rfcomm-accept" }.Start();
    }

    /// <summary>Withdraws the SDP record and closes the listening socket.</summary>
    public void Stop()
    {
        if (isRegistered)
        {
            try { SetService(registered, 2 /* RNRSERVICE_DELETE */); } catch (Win32Exception) { }
            isRegistered = false;
        }
        if (listener != Win32.InvalidSocket)
        {
            Win32.closesocket(listener);
            listener = Win32.InvalidSocket;
        }
    }

    void SetService(Win32.SockaddrBth local, int op)
    {
        // The SDP record points phones that look up ServiceUuid at our channel.
        var uuid = ServiceUuid;
        var name = Marshal.StringToHGlobalUni("StinePods");
        var sockaddr = Marshal.AllocHGlobal(Marshal.SizeOf<Win32.SockaddrBth>());
        try
        {
            Marshal.StructureToPtr(local, sockaddr, false);
            var csa = new Win32.CsAddrInfo
            {
                LocalAddr = new Win32.SocketAddress { Sockaddr = sockaddr, Length = Marshal.SizeOf<Win32.SockaddrBth>() },
                SocketType = Win32.SockStream,
                Protocol = Win32.BthProtoRfcomm,
            };
            var qs = new Win32.WsaQuerySet
            {
                Size = Marshal.SizeOf<Win32.WsaQuerySet>(),
                ServiceInstanceName = name,
                ServiceClassId = (nint)(&uuid),
                NameSpace = Win32.NsBth,
                NumberOfCsAddrs = 1,
                CsaBuffer = (nint)(&csa),
            };
            Check(Win32.WSASetService(ref qs, op, 0), "WSASetService");
        }
        finally
        {
            Marshal.FreeHGlobal(sockaddr);
            Marshal.FreeHGlobal(name);
        }
    }

    void AcceptLoop()
    {
        while (true)
        {
            var peer = new Win32.SockaddrBth();
            var len = Marshal.SizeOf<Win32.SockaddrBth>();
            var s = Win32.accept(listener, ref peer, ref len);
            if (s == Win32.InvalidSocket)
            {
                // The listening socket is dead (typically Bluetooth was switched off). Tear down and let the owner
                // start a fresh server once the radio is back.
                Log.Warn($"link: listener stopped ({Win32.WSAGetLastError()})");
                Stop();
                onStopped();
                return;
            }
            var c = new Connection(s, peer.BtAddr);
            Log.Info($"link: phone connected from {c.PeerAddress}");
            // Serve this phone until it goes away; one phone at a time.
            onConnected(c);
            c.Closed.Wait();
        }
    }

    static void Check(int rc, string what)
    {
        if (rc != 0) throw new Win32Exception(Win32.WSAGetLastError(), $"{what} failed");
    }

    /// <summary>One accepted phone connection. Reads on its own thread; Send is thread-safe.</summary>
    public sealed class Connection(nint socket, ulong peer)
    {
        readonly Lock sendGate = new();
        readonly ManualResetEventSlim closed = new();
        int isClosed;

        public string PeerAddress { get; } = string.Join(':', BitConverter.GetBytes(peer).Take(6).Reverse().Select(b => b.ToString("X2")));
        public ManualResetEventSlim Closed => closed;

        /// <summary>Starts the reader thread; <paramref name="onData"/> gets raw bytes, <paramref name="onClose"/> runs once.</summary>
        public void Run(Action<byte[]> onData, System.Action onClose)
        {
            new Thread(() =>
            {
                var buf = new byte[4096];
                try
                {
                    while (true)
                    {
                        int n;
                        fixed (byte* p = buf) n = Win32.recv(socket, p, buf.Length, 0);
                        if (n <= 0) break;
                        onData(buf[..n]);
                    }
                }
                catch (Exception ex) { Log.Warn($"link: reader stopped: {ex.Message}"); }
                Close();
                onClose();
            }) { IsBackground = true, Name = "rfcomm-read" }.Start();
        }

        public bool Send(byte[] data)
        {
            lock (sendGate)
            {
                if (isClosed != 0) return false;
                fixed (byte* p = data)
                {
                    var off = 0;
                    while (off < data.Length)
                    {
                        var n = Win32.send(socket, p + off, data.Length - off, 0);
                        if (n <= 0) { Close(); return false; }
                        off += n;
                    }
                }
                return true;
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref isClosed, 1) != 0) return;
            Win32.closesocket(socket);
            closed.Set();
        }
    }
}
