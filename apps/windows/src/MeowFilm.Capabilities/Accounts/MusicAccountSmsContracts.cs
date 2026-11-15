// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace MeowFilm.Capabilities.Accounts;

/// <summary>Current trusted native SMS contract. SMS is never a background job.</summary>
internal interface IMusicAccountSmsProtocol
{
    Task<MusicAccountSmsSent> SendSmsAsync(MusicAccountSmsInput input, CancellationToken cancellationToken = default);
    Task LoginSmsAsync(MusicAccountSmsInput input, string attemptId,
        MusicAccountSessionHandoff acceptSession, CancellationToken cancellationToken = default);
}

public sealed record MusicAccountSmsSent(string Platform, bool Sent, int RetryAfterSeconds);

/// <summary>Transient native input, not a serializable DTO, diagnostic or saved document.</summary>
[DebuggerDisplay("MusicAccountSmsInput (redacted)")]
public sealed class MusicAccountSmsInput(string platform, string phone, string countryCode = "86", string? captcha = null)
{
    [JsonIgnore] public string Platform { get; } = platform;
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)] public string Phone { get; } = phone;
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)] public string CountryCode { get; } = countryCode;
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)] public string? Captcha { get; } = captcha;
    public override string ToString() => "MusicAccountSmsInput (redacted)";

}
