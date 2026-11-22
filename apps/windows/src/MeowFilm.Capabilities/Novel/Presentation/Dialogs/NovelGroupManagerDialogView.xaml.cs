using System.Collections.ObjectModel;
using System.IO;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed partial class NovelGroupManagerDialogView : UserControl
{
    private readonly Func<string[]> readGroups;
    private readonly Func<string?, string?, CancellationToken, Task> changeGroupAsync;
    private readonly CancellationToken token;
    private readonly ObservableCollection<string> groups = [];
    private bool busy;

    private NovelGroupManagerDialogView(
        bool canAdd,
        Func<string[]> readGroups,
        Func<string?, string?, CancellationToken, Task> changeGroupAsync,
        CancellationToken token)
    {
        this.readGroups = readGroups;
        this.changeGroupAsync = changeGroupAsync;
        this.token = token;
        InitializeComponent();
        AddGroupButton.IsEnabled = canAdd;
        GroupList.ItemsSource = groups;
        Reload();
    }

    internal static Task ShowAsync(
        FrameworkElement owner,
        bool canAdd,
        Func<string[]> readGroups,
        Func<string?, string?, CancellationToken, Task> changeGroupAsync,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dialog = new NovelGroupManagerDialogView(canAdd, readGroups, changeGroupAsync, token);
        return DesktopModalHost.ShowSurfaceAsync(
            owner,
            dialog,
            dismissOnOutside: true,
            onOpened: () => dialog.DoneButton.Focus(FocusState.Programmatic),
            cancellationToken: token);
    }

    private void Reload()
    {
        groups.Clear();
        foreach (string group in readGroups()) groups.Add(group);
        EmptyText.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupList.Visibility = groups.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void AddGroup_Click(object sender, RoutedEventArgs e) => await EditGroupAsync(null);

    private async void EditGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string group })
            await EditGroupAsync(group);
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not FrameworkElement { DataContext: string group }) return;
        busy = true;
        try
        {
            var request = new DesktopDialogRequest
            {
                Title = "删除分组",
                Message = $"移除分组“{group}”？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = DesktopDialogDefaultButton.Close,
                OnPrimaryAsync = async () =>
                {
                    try
                    {
                        await changeGroupAsync(group, null, token);
                        Reload();
                        return true;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        Notices.Show(this, error is InvalidDataException ? error.Message : "删除分组失败，请重试");
                        return false;
                    }
                }
            };
            await DesktopModalHost.ShowDialogAsync(this, request, token, stack: true);
        }
        catch (OperationCanceledException) { }
        finally { busy = false; }
    }

    private async Task EditGroupAsync(string? oldGroup)
    {
        if (busy) return;
        busy = true;
        try
        {
            var inputView = new NovelImportInputDialogView("分组名称", oldGroup ?? "");
            var request = new DesktopDialogRequest
            {
                Title = oldGroup is null ? "新增分组" : "编辑分组",
                Content = inputView,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = DesktopDialogDefaultButton.Primary,
                OnOpened = () => inputView.FocusInput(),
                OnPrimaryAsync = async () =>
                {
                    try
                    {
                        string name = inputView.InputText.Trim();
                        if (NovelGroups.Split(name).Length != 1) throw new InvalidDataException("请填写单个分组名称");
                        await changeGroupAsync(oldGroup, name, token);
                        Reload();
                        return true;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        inputView.ShowError(error is InvalidDataException ? error.Message : "保存分组失败，请重试");
                        return false;
                    }
                }
            };
            await DesktopModalHost.ShowDialogAsync(this, request, token, stack: true);
        }
        catch (OperationCanceledException) { }
        finally { busy = false; }
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        if (!busy) DesktopModalHost.CloseFor(this, this);
    }
}
