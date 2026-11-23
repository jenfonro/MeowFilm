using Microsoft.UI.Xaml;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal enum MusicModalAnchor
{
    Active,
    Page,
    Settings,
    Player
}

internal static class MusicDialogs
{
    internal static async Task<bool> ConfirmAsync(FrameworkElement owner, string message, string title,
        CancellationToken cancellationToken = default)
    {
        var request = new DesktopDialogRequest
        {
            Title = title,
            Message = message,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = DesktopDialogDefaultButton.Close
        };
        try
        {
            return await DesktopModalHost.ShowDialogAsync(owner, request, cancellationToken, stack: true) == DesktopDialogResult.Primary;
        }
        catch (OperationCanceledException) { return false; }
    }

    internal static Task<string[]?> SelectAudioFilesAsync(FrameworkElement owner) =>
        CapabilityFilePicker.OpenMultipleAsync(owner, "添加音乐", ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma");

    internal static Task<string[]?> SelectScriptFilesAsync(FrameworkElement owner) =>
        CapabilityFilePicker.OpenMultipleAsync(owner, "导入 LX 音源", ".js");

    internal static Task<string?> SelectScriptFileAsync(FrameworkElement owner) =>
        CapabilityFilePicker.OpenAsync(owner, "更新 LX 音源", ".js");

    internal static Task<string?> SelectLyricFileAsync(FrameworkElement owner) =>
        CapabilityFilePicker.OpenAsync(owner, "导入歌词", ".lrc", ".qrc", ".yrc", ".txt");
}
