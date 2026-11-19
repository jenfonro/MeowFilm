using System.Text.Json;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using SkiaSharp;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.Capabilities.Accounts;

namespace MeowFilm.Capabilities;

/// <summary>Local pixels only. No image URL, process, browser or disk cache.</summary>
public static class MusicAccountQrImage
{
    public static WriteableBitmap DecodePng(string base64)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            AccountQrPng.Validate(bytes);
            using var decoded = SKBitmap.Decode(bytes);
            if (decoded is null) throw new MusicOperationException("INVALID_RESPONSE");
            if (decoded.Width is < 1 or > 1024 || decoded.Height is < 1 or > 1024)
                throw new MusicOperationException("LIMIT");
            using var pixels = decoded.Copy(SKColorType.Bgra8888);
            var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
            using (var output = bitmap.PixelBuffer.AsStream()) output.Write(pixels.Bytes);
            bitmap.Invalidate();
            return bitmap;
        }
        catch (MusicOperationException) { throw; }
        catch (MusicAccountException e) { throw new MusicOperationException(e.Code); }
        catch { throw new MusicOperationException("INVALID_RESPONSE"); }
    }
    public static WriteableBitmap Create(string address)
    {
        var bitmap = QrCodeImage.Create(address);
        if (bitmap.PixelWidth > 1024) throw new MusicOperationException("LIMIT");
        return bitmap;
    }
}

public sealed partial class MusicCapability
{
    private sealed record AccountQr(string Platform, string Id, long ExpiresAt);
    private AccountQr? accountQr;
    private WriteableBitmap? accountQrImage;
    public WriteableBitmap? AccountQrImage => accountQrImage;
    public bool HasAccountQr => accountQr is not null && accountQrImage is not null;
    public bool CanBeginAccountQr => CanManageAccount && !accountSaveUncertain && selectedAccount?.Platform is "wy" or "tx" or "kg";
    public bool CanCheckAccountQr => CanManageAccount && accountQr?.Platform == selectedAccount?.Platform;
    public bool CanCancelAccount => !stopping && (AccountBusy || HasAccountQr || HasAccountSms);
    public string AccountQrAccessibleName => (selectedAccount?.Name ?? "") + "登录二维码";
    internal string? AccountQrIdentity => accountQr?.Id;

    private void AccountQrBindings()
    {
        foreach (string name in new[] { nameof(AccountQrImage), nameof(HasAccountQr),
            nameof(CanBeginAccountQr), nameof(CanCheckAccountQr), nameof(CanCancelAccount), nameof(AccountQrAccessibleName),
            nameof(CanSaveAccountQr) }) Changed(name);
    }
    private void ClearAccountQr()
    {
        accountQr = null; accountQrImage = null; AccountQrBindings();
    }
    private void ReadAccountQr(JsonElement reply, string platform)
    {
        var challenge = reply.GetProperty("challenge");
        MusicDto.Require(MusicDto.Text(challenge, "platform") == platform);
        string id = MusicDto.Text(challenge, "challengeId");
        long expires = MusicDto.Integer(challenge, "expiresAt");
        WriteableBitmap image;
        if (MusicDto.Text(challenge, "displayKind") == "png")
        {
            var content = challenge.GetProperty("imageBase64");
            MusicDto.Require(content.ValueKind == JsonValueKind.String);
            image = MusicAccountQrImage.DecodePng(content.GetString()!);
        }
        else
        {
            MusicDto.Require(MusicDto.Text(challenge, "displayKind") == "url");
            image = MusicAccountQrImage.Create(MusicDto.Text(challenge, "url"));
        }
        accountQr = new(platform, id, expires); accountQrImage = image;
        accountStatus = "等待扫码"; AccountQrBindings();
    }
    public Task BeginAccountQrAsync()
    {
        if (!CanBeginAccountQr) return Task.CompletedTask;
        string platform = selectedAccount!.Platform;
        return ChangeAccountAsync(platform, "qr.begin", new { platform });
    }
    public Task CheckAccountQrAsync(CancellationToken token = default)
    {
        var challenge = accountQr;
        if (!CanCheckAccountQr || challenge is null) return Task.CompletedTask;
        return ChangeAccountAsync(challenge.Platform, "qr.poll",
            new { platform = challenge.Platform, challengeId = challenge.Id }, token: token);
    }
    private bool PublishQrPoll(JsonElement reply, string platform)
    {
        MusicDto.Require(MusicDto.Text(reply, "platform") == platform && accountQr is not null &&
            MusicDto.Text(reply, "challengeId") == accountQr.Id);
        string state = MusicDto.Text(reply, "status");
        MusicDto.Require(state is "waiting" or "confirming" or "expired" or "refused" or "authenticated");
        if (state == "authenticated") { ClearAccountQr(); return true; }
        accountStatus = state switch { "waiting" => "等待扫码", "confirming" => "等待确认",
            "refused" => "已拒绝登录", _ => "二维码已过期" };
        if (state is "expired" or "refused") ClearAccountQr();
        return false;
    }
    public Task LeaveAccountQrAsync() => HasAccountQr || accountQrOperation ? CancelAccountAsync() : Task.CompletedTask;
}
