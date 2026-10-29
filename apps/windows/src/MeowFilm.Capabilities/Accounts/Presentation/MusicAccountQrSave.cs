using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

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

    public Task SaveAccountQrAsync()
    {
        if (!CanSaveAccountQr) return Task.CompletedTask;
        var challenge = accountQr;
        var image = accountQrImage;
        long epoch = accountEpoch;
        bool Current() => !stopping && epoch == accountEpoch && ReferenceEquals(accountQr, challenge) &&
            image is not null && ReferenceEquals(image, accountQrImage) &&
            challenge!.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        accountQrSaving = true; AccountBindings();
        try
        {
            if (!dialogs.Confirm("保存此登录二维码？", "保存二维码") || !Current()) return Task.CompletedTask;
            string? path;
            if (dialogs is IMusicAccountQrSaveDialog picker) path = picker.SelectQrPngPath();
            else
            {
                var dialog = new SaveFileDialog { Title = "保存二维码", Filter = "PNG 图片|*.png",
                    FileName = "meowfilm-login-qr.png", DefaultExt = ".png", AddExtension = true,
                    CheckPathExists = true, ValidateNames = true, OverwritePrompt = false };
                path = dialog.ShowDialog() == true ? dialog.FileName : null;
            }
            if (path is null || !Current()) return Task.CompletedTask;
            // New file only: no silent overwrite, stream/ADS, device path, network share or reparse parent.
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
                path.StartsWith("//", StringComparison.Ordinal) || path.Length > 4096 ||
                path.Skip(2).Contains(':') || !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
                throw new IOException();
            path = Path.GetFullPath(path);
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null; parent = parent.Parent)
                if (!parent.Exists || (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException();
            if (File.Exists(path) || Directory.Exists(path))
            { if (Current()) { accountStatus = "文件已存在"; AccountBindings(); } return Task.CompletedTask; }
            using var bytes = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image!));
            encoder.Save(bytes);
            if (bytes.Length > 524288) throw new IOException();
            if (!Current()) return Task.CompletedTask;
            // Encoding is complete before any I/O. Bounded synchronous publication cannot pump
            // a modal UI callback between its final epoch check and CreateNew/write.
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384,
                FileOptions.WriteThrough))
            { bytes.Position = 0; bytes.CopyTo(output); output.Flush(flushToDisk: true); }
            if (Current()) { accountStatus = "二维码已保存"; AccountBindings(); }
        }
        catch { if (Current()) { accountStatus = "二维码保存失败"; AccountBindings(); } }
        finally { accountQrSaving = false; AccountBindings(); }
        return Task.CompletedTask;
    }
}
