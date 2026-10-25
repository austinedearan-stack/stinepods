using StinePods.Core;

namespace StinePods.Core.Tests;

/// <summary>Runs every scenario in protocol/vectors/engine.txt against the C# engine.</summary>
public class EngineVectorTests
{
    public static TheoryData<string> Scenarios() => new(Vectors.Load().Keys);

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Scenario(string name)
    {
        var lines = Vectors.Load()[name];
        Engine? engine = null;
        long now = 0;
        List<string>? actual = null;
        var expected = new List<string>();
        string? lastEvent = null;

        void Check()
        {
            if (actual is null) return;
            Assert.True(expected.SequenceEqual(actual),
                $"after '> {lastEvent}':\n  expected: [{string.Join(" | ", expected)}]\n  actual:   [{string.Join(" | ", actual)}]");
            expected.Clear();
            actual = null;
        }

        foreach (var (no, line) in lines)
        {
            if (line.StartsWith("settings")) { engine = new Engine(Vectors.ParseSettings(line[8..])); continue; }
            if (line.StartsWith("< ")) { expected.Add(line[2..]); continue; }
            if (!line.StartsWith("> ")) throw new FormatException($"line {no}: {line}");
            Check();
            var ev = line[2..];
            if (ev.StartsWith("wait ")) { now += long.Parse(ev[5..]); continue; }
            engine ??= new Engine(new Settings { Role = Role.Phone });
            lastEvent = ev;
            actual = engine.Handle(Vectors.ParseEvent(ev), now).Select(Describe.Of).ToList();
        }
        Check();
    }
}

static class Vectors
{
    static Dictionary<string, List<(int, string)>>? cache;

    public static Dictionary<string, List<(int, string)>> Load()
    {
        if (cache is not null) return cache;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "protocol", "vectors", "engine.txt"))) dir = dir.Parent;
        var path = Path.Combine(dir?.FullName ?? throw new FileNotFoundException("protocol/vectors/engine.txt"), "protocol", "vectors", "engine.txt");

        var result = new Dictionary<string, List<(int, string)>>();
        List<(int, string)>? current = null;
        var no = 0;
        foreach (var raw in File.ReadLines(path))
        {
            no++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("scenario ")) { current = []; result.Add(line[9..], current); continue; }
            if (line == "end") { current = null; continue; }
            (current ?? throw new FormatException($"line {no} outside a scenario")).Add((no, line));
        }
        return cache = result;
    }

    public static Settings ParseSettings(string text)
    {
        var kv = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        var s = new Settings { Role = kv["role"] == "pc" ? Role.Pc : Role.Phone };
        if (kv.TryGetValue("headsets", out var hs)) s.Headsets = [.. hs.Split(',')];
        if (kv.TryGetValue("prefer", out var pr)) s.Prefer = Enum.Parse<Prefer>(pr, true);
        if (kv.TryGetValue("return", out var rs)) s.ReturnOnStop = Enum.Parse<ReturnOnStop>(rs, true);
        if (kv.TryGetValue("calls", out var c)) s.CallsTakeHeadset = c == "on";
        if (kv.TryGetValue("apps", out var apps))
            foreach (var a in apps.Split(',')) { var p = a.Split(':'); s.Apps[p[0]] = Enum.Parse<AppRule>(p[1], true); }
        return s;
    }

    public static Event ParseEvent(string text)
    {
        var w = text.Split(' ');
        return w[0] switch
        {
            "peer" => new PeerLink(w[1] == "up"),
            "headset" => new HeadsetLink(w[1], w[2] == "here"),
            "play" => new Playback(w[1], true),
            "stop" => new Playback(w[1], false),
            "answer" => new Answer(int.Parse(w[1]), w[2] == "yes"),
            "timer" => new TimerFired(w[1]),
            "call" => new Call(w[1] == "on"),
            "recv" => new Received(ParseMsg(w[1..])),
            _ => throw new FormatException(text),
        };
    }

    static Msg ParseMsg(string[] w) => w[0] switch
    {
        "claim" => new Msg("claim", w[1], int.Parse(w[2])),
        "released" => new Msg("released", w[1], w[2] == "-" ? null : int.Parse(w[2]), w[3]),
        "deny" => new Msg("deny", w[1], int.Parse(w[2]), w[3]),
        "state" => new Msg("state", w[1]) { Here = w[2] == "here" },
        "config" => new Msg("config")
        {
            Headsets = [.. w[1].Split(',')],
            Prefer = Enum.Parse<Prefer>(w[2]["prefer=".Length..], true),
            ReturnOnStop = Enum.Parse<ReturnOnStop>(w[3]["return=".Length..], true),
        },
        _ => throw new FormatException(string.Join(' ', w)),
    };
}
