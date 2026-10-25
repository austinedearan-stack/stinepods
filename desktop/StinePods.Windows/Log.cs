namespace StinePods.Windows;

/// <summary>Small rotating local log (never leaves the PC; see docs/SECURITY.md #9).</summary>
internal static class Log
{
    const long MaxBytes = 512 * 1024;
    static readonly Lock Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(Store.Dir, "stinepods.log");

    public static void Info(string m) => Write("INFO", m);
    public static void Warn(string m) => Write("WARN", m);

    static void Write(string level, string m)
    {
        lock (Gate)
        {
            try
            {
                var f = new FileInfo(Path);
                if (f.Exists && f.Length > MaxBytes) File.Move(Path, Path + ".1", overwrite: true);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {m}{Environment.NewLine}");
            }
            catch (IOException) { }
        }
    }
}
