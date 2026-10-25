using StinePods.Windows;

using var single = new Mutex(true, @"Local\StinePods", out var first);
if (!first) return;   // already running

AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Warn($"crash: {e.ExceptionObject}");
TaskScheduler.UnobservedTaskException += (_, e) => { Log.Warn($"unobserved: {e.Exception.InnerException?.Message}"); e.SetObserved(); };

var tray = new Tray();
tray.Start();
new Agent(new Store(), tray).Run();
