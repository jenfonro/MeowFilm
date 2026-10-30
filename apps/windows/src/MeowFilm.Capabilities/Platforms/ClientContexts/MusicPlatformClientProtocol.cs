// SPDX-License-Identifier: GPL-3.0-only
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities.Platforms;

// Fixed anonymous platform protocols. No account credentials, state writer,
// constructor I/O or startup registration. The context owner drains actual work.
internal sealed partial class MusicPlatformClientProtocol
{
    private readonly MusicPlatformHttp transport;
    private readonly Func<string> registrationEntropy;
    internal TimeProvider Clock { get; }
    internal MusicPlatformClientProtocol(MusicPlatformHttp transport, TimeProvider? clock = null,
        Func<string>? registrationEntropy = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Clock = clock ?? TimeProvider.System;
        this.registrationEntropy = registrationEntropy ?? (() => string.Concat(Enumerable.Range(0, 6)
            .Select(_ => "0123456789abcdefghijklmnopqrstuvwxyz"[RandomNumberGenerator.GetInt32(36)])));
    }
    private async Task<JsonObject> SendQqDevice(MusicQqDeviceHttpStep step, HttpRequestMessage request, CancellationToken token)
    {
        var result = await transport.SendQqDeviceJsonAsync(step, request, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return result.Body;
    }
    private static string Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var text)
        ? text : throw new MusicPlatformException("INVALID_RESPONSE");
    private static string Scalar(JsonNode? value) => PJ.Scalar(value);
    private static bool Match(string value, string pattern) => Regex.IsMatch(value, pattern,
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static int StrictCode(JsonNode? node)
    {
        PJ.Require(node is JsonValue && !((JsonValue)node).TryGetValue<string>(out _), "INVALID_RESPONSE");
        using var json = JsonDocument.Parse(node!.ToJsonString());
        PJ.Require(json.RootElement.TryGetDecimal(out var number) && number is >= int.MinValue and <= int.MaxValue &&
            decimal.Truncate(number) == number, "INVALID_RESPONSE");
        return (int)number;
    }
    private static JsonObject QqDeviceData(JsonObject body)
    {
        PJ.Require(StrictCode(body["code"]) == 0, "UPSTREAM_ERROR");
        PJ.Require(body["req_0"] is JsonObject, "INVALID_RESPONSE");
        PJ.Require(StrictCode(body["req_0"]!["code"]) == 0, "UPSTREAM_ERROR");
        PJ.Require(body["req_0"]!["data"] is JsonObject, "INVALID_RESPONSE");
        return body["req_0"]!["data"]!.AsObject();
    }
}
