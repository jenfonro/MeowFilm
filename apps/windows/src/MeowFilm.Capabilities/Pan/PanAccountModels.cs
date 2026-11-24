using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

internal sealed record PanProviderDescriptor(
    string Provider,
    string Name,
    IReadOnlyList<string> CredentialFields,
    bool SupportsQr,
    bool SupportsRefresh)
{
    internal bool HasField(string field) => CredentialFields.Contains(field, StringComparer.Ordinal);
}

internal sealed class PanAccountException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

public sealed class PanCredentialField(string id)
{
    public string Id { get; } = id;
    public string Label => Id switch
    {
        "cookie" => "Cookie", "username" => "用户名", "password" => "密码",
        "authorization" => "Authorization", "refreshedAt" => "刷新时间（毫秒）",
        _ => throw new BridgeException("INVALID_RESPONSE")
    };
    public bool IsNumber => Id is "refreshedAt";
    public bool IsSecret => Id is "cookie" or "password" or "authorization";

    internal object Parse(string text)
    {
        if (!IsNumber) return text;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ||
            value < 0 || value > BridgeCodec.MaximumSafeInteger)
            throw new BridgeException("INVALID_REQUEST");
        return value;
    }
}

internal sealed record PanAccountSnapshot(string Provider,
    long? Revision,
    bool? Configured,
    string? Status,
    string[]? Fields,
    string? ErrorCode = null)
{
    internal static PanAccountSnapshot Corrupt(string provider) => new(provider, null, null, null, null, "CORRUPT_STORAGE");

    internal static PanAccountSnapshot Read(JsonElement value)
    {
        string provider;
        try { provider = BridgeCodec.RequiredProperty(value, "provider").GetString()!; }
        catch { throw new BridgeException("INVALID_RESPONSE"); }
        if (PanSettingsStore.FindProvider(provider) is not { } declaration)
            throw new BridgeException("INVALID_RESPONSE");
        if (value.TryGetProperty("errorCode", out _))
        {
            try
            {
                if (BridgeCodec.RequiredProperty(value, "errorCode").GetString() != "CORRUPT_STORAGE" ||
                    new[] { "revision", "configured", "status", "fields" }.Any(key => value.TryGetProperty(key, out _)))
                    throw new BridgeException("INVALID_RESPONSE");
                return Corrupt(provider);
            }
            catch { throw new BridgeException("INVALID_RESPONSE"); }
        }
        foreach (string key in new[] { "revision", "configured", "status", "fields" })
            if (value.EnumerateObject().Count(item => item.NameEquals(key)) != 1)
                throw new BridgeException("INVALID_RESPONSE");
        if (!value.TryGetProperty("revision", out var revisionValue) || revisionValue.ValueKind != JsonValueKind.Number ||
            !revisionValue.TryGetInt64(out long revision) ||
            !value.TryGetProperty("configured", out var configuredValue) ||
            configuredValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !value.TryGetProperty("status", out var statusValue) || statusValue.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("fields", out var fieldValues) || fieldValues.ValueKind != JsonValueKind.Array ||
            fieldValues.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new BridgeException("INVALID_RESPONSE");
        bool configured = configuredValue.GetBoolean();
        string status = statusValue.GetString()!;
        var fields = fieldValues.EnumerateArray().Select(item => item.GetString()!).ToArray();
        if (fields.Distinct().Count() != fields.Length ||
            fields.Any(field => !declaration.HasField(field)) ||
            configured != (fields.Length > 0) || revision < 0 || revision > BridgeCodec.MaximumSafeInteger ||
            status is not ("signedIn" or "expired" or "signedOut") ||
            !configured && status != "signedOut")
            throw new BridgeException("INVALID_RESPONSE");
        return new(provider, revision, configured, status, fields);
    }
}

public sealed class PanAccountRow : INotifyPropertyChanged
{
    internal PanAccountRow(PanProviderDescriptor provider)
    {
        Provider = provider.Provider;
        Name = provider.Name;
        CredentialFields = provider.CredentialFields
            .Select(field => new PanCredentialField(field)).ToArray();
        foreach (var field in CredentialFields) _ = field.Label;
        SupportsQr = provider.SupportsQr;
        SupportsRefresh = provider.SupportsRefresh;
    }

    public string Provider { get; }
    public string Name { get; }
    public IReadOnlyList<PanCredentialField> CredentialFields { get; }
    public bool SupportsQr { get; }
    public bool SupportsRefresh { get; }
    public long? Revision => snapshot?.Revision;
    public bool Configured => snapshot?.Configured == true;
    public string? Status => snapshot?.Status;
    public string? ErrorCode => snapshot?.ErrorCode;
    public string StatusText => ErrorCode == "CORRUPT_STORAGE" ? "凭据损坏" :
        Status switch
        {
            "signedIn" => "已登录",
            "unverified" => "未验证",
            "expired" => "失效",
            "signedOut" => "未登录",
            _ => ""
        };
    public bool HasSnapshot => snapshot is not null;
    public bool IsBusy => Request is not null;
    public bool CanLogin => Available && Status != "signedIn";
    public bool CanEdit => Available;
    public bool CanRefresh => Available && Status is ("signedIn" or "unverified") && SupportsRefresh;
    public bool CanLogout => Available && Configured && Status is ("signedIn" or "unverified" or "expired");
    public bool ShowLogin => HasSnapshot && Status is ("signedOut" or "expired");
    public bool ShowEdit => Status is ("signedIn" or "unverified");
    public bool ShowLogout => Status is ("signedIn" or "unverified" or "expired");
    public string LoginText => Status == "expired" ? "重新登录" : "登录";
    internal long Generation;
    internal CancellationTokenSource? Request;
    internal bool Editing;
    internal bool Active;
    private PanAccountSnapshot? snapshot;
    private bool Available => Active && snapshot is { ErrorCode: null, Revision: not null } && !IsBusy && !Editing;

    internal void Publish(PanAccountSnapshot value)
    {
        if (value.Provider != Provider) throw new BridgeException("INVALID_RESPONSE");
        if (value.ErrorCode is null && value.Revision < Revision) return;
        snapshot = value;
        Changed();
    }
    internal void Changed()
    {
        foreach (string name in new[] { nameof(Revision), nameof(Configured), nameof(Status), nameof(StatusText), nameof(ErrorCode),
            nameof(HasSnapshot), nameof(IsBusy), nameof(CanLogin), nameof(CanEdit), nameof(CanRefresh), nameof(CanLogout),
            nameof(ShowLogin), nameof(ShowEdit), nameof(ShowLogout), nameof(LoginText) })
            PropertyChanged?.Invoke(this, new(name));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
