using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class MusicSourcesSettingsTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.SourcesSettingsCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> SourcesSettingsCases()
    {
        yield return Case("source settings bind real official and installed script sources without fixture rows", async () =>
        {
            await using var f = await Fixture.Create();
            var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view);
            var sources = (MusicSourcesSettingsView)view.FindName("SourcesPage");
            var category = Visuals(view).OfType<RadioButton>().Single(button => Equals(button.Content, "音源"));
            category.IsChecked = true;
            await Until(() => f.Calls.Count("music.officialSources.settings.get") > 0 && !f.Module.Busy);
            Equal(3, f.Module.OfficialSourceRows.Count);
            True(f.Module.OfficialSourceRows.Select(row => row.Name)
                .SequenceEqual(["网易云音乐", "QQ 音乐", "酷狗音乐"]), "the three official providers have separate identities");
            True(f.Module.OfficialSourceRows.All(row => row.Enabled) && f.Module.Sources.Count == 0,
                "native defaults are visible without script fixtures");
            True(Visuals(sources).OfType<Button>().Any(button => Equals(button.Content, "导入音源")) &&
                Visuals(sources).OfType<Button>().Any(button => Equals(button.Content, "下载导入")),
                "local and network source installation are available");
            Equal(3, ((ItemsControl)sources.FindName("SourceList")).Items.Count);
            var qq = f.Module.OfficialSourceRows.Single(row => row.Platform == "tx");
            var toggle = Visuals(sources).OfType<CheckBox>().Single(box => ReferenceEquals(box.DataContext, qq));
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => !f.Module.Busy && !qq.Enabled);
            Equal(1, f.Calls.Count("music.officialSources.settings.save"));
            True(Visuals(sources).OfType<Button>().All(button => !ReferenceEquals(button.DataContext, qq)),
                "built-in source rows expose no script update or delete action");
        });

        yield return Case("source settings persist independent official platform switches through the real owner", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.RefreshSourceManagementAsync();
            await Until(() => f.Module.CanEdit);
            var qq = f.Module.OfficialSourceRows.Single(row => row.Platform == "tx");
            await f.Module.SetOfficialSourceEnabledAsync(qq, false);
            await Until(() => f.Module.CanEdit);
            True(!qq.Enabled && f.Module.OfficialSourceRows.Single(row => row.Platform == "wy").Enabled &&
                f.Module.OfficialSourceRows.Single(row => row.Platform == "kg").Enabled,
                "only the selected official provider changes");
            var persisted = await f.Rpc("music.officialSources.settings.get");
            True(persisted.GetProperty("settings").GetProperty("enabled").EnumerateArray()
                .Select(value => value.GetString()).SequenceEqual(["wy", "kg"]),
                "the user setting is durably stored");
            await f.Module.RefreshSourceManagementAsync();
            await Until(() => f.Module.CanEdit);
            True(!f.Module.OfficialSourceRows.Single(row => row.Platform == "tx").Enabled,
                "refresh restores the saved switch");
        });

        yield return Case("source settings update rollback select and remove the captured real installation", async () =>
        {
            await using var f = await Fixture.Create();
            await InstallSettingsSources(f, 1);
            var settings = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(settings);
            ReplicaAnchors(settings)[^1].IsChecked = true;
            await Until(() => f.Module.CanEdit);
            var view = (MusicSourcesSettingsView)settings.FindName("SourcesPage");
            Button More() => Visuals(view).OfType<Button>().Single(button =>
                button.DataContext is MusicSourceRow && Equals(button.Content, "更多"));
            async Task Choose(string title)
            {
                var button = More();
                Click(button); await Drain();
                var item = button.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, title));
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Until(() => f.Module.CanEdit);
                view.UpdateLayout();
            }
            var toggle = Visuals(view).OfType<CheckBox>().Single(box => box.DataContext is MusicSourceRow);
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => f.Module.CanEdit && f.Module.Sources.Single().Enabled);
            await Choose("使用 · 网易云");
            True(!f.Module.UsesOfficialSources, "the native settings accepted the script platform");
            Click(FindButton(view, "使用内置音源"));
            await Until(() => f.Module.CanEdit && f.Module.UsesOfficialSources);
            f.Dialogs.ScriptPath = Path.Combine(f.Root, "update.js");
            File.WriteAllText(f.Dialogs.ScriptPath, TestScript(version: "2"), new UTF8Encoding(false));
            await Choose("更新脚本");
            Equal("2", f.Module.Sources.Single().Version);
            True(f.Module.Sources.Single().RollbackAvailable, "the actual previous script is retained");
            await Choose("回退版本");
            Equal("1", f.Module.Sources.Single().Version);
            await Choose("删除音源");
            Equal(0, f.Module.Sources.Count);
            True(f.Calls.Count("music.sources.update") == 1 && f.Calls.Count("music.sources.rollback") == 1 &&
                f.Calls.Count("music.sources.remove") == 1, "row actions reach the persistent native owner");
        });

        yield return Case("source settings retire detached menus when hidden and keep the unified list scrollable", async () =>
        {
            await using var f = await Fixture.Create();
            await InstallSettingsSources(f, 6);
            var settings = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(settings);
            mount.Window.Width = 620; mount.Window.Height = 430;
            ReplicaAnchors(settings)[^1].IsChecked = true;
            await Until(() => f.Module.CanEdit); await Drain();
            var view = (MusicSourcesSettingsView)settings.FindName("SourcesPage");
            var list = (ItemsControl)view.FindName("SourceList");
            Equal(9, list.Items.Count);
            var scroll = (ScrollViewer)list.Template.FindName("ItemsScroll", list);
            True(scroll.ViewportHeight >= 76 && scroll.ScrollableHeight > 0, "a visible row and independent scroll survive a short host");
            scroll.ScrollToVerticalOffset(228); view.UpdateLayout(); await Drain();
            var button = Visuals(view).OfType<Button>().First(item =>
                item.DataContext is MusicSourceRow && Equals(item.Content, "更多"));
            Click(button); await Drain();
            var stale = button.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "删除音源"));
            ReplicaAnchors(settings)[0].IsChecked = true; await Drain();
            stale.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Equal(0, f.Calls.Count("music.sources.remove"));
            Equal(6, f.Module.Sources.Count);
        });

        yield return Case("source settings install a one MiB HTTPS script using the native staged transfer", async () =>
        {
            byte[] bytes = Encoding.UTF8.GetBytes("// @name Network Source\n// @version 1.2\n".PadRight(1024 * 1024));
            var transport = new SourceDownloadHandler(new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
            await using var f = await Fixture.Create(sourceNetworkDownload: new(transport));
            await f.Module.ImportSourceFromUrlAsync("https://sources.invalid/source.js");
            await Until(() => f.Module.CanEdit);
            Equal(1, f.Module.Sources.Count);
            Equal("Network Source", f.Module.Sources[0].Name);
            Equal("1.2", f.Module.Sources[0].Version);
            var detail = await f.Rpc("music.sources.detail", new { installationId = f.Module.Sources[0].InstallationId });
            Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)),
                detail.GetProperty("item").GetProperty("contentHash").GetString());
            True(f.Calls.Count("music.transfer.begin") == 1 && f.Calls.Count("music.transfer.chunk") > 1 &&
                f.Calls.Count("music.sources.install") == 1 && !f.Runtime.HasStarted,
                "downloaded bytes use the normal validated install path without executing the script");
        });

        yield return Case("source settings retain a large local import and a further network installation", async () =>
        {
            var transport = new SourceDownloadHandler(new(HttpStatusCode.OK)
            {
                Content = new StringContent("// @name Downloaded Source\n// @version 1\n", Encoding.UTF8)
            });
            await using var f = await Fixture.Create(sourceNetworkDownload: new(transport));
            await InstallSettingsSources(f, 40);
            await Until(() => f.Module.CanEdit);
            Equal(40, f.Module.Sources.Count);
            True(f.Module.Sources.Select(source => source.Name)
                .SequenceEqual(Enumerable.Range(0, 40).Select(index => "测试音源 " + index)),
                "local multi-selection preserves every installation in selection order");
            await f.Module.ImportSourceFromUrlAsync("https://sources.invalid/source.js");
            await Until(() => f.Module.CanEdit);
            await f.Module.RefreshSourceManagementAsync();
            await Until(() => f.Module.CanEdit);
            Equal(41, f.Module.Sources.Count);
            Equal("Downloaded Source", f.Module.Sources[^1].Name);
            Equal(41, f.Module.Sources.Select(source => source.InstallationId).Distinct().Count());
            Equal(41, f.Calls.Count("music.sources.install"));
            True(f.Module.Sources.All(source => !source.Enabled) && !f.Runtime.HasStarted,
                "import persists all sources without enabling them or starting Node");
        });

        yield return Case("source download rejects redirects and oversize responses without staged partial installs", async () =>
        {
            foreach (var response in new[]
            {
                new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://elsewhere.invalid/source.js") } },
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[16 * 1024 * 1024 + 1]) }
            })
            {
                var transport = new SourceDownloadHandler(response);
                await using var f = await Fixture.Create(sourceNetworkDownload: new(transport));
                await f.Module.ImportSourceFromUrlAsync("https://sources.invalid/source.js");
                await Until(() => f.Module.CanEdit);
                Equal(0, f.Module.Sources.Count);
                Equal(0, f.Calls.Count("music.transfer.begin"));
                True(f.Module.Status.Length > 0, "the download failure is reported");
            }
        });

        yield return Case("source download cancellation drains the request and leaves no transfer stage", async () =>
        {
            var transport = new SourceDownloadHandler();
            await using var f = await Fixture.Create(sourceNetworkDownload: new(transport));
            var download = f.Module.ImportSourceFromUrlAsync("https://sources.invalid/source.js");
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.CancelOnlineAsync();
            await download.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => f.Module.CanEdit);
            Equal(0, f.Module.Sources.Count);
            Equal(0, f.Calls.Count("music.transfer.begin"));
            True(transport.Cancelled, "the active HTTP request observed cancellation");
        });
    }

    private static async Task InstallSettingsSources(Fixture f, int count)
    {
        f.Dialogs.ScriptPaths = Enumerable.Range(0, count).Select(index =>
        {
            var path = Path.Combine(f.Root, $"settings-source-{index}.js");
            File.WriteAllText(path, TestScript(name: "测试音源 " + index), new UTF8Encoding(false));
            return path;
        }).ToArray();
        try { await f.Module.ImportSourceAsync(); }
        finally { f.Dialogs.ScriptPaths = null; }
    }

    private sealed class SourceDownloadHandler(HttpResponseMessage? response = null) : HttpMessageHandler
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Entered.TrySetResult();
            if (response is null)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return response!;
        }
    }
}
