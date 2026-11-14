// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Desktop;
using Microsoft.Win32;

namespace MeowFilm.Capabilities;

/// <summary>Safe UI/dialog seam; the dialog never receives file credentials.</summary>
public interface IMusicAccountTransferDialogs
{
    string? SelectAccountImportPath();
    string? SelectAccountExportPath();
    MusicAccountTransferPolicy ChooseAccountTransferPolicy(IReadOnlyList<MusicAccountTransferConflict> conflicts);
}

public sealed partial class MusicCapability
{
    private bool accountTransferBusy;
    private Task accountTransferSettlement = Task.CompletedTask;
    private CancellationTokenSource? accountTransferCancellation;
    public bool CanTransferAccounts => application is IMusicAccountTransferSource && !stopping && !disposed &&
        !AccountBusy && !HasAccountQr && !HasAccountSms && !accountQrSaving && !accountTransferBusy && !accountSaveUncertain;
    private void AccountTransferBindings()
    { Changed(nameof(CanTransferAccounts)); AccountBindings(); }
    private static string TransferPlatform(string value) => value switch
    { "wy" => "网易云音乐", "tx" => "QQ 音乐", "kg" => "酷狗音乐", _ => "" };
    internal async Task RetireAccountTransferAsync()
    {
        accountTransferCancellation?.Cancel();
        await accountTransferSettlement;
    }
    public Task ImportAccountsFileAsync() => StartAccountTransfer(import: true);
    public Task ExportAccountsFileAsync() => StartAccountTransfer(import: false);
    private Task StartAccountTransfer(bool import)
    {
        if (!CanTransferAccounts) return Task.CompletedTask;
        accountTransferBusy = true;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        accountTransferCancellation = cancellation;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accountTransferSettlement = completion.Task;
        AccountTransferBindings();
        _ = Transfer();
        return completion.Task;
        async Task Transfer()
        {
            await Task.Yield();
            var token = cancellation.Token;
            try
            {
                var source = (IMusicAccountTransferSource)application;
                if (import)
                {
                    string? path = dialogs is IMusicAccountTransferDialogs picker ? picker.SelectAccountImportPath() : PickImport();
                    if (path is null) return;
                    token.ThrowIfCancellationRequested();
                    MusicAccountTransferPlan plan;
                    using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous))
                    using (var file = await MusicAccountTransferFile.ReadAsync(input, token))
                        plan = await source.PrepareAccountTransferAsync(file, token);
                    using (plan)
                    {
                        token.ThrowIfCancellationRequested();
                        var policy = plan.Conflicts.Count == 0 ? MusicAccountTransferPolicy.Overwrite :
                            dialogs is IMusicAccountTransferDialogs chooser ? chooser.ChooseAccountTransferPolicy(plan.Conflicts) : ChooseTransferPolicy(plan.Conflicts, token);
                        token.ThrowIfCancellationRequested();
                        if (policy == MusicAccountTransferPolicy.Cancel) return;
                        var summary = await source.ImportAccountTransferAsync(plan, policy, token);
                        bool unknown = summary.Items.Any(item => item.Code == "COMMIT_UNKNOWN");
                        if (unknown) accountSaveUncertain = true;
                        // Use the normal safe account read after settlement; never publish a stale candidate profile.
                        if (!unknown && !stopping && !token.IsCancellationRequested) await ReadAccountsAsync();
                        if (!stopping && !token.IsCancellationRequested)
                        {
                            string Part(string status, string title) => title + " " + summary.Items.Count(item => item.Status == status) +
                                (summary.Items.Any(item => item.Status == status) ? "（" + string.Join("、", summary.Items.Where(item => item.Status == status)
                                    .Select(item => TransferPlatform(item.Platform) + (item.Code is null ? "" : "：" + TransferError(item.Code)))) + "）" : "");
                            accountStatus = string.Join("；", Part("imported", "已导入"), Part("skipped", "已跳过"),
                                Part("failed", "失败"), Part("cancelled", "已取消"));
                        }
                    }
                }
                else
                {
                    using var file = await source.ExportAccountTransferAsync(token);
                    token.ThrowIfCancellationRequested();
                    if (file is null) { accountStatus = "没有可导出的账号"; return; }
                    string? path = dialogs is IMusicAccountTransferDialogs picker ? picker.SelectAccountExportPath() : PickExport();
                    if (path is null) return;
                    token.ThrowIfCancellationRequested();
                    using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous))
                        await file.WriteAsync(output, token);
                    if (!stopping && !token.IsCancellationRequested) accountStatus = "账号已导出";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!stopping && !token.IsCancellationRequested)
                    accountStatus = error is MusicAccountException known ? TransferError(known.Code) : "账号文件读写失败";
            }
            finally
            {
                accountTransferCancellation = null; cancellation.Dispose(); accountTransferBusy = false;
                AccountTransferBindings(); completion.TrySetResult();
            }
        }
    }
    private static string TransferError(string code) => code switch
    {
        "TRANSFER_FORMAT" => "账号文件格式无效", "LIMIT" => "账号文件超出容量限制",
        "TRANSFER_FILE" => "账号文件读写失败", "COMMIT_UNKNOWN" => "保存结果未确认，请读取账号状态",
        "STORAGE_CONFLICT" or "STALE_SESSION" => "账号已变更", "CANCELLED" or "CLOSED" => "已取消",
        _ => AccountError(new MusicOperationException(code))
    };
    private static string? PickImport()
    {
        var dialog = new OpenFileDialog { Title = "导入账号", Filter = "JSON 文件|*.json", Multiselect = false, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    private static string? PickExport()
    {
        var dialog = new SaveFileDialog { Title = "导出账号", Filter = "JSON 文件|*.json", FileName = "meowfilm-music-accounts.json",
            DefaultExt = ".json", AddExtension = true, CheckPathExists = true, OverwritePrompt = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    private static MusicAccountTransferPolicy ChooseTransferPolicy(IReadOnlyList<MusicAccountTransferConflict> conflicts, CancellationToken token)
    {
        var choice = MusicAccountTransferPolicy.Cancel;
        var dialog = new DesktopWindow { Title = "导入账号", SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current?.MainWindow,
            ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(20), MinWidth = 320 };
        panel.Children.Add(new TextBlock { Text = "已有账号：" + string.Join("、", conflicts.Select(item => TransferPlatform(item.Platform))),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 480, Margin = new Thickness(0, 0, 0, 16) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, policy) in new[] { ("跳过", MusicAccountTransferPolicy.Skip), ("覆盖", MusicAccountTransferPolicy.Overwrite), ("取消", MusicAccountTransferPolicy.Cancel) })
        {
            var button = new Button { Content = label, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(6, 0, 0, 0),
                IsCancel = policy == MusicAccountTransferPolicy.Cancel };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => { choice = policy; dialog.Close(); };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons); dialog.Content = panel;
        using var cancellation = token.Register(() => _ = dialog.Dispatcher.InvokeAsync(dialog.Close));
        if (!token.IsCancellationRequested) dialog.ShowDialog();
        return choice;
    }
}
