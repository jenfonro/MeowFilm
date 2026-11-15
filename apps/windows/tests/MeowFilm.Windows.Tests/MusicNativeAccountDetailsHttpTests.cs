// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeAccountDetailsTests
{
    // This group covers the native adapter; provider HTTP/crypto/resource behavior belongs to Node tests.
    private static IEnumerable<TestCase> HttpCases()
    {
        foreach (string platform in new[] { "wy", "tx", "kg" })
        {
            string p = platform;
            yield return Case(p + "-profile-adapter-uses-exact-issued-candidate-and-one-observation", async () => {
                await using var runtime = new MusicPlatformRuntimeFixture();
                await using var protocol = runtime.CreateAccountProtocol();
                var account = new MusicPlatformSession(p, "synthetic-details", 7, UserId, MusicAccountDetailsFixture.Cookie(p));
                runtime.OnRequest = (call, token) => {
                    Check(call.Operation == "account.profile" && call.Platform == p);
                    var candidate = call.Payload.GetProperty("candidate");
                    Check(candidate.GetProperty("userId").GetString() == UserId && candidate.GetProperty("cookie").GetString() == MusicAccountDetailsFixture.Cookie(p));
                    Check(!call.Payload.TryGetProperty("expectedSession", out _) && !call.Parameters.EnumerateObject().Any());
                    return Task.FromResult(MusicPlatformRuntimeFixture.Observation(p, UserId));
                };
                var result = await protocol.ReadAccountDetailsAsync(account);
                Check(result.Profile.UserId == UserId && runtime.Requests.Count == 1);
            });
        }
        foreach (string code in new[] { "NETWORK_ERROR", "INVALID_RESPONSE", "LIMIT", "FORBIDDEN", "VERIFICATION_REQUIRED", "TIMEOUT" })
        {
            string c = code;
            yield return Case("profile-canonical-" + c + "-never-saves-or-retries", async () => {
                await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed();
                f.FailureCode = c; await Error(() => f.Refresh(), c);
                Check(f.Wrapper.Writes == 0 && f.Steps.Count == 1);
            });
        }
    }
}
