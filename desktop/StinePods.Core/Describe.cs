namespace StinePods.Core;

/// <summary>
/// One-line text form of actions, matching the "&lt;" lines of protocol/vectors. Also used for logs.
/// </summary>
public static class Describe
{
    public static string Of(Action a) => a switch
    {
        Pause p => $"pause {p.App}",
        Resume r => $"resume {r.App}",
        Release r => $"release {r.H}",
        Take t => $"take {t.H}",
        Send s => "send " + Of(s.M),
        Ask k => $"ask {k.Id} {k.App}",
        StartTimer t => $"timer {t.Name} {t.Ms}",
        CancelTimer c => $"cancel {c.Name}",
        Notify n => $"notify {n.Text}",
        _ => a.ToString(),
    };

    public static string Of(Msg m) => m.T switch
    {
        "config" => $"config {string.Join(',', m.Headsets ?? [])} prefer={Name(m.Prefer)} return={Name(m.ReturnOnStop)}",
        "claim" => $"claim {m.H} {m.Id}",
        "released" => $"released {m.H} {m.Id?.ToString() ?? "-"} {m.Why}",
        "deny" => $"deny {m.H} {m.Id} {m.Why}",
        "state" => $"state {m.H} {(m.Here == true ? "here" : "away")}",
        _ => m.T,
    };

    public static string Name<T>(T? value) where T : struct, Enum => value?.ToString().ToLowerInvariant() ?? "-";
}
