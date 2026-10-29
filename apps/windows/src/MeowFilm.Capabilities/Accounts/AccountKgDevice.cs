// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Accounts;

// Native-only, never a command/DTO. Seed and dfid share the account owner's DPAPI/CAS.
internal sealed class StoredKgDevice
{
    internal long Generation { get; init; }
    internal string Seed { get; init; } = "";
    internal string? Dfid { get; init; }
    internal string? Fingerprint { get; init; }
    internal string Mid => new BigInteger(MD5.HashData(Encoding.ASCII.GetBytes(Seed)), true, true)
        .ToString(CultureInfo.InvariantCulture);
    internal string Dev => Seed[..10].ToUpperInvariant();
    internal static bool Same(StoredKgDevice a, StoredKgDevice b) =>
        a.Generation == b.Generation && a.Fingerprint == b.Fingerprint;
    public override string ToString() => "StoredKgDevice (redacted)";
}

// The owner supplies these callbacks only for a live, fenced begin operation.
// Protocol never receives the protected store or becomes a second writer.
internal interface IMusicKgDeviceProtocol
{
    Task<MusicAccountChallenge> BeginKgAsync(
        Func<CancellationToken, Task<StoredKgDevice>> acquire,
        Func<StoredKgDevice, string, CancellationToken, Task<StoredKgDevice>> save,
        CancellationToken token);
}

internal sealed partial class AccountDocument
{
    internal const string KgDeviceKey = "[\"meowfilm\",\"music\",1,\"platform-device\",\"kg\"]";
    internal StoredKgDevice GetKgDevice()
    {
        try
        {
            var row = root["entries"]!.AsArray().FirstOrDefault(n => n!["key"]!.GetValue<string>() == KgDeviceKey);
            if (row is null) return new();
            return ReadKgDevice(JsonSerializer.SerializeToElement(row["value"], Encoding));
        }
        catch { throw new MusicAccountException("CORRUPT_STORAGE"); }
    }
    private static StoredKgDevice ReadKgDevice(JsonElement raw)
    {
        AccountRules.Fields(raw, ["version", "generation", "seed", "dfid"],
            ["version", "generation", "seed", "dfid"], "CORRUPT_STORAGE");
        AccountRules.Require(AccountRules.Integer(raw.GetProperty("version")) == 1, "CORRUPT_STORAGE");
        long generation = AccountRules.Integer(raw.GetProperty("generation"));
        string seed = raw.GetProperty("seed").GetString()!;
        string? dfid = raw.GetProperty("dfid").GetString();
        AccountRules.Require(generation > 0 && seed is not null &&
            AccountRules.Match(seed, "^[a-f0-9]{32}$") &&
            (dfid is null || AccountRules.Match(dfid, "^[A-Za-z0-9_-]{8,128}$")), "CORRUPT_STORAGE");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(raw, Encoding);
        try { return new() { Generation = generation, Seed = seed!, Dfid = dfid,
            Fingerprint = Convert.ToHexStringLower(SHA256.HashData(bytes)) }; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal byte[] ReplaceKgDevice(StoredKgDevice expected, string seed, string? dfid)
    {
        var current = GetKgDevice();
        AccountRules.Require(StoredKgDevice.Same(current, expected), "STORAGE_CONFLICT");
        AccountRules.Require(current.Generation < AccountRules.MaxInteger, "LIMIT");
        AccountRules.Require(AccountRules.Match(seed, "^[a-f0-9]{32}$") &&
            (current.Generation == 0 || current.Seed == seed) &&
            (dfid is null || AccountRules.Match(dfid, "^[A-Za-z0-9_-]{8,128}$")), "INVALID_REQUEST");
        var entries = root["entries"]!.AsArray();
        var row = entries.FirstOrDefault(n => n!["key"]!.GetValue<string>() == KgDeviceKey);
        var value = new JsonObject { ["version"] = 1, ["generation"] = current.Generation + 1,
            ["seed"] = seed, ["dfid"] = dfid };
        if (row is null)
        {
            AccountRules.Require(entries.Count < CurrentEntryCount, "LIMIT");
            entries.Add(new JsonObject { ["key"] = KgDeviceKey, ["value"] = value });
        }
        else row["value"] = value;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root, Encoding);
        if (bytes.Length <= MaximumBytes) return bytes;
        CryptographicOperations.ZeroMemory(bytes);
        throw new MusicAccountException("LIMIT");
    }
}
