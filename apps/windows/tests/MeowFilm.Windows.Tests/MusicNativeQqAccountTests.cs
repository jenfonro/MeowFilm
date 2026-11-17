// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Text.Json;
using MeowFilm.Capabilities.Accounts;
namespace MeowFilm.Windows.Tests;
public static class MusicNativeQqAccountTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (var test in MusicAccountRuntimeFixture.QrCases("tx", "music.native.accounts.qq.")) yield return test;
        yield return new("music.native.accounts.qq.native-png-valid-before-image-decode", () => {
            AccountQrPng.Validate(MusicAccountRuntimeFixture.QrPng()); return Task.CompletedTask;
        });
        yield return new("music.native.accounts.qq.native-png-over-half-MiB-and-256-chunks", () => {
            byte[] bytes = MusicAccountRuntimeFixture.QrPng(width: 1024, height: 1024, noise: true, chunkBytes: 2048);
            Check(bytes.Length > 524288);
            AccountQrPng.Validate(bytes); return Task.CompletedTask;
        });
        foreach (string fault in new[] { "bad-crc", "huge", "bomb", "trailing" })
            yield return new("music.native.accounts.qq.native-png-reject-" + fault, () => {
                byte[] bytes = fault switch {
                    "huge" => MusicAccountRuntimeFixture.QrPng(width: 2048),
                    "bomb" => MusicAccountRuntimeFixture.QrPng(inflated: 1024 * 1024),
                    "trailing" => [.. MusicAccountRuntimeFixture.QrPng(), 0], _ => MusicAccountRuntimeFixture.QrPng() };
                if (fault == "bad-crc") bytes[^1] ^= 1;
                try { AccountQrPng.Validate(bytes); }
                catch (MusicAccountException error) {
                    Check(error.Code == (fault is "huge" or "bomb" ? "LIMIT" : "INVALID_RESPONSE")); return Task.CompletedTask;
                }
                throw new InvalidOperationException("Unsafe native QR image admitted");
            });
    }
    internal static string DataPath() { string path = TestWorkspace.DataPath("qq-account-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    internal static void Check(bool value) => MusicAccountRuntimeFixture.Check(value);
    internal static bool Ok(JsonElement value) => value.GetProperty("ok").GetBoolean();
    internal static string AccountState(JsonElement value) => value.GetProperty("account").GetProperty("state").GetString()!;
    internal static string RowState(JsonElement value) => value.GetProperty("items").EnumerateArray().Single(row => row.GetProperty("platform").GetString() == "tx").GetProperty("state").GetString()!;
    internal static async Task<JsonElement> Login(MusicAccountOwner owner)
    {
        var begin = MusicAccountRuntimeFixture.Ok(await owner.CallAsync("music.accounts.qr.begin", new { platform = "tx" }));
        return await owner.CallAsync("music.accounts.qr.poll", new { platform = "tx", challengeId = MusicAccountRuntimeFixture.Id(begin) });
    }
}
