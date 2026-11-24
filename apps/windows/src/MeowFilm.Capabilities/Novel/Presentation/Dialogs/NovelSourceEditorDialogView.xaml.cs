using System.IO;
using MeowFilm.Core.Debug;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed record NovelProtocolChoice(string Key, string Title);

public sealed partial class NovelSourceEditorDialogView : UserControl
{
    private readonly NovelCapability module;
    private readonly Func<CancellationToken, Task> importFileAsync;
    private readonly Func<CancellationToken, Task> importUrlAsync;
    private readonly Func<CancellationToken, Task> importClipboardAsync;
    private readonly CancellationToken token;
    private readonly List<Task> exports = [];
    private Task? activeOperation;
    private NovelSource? source;
    private bool selectingProtocol, busy, continuing;
    private Func<CancellationToken, Task>? importAfterClose;

    private NovelSourceEditorDialogView(
        NovelCapability module,
        NovelSource? source,
        string protocol,
        string? definition,
        Func<CancellationToken, Task> importFileAsync,
        Func<CancellationToken, Task> importUrlAsync,
        Func<CancellationToken, Task> importClipboardAsync,
        CancellationToken token)
    {
        this.module = module;
        this.source = source;
        this.importFileAsync = importFileAsync;
        this.importUrlAsync = importUrlAsync;
        this.importClipboardAsync = importClipboardAsync;
        this.token = token;
        InitializeComponent();

        Editor.Load(protocol, definition);
        selectingProtocol = true;
        ProtocolBox.ItemsSource = NovelSourceEditor.Protocols.Select(p => new NovelProtocolChoice(p, NovelSourceFields.Title(p))).ToArray();
        ProtocolBox.SelectedValue = protocol;
        selectingProtocol = false;
    }

    internal static async Task<Func<CancellationToken, Task>?> ShowAsync(
        FrameworkElement owner,
        NovelCapability module,
        NovelSource? source,
        string protocol,
        string? definition,
        Func<CancellationToken, Task> importFileAsync,
        Func<CancellationToken, Task> importUrlAsync,
        Func<CancellationToken, Task> importClipboardAsync,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (DesktopModalHost.Find(owner) is not { } host) return null;
        var dialog = new NovelSourceEditorDialogView(
            module, source, protocol, definition, importFileAsync, importUrlAsync, importClipboardAsync, token);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        try
        {
            host.Show(
                owner,
                dialog,
                onClosed: () =>
                {
                    registration.Dispose();
                    completion.TrySetResult();
                },
                dismissOnOutside: true,
                onCloseRequested: dialog.RequestClose);
            if (token.CanBeCanceled && !completion.Task.IsCompleted)
            {
                registration = token.Register(() => owner.DispatcherQueue?.TryEnqueue(() => host.Close(dialog)));
                if (completion.Task.IsCompleted) registration.Dispose();
            }
            await completion.Task;
        }
        finally
        {
            registration.Dispose();
            if (dialog.activeOperation is { } running)
            {
                try { await running; }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    DebugLog.Warn(DebugCategories.Novel, "source.editor", "close.drain_error", error: error);
                }
            }
            await Task.WhenAll(dialog.exports);
        }
        return dialog.importAfterClose;
    }

    private Task Track(Task operation)
    {
        activeOperation = operation;
        return operation;
    }

    private void RequestClose() => _ = Track(ContinueWithUnsavedPromptAsync(() => DesktopModalHost.CloseFor(this, this)));

    private async Task<bool> SaveCoreAsync()
    {
        if (busy) return false;
        busy = true;
        SetControlsEnabled(false);
        try
        {
            string text = Editor.Draft.Definition();
            var result = source is null
                ? await module.Sources.SaveDraftAsync(Editor.Draft.Protocol, text, token)
                : await module.Sources.SaveAsync(source, text, token);
            source = NovelJson.Decode<NovelSource>(result.GetProperty("source"));
            Editor.Draft.MarkSaved(text);
            module.Notify("书源已保存");
            return true;
        }
        catch (OperationCanceledException)
        {
            DesktopModalHost.CloseFor(this, this);
            return false;
        }
        catch (Exception error)
        {
            Notices.Show(this, error is InvalidDataException ? error.Message : "操作失败，请检查输入或重试");
            return false;
        }
        finally
        {
            busy = false;
            SetControlsEnabled(true);
        }
    }

    private async Task ContinueWithUnsavedPromptAsync(Action continuation)
    {
        if (continuing || busy) return;
        continuing = true;
        SetControlsEnabled(false);
        try
        {
            if (Editor.Draft.HasInput)
            {
                var confirm = new DesktopDialogRequest
                {
                    Title = "保存更改",
                    Message = "当前更改未保存，是否保存",
                    PrimaryButtonText = "是",
                    CloseButtonText = "否",
                    DefaultButton = DesktopDialogDefaultButton.Primary
                };
                var choice = await DesktopModalHost.ShowDialogAsync(this, confirm, token, stack: true);
                if (token.IsCancellationRequested) return;
                if (choice == DesktopDialogResult.Primary && !await SaveCoreAsync()) return;
            }
            if (!token.IsCancellationRequested) continuation();
        }
        catch (OperationCanceledException) { DesktopModalHost.CloseFor(this, this); }
        catch (Exception error) { Notices.Show(this, error is InvalidDataException ? error.Message : "操作失败，请重试"); }
        finally
        {
            continuing = false;
            SetControlsEnabled(true);
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        Editor.IsEnabled = enabled;
        ProtocolBox.IsEnabled = enabled;
        MoreButton.IsEnabled = enabled;
        ImportButton.IsEnabled = enabled;
        ExportButton.IsEnabled = enabled;
        CloseButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
        CancelButton.IsEnabled = enabled;
    }

    private async void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (selectingProtocol || ProtocolBox.SelectedValue is not string next || next == Editor.Draft.Protocol) return;
        await Track(ContinueWithUnsavedPromptAsync(() => { Editor.ChangeProtocol(next); source = null; }));
        selectingProtocol = true;
        ProtocolBox.SelectedValue = Editor.Draft.Protocol;
        selectingProtocol = false;
    }

    private void CopySource_Click(object sender, RoutedEventArgs e)
    {
        try { NovelPresentation.CopyText(Editor.Draft.Definition()); module.Notify("书源已复制"); }
        catch (Exception error) { module.Notify(error.Message); }
    }

    private void ImportFile_Click(object sender, RoutedEventArgs e) => QueueImport(importFileAsync);
    private void ImportUrl_Click(object sender, RoutedEventArgs e) => QueueImport(importUrlAsync);
    private void ImportClipboard_Click(object sender, RoutedEventArgs e) => QueueImport(importClipboardAsync);

    private void QueueImport(Func<CancellationToken, Task> operation) =>
        _ = Track(ContinueWithUnsavedPromptAsync(() =>
        {
            importAfterClose = operation;
            DesktopModalHost.CloseFor(this, this);
        }));

    private void ExportDraft_Click(object sender, RoutedEventArgs e) => exports.Add(ExportDraftAsync());

    private async Task ExportDraftAsync()
    {
        try
        {
            string? path = await NovelPresentation.SaveJsonAsync(this, "book-source.json");
            token.ThrowIfCancellationRequested();
            if (path is not null)
            {
                await File.WriteAllTextAsync(path, Editor.Draft.Definition(), token);
                module.Notify("书源已导出");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { module.Notify(error.Message); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await Track(SaveCoreAsync());
    private void Cancel_Click(object sender, RoutedEventArgs e) => RequestClose();
}
