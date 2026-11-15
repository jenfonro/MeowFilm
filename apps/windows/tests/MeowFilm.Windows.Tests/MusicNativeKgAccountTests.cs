// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Text.Json;
using MeowFilm.Capabilities.Accounts;
namespace MeowFilm.Windows.Tests;
public static class MusicNativeKgAccountTests
{
    public static IEnumerable<TestCase> Cases() => MusicAccountRuntimeFixture.QrCases("kg", "music.native.accounts.kg.");
    internal static void Check(bool value) => MusicAccountRuntimeFixture.Check(value);
    internal static bool Ok(JsonElement value) => value.GetProperty("ok").GetBoolean();
    internal static string Code(JsonElement value) => MusicAccountRuntimeFixture.Code(value);
    internal static string Id(JsonElement value) => MusicAccountRuntimeFixture.Id(value);
    internal static string StateOf(JsonElement value) => value.GetProperty("account").GetProperty("state").GetString()!;
    internal static JsonElement Row(JsonElement value, string platform = "kg") => value.GetProperty("items").EnumerateArray().Single(row => row.GetProperty("platform").GetString() == platform);
    internal static string DataPath() { string path = TestWorkspace.DataPath("kg-account-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    internal static Task<JsonElement> Begin(MusicAccountOwner owner) => owner.CallAsync("music.accounts.qr.begin", new { platform = "kg" });
    internal static Task<JsonElement> Poll(MusicAccountOwner owner, string id) => owner.CallAsync("music.accounts.qr.poll", new { platform = "kg", challengeId = id });
    internal static async Task Until(Func<bool> ready)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!ready()) await Task.Delay(10, deadline.Token); }
}
