// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static class MusicAccountTransferUiTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.AccountTransferCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> AccountTransferCases()
    {
        yield return Case("account transfer has one accessible button on the account title row and two native actions", async () =>
        {
            await using var f = new TransferFixture(); var dialogs = new TransferDialogs();
            await using var module = new MusicCapability(new TransferApplication(f.Owner), f.Inner, new PlaybackProbe(), dialogs);
            var view = new MusicSettingsView(module);
            view.Measure(new Size(1000, 850)); view.Arrange(new Rect(0, 0, 1000, 850)); view.UpdateLayout();
            var button = (Button)view.FindName("AccountTransferButton");
            True(Equals(button.Content, "导入导出") && AutomationProperties.GetName(button) == "导入导出", "one labeled account action");
            True(button.Parent is Grid grid && Grid.GetColumn(button) == 1 &&
                grid.Children.OfType<TextBlock>().Single().Text == "账号", "account title right side");
            Equal("导入账号,导出账号", string.Join(',', button.ContextMenu.Items.Cast<MenuItem>().Select(item => item.Header)));
            Equal(0, f.Requests.Values.Sum()); Equal(0, dialogs.Choices);
        });
        yield return Case("file transfer asks once per conflicting batch and publishes one final safe summary", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); await f.Seed(TransferFixture.Only("wy"));
            var dialogs = await TransferDialogs.Create(f); dialogs.Policy = MusicAccountTransferPolicy.Skip;
            await using var module = new MusicCapability(new TransferApplication(f.Owner), f.Inner, new PlaybackProbe(), dialogs);
            await module.ImportAccountsFileAsync();
            Equal(1, dialogs.Choices); Equal(0, f.Requests["wy"]);
            True(module.AccountStatus.Contains("已导入 2", StringComparison.Ordinal) && module.AccountStatus.Contains("已跳过 1", StringComparison.Ordinal), "one summary includes both outcomes");
            True(!module.AccountStatus.Contains("SYNTHETIC", StringComparison.Ordinal), "summary redacts secrets");
            dialogs.Policy = MusicAccountTransferPolicy.Cancel; await module.ImportAccountsFileAsync(); Equal(2, dialogs.Choices);
            Equal(2, f.Store.Writes);
        });
        yield return Case("fresh file import has no conflict prompt and safe account rows update after readback", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); var dialogs = await TransferDialogs.Create(f);
            await using var module = new MusicCapability(new TransferApplication(f.Owner), f.Inner, new PlaybackProbe(), dialogs);
            await module.ImportAccountsFileAsync(); Equal(0, dialogs.Choices);
            True(module.Accounts.All(item => item.State == "verified") && module.AccountStatus.Contains("已导入 3", StringComparison.Ordinal), "readback refreshes safe rows");
            True(module.CanTransferAccounts && dialogs.ImportSelections == 1, "busy state released");
        });
        yield return Case("delayed passive account reads preserve the final file import summary on success and failure", async () =>
        {
            foreach (bool failRead in new[] { false, true })
            {
                await using var f = new TransferFixture(); await f.Initialize();
                var dialogs = await TransferDialogs.Create(f);
                var application = new DelayedTransferApplication(f.Owner);
                await using var module = new MusicCapability(application, f.Inner, new PlaybackProbe(), dialogs);
                await module.ImportAccountsFileAsync();
                string summary = module.AccountStatus;
                True(summary.Contains("已导入 3", StringComparison.Ordinal), "completed import has its final summary");
                // A real owner notification makes the next settings read passive and necessary.
                await f.Owner.CallAsync("music.accounts.cancel", new { platform = "wy" });
                application.HoldNextRead = true; application.FailRead = failRead;
                var activation = module.ActivatePlatformSettingsAsync();
                try { await application.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { application.ReleaseRead.TrySetResult(); }
                await activation;
                Equal(summary, module.AccountStatus);
                application.FailRead = false;
                await module.ReadAccountsAsync();
                Equal("", module.AccountStatus);
            }
        });
        yield return Case("explicit account read joining a passive read clears the previous transfer notice", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize();
            var dialogs = new TransferDialogs();
            var application = new DelayedTransferApplication(f.Owner);
            await using var module = new MusicCapability(application, f.Inner, new PlaybackProbe(), dialogs);
            await module.ExportAccountsFileAsync();
            Equal("没有可导出的账号", module.AccountStatus);
            application.HoldNextRead = true;
            var activation = module.ActivatePlatformSettingsAsync();
            Task explicitRead;
            try
            {
                await application.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                explicitRead = module.ReadAccountsAsync();
            }
            finally { application.ReleaseRead.TrySetResult(); }
            await activation; await explicitRead;
            Equal("", module.AccountStatus);
        });
        yield return Case("file picker cancellation and empty export produce no file or success notice", async () =>
        {
            await using var f = new TransferFixture(); var dialogs = new TransferDialogs { ExportPath = Path.Combine(f.Root, "cancelled-export.json") };
            await using var module = new MusicCapability(new TransferApplication(f.Owner), f.Inner, new PlaybackProbe(), dialogs);
            await module.ImportAccountsFileAsync(); Equal("", module.AccountStatus);
            await module.ExportAccountsFileAsync(); Equal("没有可导出的账号", module.AccountStatus);
            Equal(0, dialogs.ExportSelections); True(!File.Exists(dialogs.ExportPath), "no empty success file");
            await f.Initialize(); await f.Seed(TransferFixture.Only("wy")); dialogs.ExportPath = null;
            await module.ExportAccountsFileAsync(); Equal(1, dialogs.ExportSelections); Equal(0, f.Requests.Values.Sum());
        });
        yield return Case("file read and write failures use the account notice without raw paths", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); await f.Seed(TransferFixture.Only("wy"));
            var dialogs = new TransferDialogs { ImportPath = Path.Combine(f.Root, "SYNTHETIC_SECRET_PATH", "missing.json"), ExportPath = f.Root };
            await using var module = new MusicCapability(new TransferApplication(f.Owner), f.Inner, new PlaybackProbe(), dialogs);
            await module.ImportAccountsFileAsync(); Equal("账号文件读写失败", module.AccountStatus);
            await module.ExportAccountsFileAsync(); Equal("账号文件读写失败", module.AccountStatus);
            Equal(0, f.Requests.Values.Sum()); Equal(0, f.Store.Writes);
        });
        yield return Case("export writes only the selected user file and preserves exact fixture credentials", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); await f.Seed();
            var dialogs = new TransferDialogs { ExportPath = Path.Combine(f.Root, "synthetic-user-export.json") };
            await using var module = new MusicCapability(new TransferApplication(f.Owner), f.Inner, new PlaybackProbe(), dialogs);
            await module.ExportAccountsFileAsync(); Equal("账号已导出", module.AccountStatus);
            True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllBytesAsync(dialogs.ExportPath!)),
                TransferFixture.Shared()), "public UI export uses owner fields");
            True(File.Exists(dialogs.ExportPath) && f.Requests.Values.Sum() == 0 && f.Store.Writes == 0, "user owns final output");
        });
        yield return Case("retiring settings cancels pending transfer and prevents old result summary or permission changes", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); await f.Seed(TransferFixture.Only("wy"));
            var old = await f.Owner.AcquireAsync("wy"); var dialogs = await TransferDialogs.Create(f); f.HoldPlatform = "wy";
            await using var module = new MusicCapability(new TransferApplication(f.Owner), f.Inner, new PlaybackProbe(), dialogs);
            var importing = module.ImportAccountsFileAsync(); await f.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await module.RetireAccountTransferAsync(); await importing;
            True(f.Owner.IsCurrent(old!) && f.Store.Writes == 0 && module.CanTransferAccounts, "retired candidate never fences old account");
            Equal("", module.AccountStatus); Equal(0, f.Requests["tx"]); Equal(0, f.Requests["kg"]);
        });
        yield return Case("unknown file import notice keeps normal close blocked without automatic reconciliation", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); f.Store.LoseReceipt = true;
            var dialogs = await TransferDialogs.Create(f);
            var module = new MusicCapability(new TransferApplication(f.Owner), f.Inner, new PlaybackProbe(), dialogs);
            try
            {
                await module.ImportAccountsFileAsync();
                True(module.AccountStatus.Contains("保存结果未确认", StringComparison.Ordinal) && !module.CanTransferAccounts, "unknown receipt visible and sealed");
                try { await module.PrepareShutdownAsync(); throw new InvalidOperationException("Expected unknown close failure"); }
                catch (MusicOperationException error) when (error.Code == "COMMIT_UNKNOWN") { }
            }
            finally { await module.AbortAsync(); }
        });
    }
    private sealed class TransferDialogs : IMusicDialogs, IMusicAccountTransferDialogs
    {
        internal string? ImportPath, ExportPath;
        internal int Choices, ImportSelections, ExportSelections;
        internal MusicAccountTransferPolicy Policy = MusicAccountTransferPolicy.Overwrite;
        public bool Confirm(string message, string title) => true;
        public string[]? SelectAudioFiles() => null;
        public string? SelectAccountImportPath() { ImportSelections++; return ImportPath; }
        public string? SelectAccountExportPath() { ExportSelections++; return ExportPath; }
        public MusicAccountTransferPolicy ChooseAccountTransferPolicy(IReadOnlyList<MusicAccountTransferConflict> conflicts)
        { Choices++; return Policy; }
        internal static async Task<TransferDialogs> Create(TransferFixture fixture)
        {
            string path = Path.Combine(fixture.Root, "synthetic-user-input.json");
            await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(TransferFixture.Shared()));
            return new() { ImportPath = path };
        }
    }
    private class TransferApplication(MusicAccountOwner owner) : MusicApplicationTestDouble, IMusicAccountTransferSource
    {
        public override Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
            method.StartsWith("music.accounts.", StringComparison.Ordinal) ? owner.CallAsync(method, parameters, cancellationToken) :
                Task.FromResult(BridgeCodec.Value(new { ok = true }));
        public Task<MusicAccountTransferPlan> PrepareAccountTransferAsync(MusicAccountTransferFile file, CancellationToken token = default) =>
            owner.PrepareAccountTransferAsync(file, token);
        public Task<MusicAccountTransferSummary> ImportAccountTransferAsync(MusicAccountTransferPlan plan, MusicAccountTransferPolicy policy,
            CancellationToken token = default) => owner.ImportAccountTransferAsync(plan, policy, token);
        public Task<MusicAccountTransferFile?> ExportAccountTransferAsync(CancellationToken token = default) => owner.ExportAccountTransferAsync(token);
    }
    private sealed class DelayedTransferApplication : TransferApplication
    {
        private readonly MusicAccountOwner owner;
        internal DelayedTransferApplication(MusicAccountOwner owner) : base(owner) { this.owner = owner; }
        internal bool HoldNextRead, FailRead;
        internal readonly TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override event Action<string>? AccountDetailsInvalidated
        {
            add => owner.SessionChanged += value;
            remove => owner.SessionChanged -= value;
        }
        public override async Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            if (method == "music.accounts.list" && HoldNextRead)
            {
                HoldNextRead = false; ReadEntered.TrySetResult();
                await ReleaseRead.Task.WaitAsync(cancellationToken);
                if (FailRead) throw new MusicOperationException("STORAGE");
            }
            return await base.ExecuteAsync(method, parameters, cancellationToken);
        }
    }
}
