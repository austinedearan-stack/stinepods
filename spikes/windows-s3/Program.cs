// Phase 0 spike S3: which Windows apps report playback through the system media sessions (GSMTC),
// how quickly play/pause events arrive, and whether we can pause/resume them remotely.
//
// usage: s3 watch [seconds]            log every session and play/pause change
//        s3 pause <app-part> [seconds] pause a session, time the confirmation
//        s3 play  <app-part> [seconds] resume a session, time the confirmation
//
// Only app ids and play state are logged, never titles (privacy: see docs/SECURITY.md #6).

using System.Diagnostics;
using Windows.Media.Control;

var cmd = args.ElementAtOrDefault(0) ?? "watch";
var match = cmd == "watch" ? "" : args.ElementAtOrDefault(1) ?? "";
var seconds = int.TryParse(args.ElementAtOrDefault(cmd == "watch" ? 1 : 2), out var s) ? s : 10;

var watchdog = new Timer(_ => { Console.WriteLine("WATCHDOG: hung, exiting"); Environment.Exit(3); }, null, TimeSpan.FromSeconds(seconds + 15), Timeout.InfiniteTimeSpan);
var sw = Stopwatch.StartNew();
void Log(string m) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} +{sw.ElapsedMilliseconds,6}ms {m}");

var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
Log($"manager ready, current={mgr.GetCurrentSession()?.SourceAppUserModelId ?? "(none)"}");

var hooked = new HashSet<string>();
var changed = new TaskCompletionSource();

void HookAll()
{
    foreach (var session in mgr.GetSessions())
    {
        var id = session.SourceAppUserModelId;
        if (!hooked.Add(id)) continue;
        Log($"session {id}: {Status(session)}");
        session.PlaybackInfoChanged += (x, _) =>
        {
            Log($"{x.SourceAppUserModelId} -> {Status(x)}");
            if (match != "" && x.SourceAppUserModelId.Contains(match, StringComparison.OrdinalIgnoreCase)) changed.TrySetResult();
        };
    }
}

mgr.SessionsChanged += (_, _) => { Log("sessions changed"); HookAll(); };
mgr.CurrentSessionChanged += (m, _) => Log($"current session -> {m.GetCurrentSession()?.SourceAppUserModelId ?? "(none)"}");
HookAll();

if (cmd is "pause" or "play")
{
    var target = mgr.GetSessions().FirstOrDefault(x => x.SourceAppUserModelId.Contains(match, StringComparison.OrdinalIgnoreCase));
    if (target is null) { Log($"no session matches '{match}'"); return; }
    sw.Restart();
    var ok = cmd == "pause" ? await target.TryPauseAsync() : await target.TryPlayAsync();
    Log($"Try{(cmd == "pause" ? "Pause" : "Play")}Async({target.SourceAppUserModelId}) -> {ok}");
    await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
    Log(changed.Task.IsCompleted ? "confirmed by PlaybackInfoChanged" : "no confirmation event");
}
else
{
    Log($"watching for {seconds}s...");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
}
GC.KeepAlive(watchdog);

static string Status(GlobalSystemMediaTransportControlsSession x) => x.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? "?";
