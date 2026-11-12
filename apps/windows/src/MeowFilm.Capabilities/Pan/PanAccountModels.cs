using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

internal sealed class PanAccountException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

public sealed record PanCredentialField(string Id)
{
    public string Label => Id switch
    {
        "cookie" => "Cookie", "username" => "用户名", "password" => "密码",
        "authorization" => "Authorization", "refreshedAt" => "刷新时间（毫秒）",
        "refreshToken" => "Refresh Token", "deviceId" => "设备 ID",
        "accessToken" => "Access Token", "expiresAt" => "过期时间（毫秒）",
        _ => throw new BridgeException("INVALID_RESPONSE")
    };
    public bool IsNumber => Id is "refreshedAt" or "expiresAt";
    public bool IsSecret => Id is "cookie" or "password" or "authorization" or "refreshToken" or "accessToken";

    internal object Parse(string text)
    {
        if (!IsNumber) return text;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ||
            value < 0 || value > BridgeCodec.MaximumSafeInteger)
            throw new BridgeException("INVALID_REQUEST");
        return value;
    }
}

internal sealed record PanAccountSnapshot(string Provider, long Revision, bool Configured, string Status, string[] Fields)
{
    internal static PanAccountSnapshot Read(JsonElement value)
    {
        string provider = value.GetProperty("provider").GetString()!;
        long revision = value.GetProperty("revision").GetInt64();
        bool configured = value.GetProperty("configured").GetBoolean();
        string status = value.GetProperty("status").GetString()!;
        var fields = value.GetProperty("fields").EnumerateArray().Select(item => item.GetString()!).ToArray();
        if (revision < 0 || revision > BridgeCodec.MaximumSafeInteger ||
            status is not ("signedIn" or "expired" or "signedOut") ||
            !configured && status != "signedOut")
            throw new BridgeException("INVALID_RESPONSE");
        return new(provider, revision, configured, status, fields);
    }
}

public sealed class PanAccountRow : INotifyPropertyChanged
{
    internal PanAccountRow(JsonElement provider)
    {
        Provider = provider.GetProperty("provider").GetString()!;
        Name = Provider switch
        {
            "baidu" => "百度网盘", "quark" => "夸克网盘", "uc" => "UC 网盘",
            "tianyi" => "天翼云盘", "yidong" => "移动云盘",
            "quark_tv" => "夸克 TV", "uc_tv" => "UC TV",
            _ => throw new BridgeException("INVALID_RESPONSE")
        };
        CredentialFields = provider.GetProperty("credentialFields").EnumerateArray()
            .Select(value => new PanCredentialField(value.GetString()!)).ToArray();
        foreach (var field in CredentialFields) _ = field.Label;
        SupportsQr = provider.GetProperty("login").GetBoolean();
        SupportsRefresh = provider.GetProperty("refresh").GetBoolean();
    }

    public string Provider { get; }
    public string Name { get; }
    public IReadOnlyList<PanCredentialField> CredentialFields { get; }
    public bool SupportsQr { get; }
    public bool SupportsRefresh { get; }
    public long Revision => snapshot?.Revision ?? 0;
    public bool Configured => snapshot?.Configured == true;
    public string Status => snapshot?.Status ?? "signedOut";
    public string StatusText => Status switch { "signedIn" => "已登录", "expired" => "失效", _ => "未登录" };
    public bool HasSnapshot => snapshot is not null;
    public bool IsBusy => Request is not null;
    public bool CanLogin => Available && Status != "signedIn";
    public bool CanRefresh => Available && Status == "signedIn" && SupportsRefresh;
    public bool CanLogout => Available && Configured && Status is "signedIn" or "expired";
    public string LoginText => Status == "expired" ? "重新登录" : "登录";
    internal long Generation;
    internal CancellationTokenSource? Request;
    internal bool Editing;
    internal bool Active;
    private PanAccountSnapshot? snapshot;
    private bool Available => Active && HasSnapshot && !IsBusy && !Editing;

    internal void Publish(PanAccountSnapshot value)
    {
        if (value.Provider != Provider) throw new BridgeException("INVALID_RESPONSE");
        if (value.Revision < Revision) return;
        snapshot = value;
        Changed();
    }
    internal void Expire()
    {
        if (snapshot is { Configured: true } value) Publish(value with { Status = "expired" });
    }
    internal void Changed()
    {
        foreach (string name in new[] { nameof(Revision), nameof(Configured), nameof(Status), nameof(StatusText),
            nameof(HasSnapshot), nameof(IsBusy), nameof(CanLogin), nameof(CanRefresh), nameof(CanLogout), nameof(LoginText) })
            PropertyChanged?.Invoke(this, new(name));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
