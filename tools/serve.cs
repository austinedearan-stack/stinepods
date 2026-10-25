// Serves the StinePods apps on the local network so a phone can download them in its browser (no cable needed).
// Usage: dotnet run tools/serve.cs [port]   (run from the repo root; serves dist/)
using System.Net;
using System.Net.Sockets;
using System.Text;

var port = args.Length > 0 ? int.Parse(args[0]) : 8765;
var dist = Path.GetFullPath("dist");
var files = new Dictionary<string, (string Path, string Type)>
{
    ["/StinePods.apk"] = (Path.Combine(dist, "android", "StinePods.apk"), "application/vnd.android.package-archive"),
    ["/StinePods.exe"] = (Path.Combine(dist, "windows", "StinePods.exe"), "application/octet-stream"),
};
var ips = Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
foreach (var ip in ips) Console.WriteLine($"Open on the phone: http://{ip}:{port}/");

var listener = new TcpListener(IPAddress.Any, port);
listener.Start();
while (true)
{
    using var client = await listener.AcceptTcpClientAsync();
    try
    {
        var stream = client.GetStream();
        var request = new StreamReader(stream).ReadLine() ?? "";
        var path = request.Split(' ').ElementAtOrDefault(1) ?? "/";
        Console.WriteLine($"{DateTime.Now:T} {client.Client.RemoteEndPoint} {request}");
        if (files.TryGetValue(path, out var f) && File.Exists(f.Path))
        {
            var len = new FileInfo(f.Path).Length;
            var head = $"HTTP/1.1 200 OK\r\nContent-Type: {f.Type}\r\nContent-Length: {len}\r\nContent-Disposition: attachment; filename=\"{Path.GetFileName(f.Path)}\"\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await using var fs = File.OpenRead(f.Path);
            await fs.CopyToAsync(stream);
        }
        else
        {
            var html = "<!doctype html><meta name=viewport content='width=device-width'><body style='font-family:sans-serif;background:#000;color:#eee;padding:24px'>"
                + "<h1>StinePods</h1><p><a style='color:#ff8a00;font-size:22px' href='/StinePods.apk'>Download for Android</a></p>"
                + "<p><a style='color:#ff8a00;font-size:22px' href='/StinePods.exe'>Download for Windows</a></p></body>";
            var body = Encoding.UTF8.GetBytes(html);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(body);
        }
    }
    catch (IOException) { }
}
