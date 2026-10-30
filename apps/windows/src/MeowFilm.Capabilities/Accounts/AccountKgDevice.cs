// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

// Published account-v1 device data is a read-only migration source.
internal sealed partial class AccountDocument
{
    internal const string KgDeviceKey = "[\"meowfilm\",\"music\",1,\"platform-device\",\"kg\"]";
    internal MusicKgClientContext? GetKgDevice()
    {
        var row = root["entries"]!.AsArray().FirstOrDefault(n => n!["key"]!.GetValue<string>() == KgDeviceKey);
        return row is null ? null : ReadKgDevice(JsonSerializer.SerializeToElement(row["value"], Encoding));
    }
    private static MusicKgClientContext ReadKgDevice(JsonElement raw)
    {
        AccountRules.Fields(raw, ["version", "generation", "seed", "dfid"],
            ["version", "generation", "seed", "dfid"], "CORRUPT_STORAGE");
        AccountRules.Require(AccountRules.Integer(raw.GetProperty("version")) == 1, "CORRUPT_STORAGE");
        long generation = AccountRules.Integer(raw.GetProperty("generation"));
        string seed = raw.GetProperty("seed").GetString()!;
        string? dfid = raw.GetProperty("dfid").GetString();
        AccountRules.Require(generation > 0 && seed is not null && AccountRules.Match(seed, "^[a-f0-9]{32}$") &&
            (dfid is null || AccountRules.Match(dfid, "^[A-Za-z0-9_-]{8,128}$")), "CORRUPT_STORAGE");
        return new(generation, seed!, dfid);
    }
}
