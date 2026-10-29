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

    internal MusicAccountSmsInput Normalize(bool login)
    {
        AccountRules.Platform(Platform);
        AccountRules.Require(Platform == "wy", "UNSUPPORTED");
        AccountRules.Require(Phone is { Length: <= 32 } && CountryCode is { Length: <= 16 } &&
            (Captcha is null || Captcha.Length <= 16));
        string phone = Phone!.Trim(), country = CountryCode!, code = Captcha?.Trim() ?? "";
        if (country.StartsWith('+')) country = country[1..];
        AccountRules.Require(AccountRules.Match(phone, "^[0-9]{5,15}$") &&
            AccountRules.Match(country, "^[1-9][0-9]{0,3}$") &&
            (country != "86" || AccountRules.Match(phone, "^1[0-9]{10}$")) &&
            (login ? AccountRules.Match(code, "^[0-9]{4,8}$") : code.Length == 0));
        return new(Platform, phone, country, login ? code : null);
    }
}
