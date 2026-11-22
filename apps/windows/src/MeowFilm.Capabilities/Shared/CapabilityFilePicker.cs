using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal static class CapabilityFilePicker
{
    internal static async Task<string[]?> OpenMultipleAsync(FrameworkElement owner, string action, params string[] extensions)
    {
        var window = DesktopWindow.GetWindow(owner) ?? throw new InvalidOperationException("FILE_PICKER_OWNER_NOT_ATTACHED");
        var picker = new FileOpenPicker(window.AppWindow.Id) { CommitButtonText = action };
        foreach (string extension in extensions) picker.FileTypeFilter.Add(extension);
        var files = await picker.PickMultipleFilesAsync();
        return files.Count == 0 ? null : files.Select(file => file.Path).ToArray();
    }

    internal static async Task<string?> OpenAsync(FrameworkElement owner, string action, params string[] extensions)
    {
        var window = DesktopWindow.GetWindow(owner) ?? throw new InvalidOperationException("FILE_PICKER_OWNER_NOT_ATTACHED");
        var picker = new FileOpenPicker(window.AppWindow.Id) { CommitButtonText = action };
        foreach (string extension in extensions) picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    internal static async Task<string?> SaveAsync(FrameworkElement owner, string action, string name, string extension)
    {
        var window = DesktopWindow.GetWindow(owner) ?? throw new InvalidOperationException("FILE_PICKER_OWNER_NOT_ATTACHED");
        var picker = new FileSavePicker(window.AppWindow.Id)
        { CommitButtonText = action, SuggestedFileName = name, DefaultFileExtension = extension };
        picker.FileTypeChoices.Add(extension, new List<string> { extension });
        return (await picker.PickSaveFileAsync())?.Path;
    }
}
