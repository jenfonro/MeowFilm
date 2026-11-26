// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed partial class MusicAccountsController
{
    private bool accountTransferBusy;
    private Task accountTransferSettlement = Task.CompletedTask;
    private CancellationTokenSource? accountTransferCancellation;

    public bool CanTransferAccounts => !stopping && !disposed &&
        !AccountBusy && !HasAccountQr && !HasAccountSms && !accountQrSaving && !accountTransferBusy;

    private static string TransferPlatform(string value) =>
        value is "wy" or "tx" or "kg" ? MusicPlatformInfo.DisplayName(value) : "";

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
        accountStatus = "";
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        accountTransferCancellation = cancellation;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accountTransferSettlement = completion.Task;
        AccountBindings();
        _ = Transfer();
        return completion.Task;
        async Task Transfer()
        {
            await Task.Yield();
            var token = cancellation.Token;
            try
            {
                if (import)
                {
                    string? path = await CapabilityFilePicker.OpenAsync(ctx.ModalOwner(MusicModalAnchor.Settings), "导入账号", ".json");
                    if (path is null) return;
                    token.ThrowIfCancellationRequested();
                    MusicAccountTransferPlan plan;
                    using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous))
                    using (var file = await MusicAccountTransferFile.ReadAsync(input, token))
                        plan = await application.Accounts.PrepareAccountTransferAsync(file, token);
                    using (plan)
                    {
                        token.ThrowIfCancellationRequested();
                        var policy = plan.Conflicts.Count == 0 ? MusicAccountTransferPolicy.Overwrite :
                            await ChooseTransferPolicy(plan.Conflicts, token);
                        token.ThrowIfCancellationRequested();
                        if (policy == MusicAccountTransferPolicy.Cancel) return;
                        var summary = await application.Accounts.ImportAccountTransferAsync(plan, policy, token);
                        // Use the normal safe account read after settlement; never publish a stale candidate profile.
                        if (!stopping && !token.IsCancellationRequested) await ReadAccountsAsync();
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
                    using var file = await application.Accounts.ExportAccountTransferAsync(token);
                    token.ThrowIfCancellationRequested();
                    if (file is null) { accountStatus = "没有可导出的账号"; return; }
                    string? path = await CapabilityFilePicker.SaveAsync(ctx.ModalOwner(MusicModalAnchor.Settings), "导出账号", "meowfilm-music-accounts.json", ".json");
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
                AccountBindings(); completion.TrySetResult();
            }
        }
    }

    private static string TransferError(string code) => code switch
    {
        "TRANSFER_FORMAT" => "账号文件格式无效", "LIMIT" => "账号文件超出容量限制",
        "TRANSFER_FILE" => "账号文件读写失败",
        "STORAGE_CONFLICT" or "STALE_SESSION" => "账号已变更", "CANCELLED" or "CLOSED" => "已取消",
        _ => AccountError(new MusicOperationException(code))
    };

    private async Task<MusicAccountTransferPolicy> ChooseTransferPolicy(IReadOnlyList<MusicAccountTransferConflict> conflicts, CancellationToken token)
    {
        var request = new DesktopDialogRequest
        {
            Title = "导入账号",
            Message = "已有账号：" + string.Join("、", conflicts.Select(item => TransferPlatform(item.Platform))),
            PrimaryButtonText = "覆盖", SecondaryButtonText = "跳过", CloseButtonText = "取消",
            DefaultButton = DesktopDialogDefaultButton.Close
        };
        token.ThrowIfCancellationRequested();
        var choice = await DesktopModalHost.ShowDialogAsync(ctx.ModalOwner(MusicModalAnchor.Settings), request, token);
        token.ThrowIfCancellationRequested();
        return choice switch { DesktopDialogResult.Primary => MusicAccountTransferPolicy.Overwrite,
            DesktopDialogResult.Secondary => MusicAccountTransferPolicy.Skip, _ => MusicAccountTransferPolicy.Cancel };
    }
}
