using System.Text.Json;
using StinePods.Core;
using StinePods.Windows.Native;

namespace StinePods.Windows;

/// <summary>
/// Persistent state under %LOCALAPPDATA%\StinePods: device identity, settings (including the last config the phone
/// sent, so the agent works stand-alone), and pairing keys protected with DPAPI.
/// </summary>
internal sealed class Store : IKeyStore
{
    public static string Dir { get; } = CreateDir();

    static string CreateDir()
    {
        var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StinePods");
        Directory.CreateDirectory(d);
        return d;
    }

    static string SettingsPath => Path.Combine(Dir, "settings.json");
    static string KeysPath => Path.Combine(Dir, "keys.bin");

    readonly Lock gate = new();
    readonly Dictionary<string, byte[]> keys;

    public string DeviceId { get; }
    public bool Enabled { get; set; } = true;
    public Settings Settings { get; } = new() { Role = Role.Pc };

    public Store()
    {
        string? id = null;
        if (File.Exists(SettingsPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(SettingsPath));
                var j = doc.RootElement;
                id = j.TryGetProperty("id", out var v) ? v.GetString() : null;
                if (j.TryGetProperty("enabled", out var en)) Enabled = en.GetBoolean();
                if (j.TryGetProperty("headsets", out var hs)) Settings.Headsets = [.. hs.EnumerateArray().Select(x => x.GetString()!)];
                if (j.TryGetProperty("prefer", out var pr) && Enum.TryParse<Prefer>(pr.GetString(), true, out var p)) Settings.Prefer = p;
                if (j.TryGetProperty("returnOnStop", out var rs) && Enum.TryParse<ReturnOnStop>(rs.GetString(), true, out var r)) Settings.ReturnOnStop = r;
                if (j.TryGetProperty("apps", out var apps))
                    foreach (var a in apps.EnumerateObject())
                        if (Enum.TryParse<AppRule>(a.Value.GetString(), true, out var rule)) Settings.Apps[a.Name] = rule;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { Log.Warn($"settings unreadable, using defaults: {ex.Message}"); }
        }
        // Apps without media controls ask first unless the user chose otherwise (owner's choice).
        Settings.Apps.TryAdd(Agent.Other, AppRule.Ask);
        DeviceId = id ?? Guid.NewGuid().ToString();
        keys = LoadKeys();
        Save();
    }

    public void Save()
    {
        lock (gate)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("id", DeviceId);
                w.WriteBoolean("enabled", Enabled);
                w.WriteStartArray("headsets");
                foreach (var h in Settings.Headsets) w.WriteStringValue(h);
                w.WriteEndArray();
                w.WriteString("prefer", Describe.Name<Prefer>(Settings.Prefer));
                w.WriteString("returnOnStop", Describe.Name<ReturnOnStop>(Settings.ReturnOnStop));
                w.WriteStartObject("apps");
                foreach (var (app, rule) in Settings.Apps) w.WriteString(app, Describe.Name<AppRule>(rule));
                w.WriteEndObject();
                w.WriteEndObject();
            }
            File.WriteAllBytes(SettingsPath, ms.ToArray());
        }
    }

    public byte[]? Get(string peerId)
    {
        lock (gate) return keys.GetValueOrDefault(peerId);
    }

    public void Put(string peerId, byte[] key)
    {
        lock (gate)
        {
            keys[peerId] = key;
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var (id, k) in keys) w.WriteString(id, Convert.ToBase64String(k));
                w.WriteEndObject();
            }
            File.WriteAllBytes(KeysPath, Win32.Protect(ms.ToArray(), protect: true));
        }
    }

    static Dictionary<string, byte[]> LoadKeys()
    {
        if (!File.Exists(KeysPath)) return [];
        try
        {
            using var doc = JsonDocument.Parse(Win32.Protect(File.ReadAllBytes(KeysPath), protect: false));
            return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => Convert.FromBase64String(p.Value.GetString()!));
        }
        catch (Exception ex)
        {
            Log.Warn($"pairing keys unreadable, the phone will need to pair again: {ex.Message}");
            return [];
        }
    }
}
