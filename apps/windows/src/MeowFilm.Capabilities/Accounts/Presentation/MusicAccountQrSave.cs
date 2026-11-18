using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using SkiaSharp;

namespace MeowFilm.Capabilities;

/// <summary>Optional trusted native dialog seam. Only invoked by explicit QR-save action.</summary>
public interface IMusicAccountQrSaveDialog
{
    string? SelectQrPngPath();
}

public sealed partial class MusicCapability
{
    private bool accountQrSaving;
    public bool CanSaveAccountQr => !stopping && !accountQrSaving && !AccountBusy && HasAccountQr &&
        accountQr!.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async Task SaveAccountQrAsync()
    {
        if (!CanSaveAccountQr) return;
        var challenge = accountQr;
        var image = accountQrImage;
        long epoch = accountEpoch;
        bool Current() => !stopping && epoch == accountEpoch && ReferenceEquals(accountQr, challenge) &&
            image is not null && ReferenceEquals(image, accountQrImage) &&
            challenge!.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        accountQrSaving = true; AccountBindings();
        try
        {
            if (!dialogs.Confirm("保存此登录二维码？", "保存二维码") || !Current()) return;
            string? path;
            if (dialogs is IMusicAccountQrSaveDialog picker) path = picker.SelectQrPngPath();
            else
            {
                path = await CapabilityFilePicker.SaveAsync(SettingsView, "保存二维码", "meowfilm-login-qr.png", ".png");
            }
            if (path is null || !Current()) return;
            // New file only: no silent overwrite, stream/ADS, device path, network share or reparse parent.
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
                path.StartsWith("//", StringComparison.Ordinal) || path.Length > 4096 ||
                path.Skip(2).Contains(':') || !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
                throw new IOException();
            path = Path.GetFullPath(path);
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null; parent = parent.Parent)
                if (!parent.Exists || (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException();
            if (File.Exists(path) || Directory.Exists(path))
            { if (Current()) { accountStatus = "文件已存在"; AccountBindings(); } return; }
            using var bytes = new MemoryStream();
            using (var bitmap = new SKBitmap(new SKImageInfo(image!.PixelWidth, image.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul)))
            {
                using var pixels = image.PixelBuffer.AsStream();
                byte[] data = new byte[bitmap.ByteCount]; pixels.ReadExactly(data);
                try { System.Runtime.InteropServices.Marshal.Copy(data, 0, bitmap.GetPixels(), data.Length); }
                finally { Array.Clear(data); }
                using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                encoded.SaveTo(bytes);
            }
            if (!Current()) return;
            // Encoding is complete before any I/O. Synchronous publication cannot pump
            // a modal UI callback between its final epoch check and CreateNew/write.
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384,
                FileOptions.WriteThrough))
            { bytes.Position = 0; bytes.CopyTo(output); output.Flush(flushToDisk: true); }
            if (Current()) { accountStatus = "二维码已保存"; AccountBindings(); }
        }
        catch { if (Current()) { accountStatus = "二维码保存失败"; AccountBindings(); } }
        finally { accountQrSaving = false; AccountBindings(); }
        return;
    }
}
