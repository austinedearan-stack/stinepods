using StinePods.Core;
using static StinePods.Core.LinkCrypto;

namespace StinePods.Core.Tests;

/// <summary>protocol/vectors/crypto.txt: the Kotlin implementation checks the same values.</summary>
public class CryptoVectorTests
{
    static readonly Dictionary<string, string> V = Load();

    static Dictionary<string, string> Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "protocol", "vectors", "crypto.txt"))) dir = dir.Parent;
        return File.ReadLines(Path.Combine(dir!.FullName, "protocol", "vectors", "crypto.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', 2))
            .ToDictionary(p => p[0], p => p[1]);
    }

    [Fact]
    public void DerivedValuesMatch()
    {
        using var phone = new KeyPair(Unhex(V["phone_d"]), Unhex(V["phone_pub"]));
        using var pc = new KeyPair(Unhex(V["pc_d"]), Unhex(V["pc_pub"]));
        var shared = phone.Agree(pc.Public);
        Assert.Equal(V["shared"], Hex(shared));
        Assert.Equal(V["shared"], Hex(pc.Agree(phone.Public)));
        Assert.Equal(int.Parse(V["code"]), PairCode(shared, phone.Public, pc.Public));

        var longKey = LongKey(shared);
        Assert.Equal(V["long_key"], Hex(longKey));
        var connKey = ConnKey(longKey, Unhex(V["phone_nonce"]), Unhex(V["pc_nonce"]));
        Assert.Equal(V["conn_key"], Hex(connKey));

        var body = Unhex(V["body"]);
        Assert.Equal(V["tag_P_7"], Hex(Tag(connKey, (byte)'P', 7, body)));
        Assert.Equal(V["tag_C_7"], Hex(Tag(connKey, (byte)'C', 7, body)));
    }
}
