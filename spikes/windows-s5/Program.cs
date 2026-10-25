// Phase 0 spike S5: phone<->PC offline control link over Bluetooth RFCOMM.
// The PC advertises a StinePods RFCOMM service and echoes every line it receives, so the phone can measure
// round-trip time.
//
// usage: s5 serve [seconds]

using System.Diagnostics;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

var seconds = int.TryParse(args.ElementAtOrDefault(1), out var s) ? s : 300;
var sw = Stopwatch.StartNew();
void Log(string m) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} +{sw.ElapsedMilliseconds,6}ms {m}");

// Spike-only service id; the real protocol id lives in protocol/ once it exists.
var serviceId = RfcommServiceId.FromUuid(new Guid("8198aff7-3480-44df-af7f-bf9bd001588c"));
var provider = await RfcommServiceProvider.CreateAsync(serviceId);
var listener = new StreamSocketListener();
listener.ConnectionReceived += async (_, e) =>
{
    var socket = e.Socket;
    Log($"connected: {socket.Information.RemoteHostName.DisplayName}");
    using var reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
    using var writer = new DataWriter(socket.OutputStream);
    var buffer = "";
    var lines = 0;
    try
    {
        while (true)
        {
            var n = await reader.LoadAsync(256);
            if (n == 0) break;
            buffer += reader.ReadString(n);
            int nl;
            while ((nl = buffer.IndexOf('\n')) >= 0)
            {
                var line = buffer[..nl];
                buffer = buffer[(nl + 1)..];
                writer.WriteString(line + "\n");
                await writer.StoreAsync();
                if (++lines % 50 == 0 || !line.StartsWith("ping")) Log($"rx/echo #{lines}: {line}");
            }
        }
    }
    catch (Exception ex) { Log($"link error: {ex.Message}"); }
    Log($"disconnected after {lines} lines");
};

await listener.BindServiceNameAsync(serviceId.AsString(), SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
provider.StartAdvertising(listener, radioDiscoverable: false);
Log($"advertising RFCOMM service {serviceId.Uuid} for {seconds}s");
await Task.Delay(TimeSpan.FromSeconds(seconds));
provider.StopAdvertising();
Log("stopped");
