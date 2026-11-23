using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Key = Windows.System.VirtualKey;
using ModifierKeys = Windows.System.VirtualKeyModifiers;

namespace MeowFilm.Desktop;

public partial class MediaControlSettingsView : UserControl
{
    private readonly IDesktopMediaControlSettings settings;
    private readonly MediaShortcutRow[] rows = Enum.GetValues<MediaShortcutAction>().Select(action => new MediaShortcutRow(action)).ToArray();
    private readonly HashSet<Key> consumedKeys = [];
    private readonly KeyEventHandler keyDownHandler, keyUpHandler;
    private DesktopWindow? window;
    private FrameworkElement? inputRoot;
    private IDisposable? visibility;
    private Button? recordingButton;
    private MediaShortcutRow? recordingRow;
    private bool recordingGlobal, subscribed, refreshing, refreshQueued;

    public MediaControlSettingsView(IDesktopMediaControlSettings settings)
    {
        this.settings = settings;
        keyDownHandler = View_KeyDown;
        keyUpHandler = View_KeyUp;
        InitializeComponent();
        DataContext = settings;
        ShortcutRows.ItemsSource = rows;
        Refresh();
    }

    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (subscribed) return;
        subscribed = true;
        visibility?.Dispose();
        visibility = DesktopVisual.ObserveVisibility(this, visible => { if (!visible) EndRecording(); });
        settings.PropertyChanged += Settings_Changed;
        inputRoot = XamlRoot.Content as FrameworkElement;
        if (inputRoot is not null)
        {
            inputRoot.AddHandler(UIElement.PreviewKeyDownEvent, keyDownHandler, true);
            inputRoot.AddHandler(UIElement.PreviewKeyUpEvent, keyUpHandler, true);
        }
        window = DesktopWindow.GetWindow(this);
        if (window is not null) window.Deactivated += Window_Deactivated;
        if (!settings.IsAvailable) await RunActionAsync(() => settings.LoadAsync(), "暂时无法加载媒体快捷键设置");
        else Refresh();
    }
    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) return;
        EndRecording();
        visibility?.Dispose();
        visibility = null;
        refreshQueued = false;
        if (!subscribed) return;
        settings.PropertyChanged -= Settings_Changed;
        if (inputRoot is not null)
        {
            inputRoot.RemoveHandler(UIElement.PreviewKeyDownEvent, keyDownHandler);
            inputRoot.RemoveHandler(UIElement.PreviewKeyUpEvent, keyUpHandler);
        }
        inputRoot = null;
        if (window is not null) window.Deactivated -= Window_Deactivated;
        window = null;
        consumedKeys.Clear();
        subscribed = false;
    }
    private void Settings_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => { if (subscribed) Settings_Changed(sender, e); });
            return;
        }
        if (!settings.IsAvailable || !settings.IsRecording) EndRecording();
        if (e.PropertyName == nameof(IDesktopMediaControlSettings.Preferences))
            foreach (var row in rows) row.SetDraftErrors("", "");
        QueueRefresh();
    }
    private void QueueRefresh()
    {
        if (refreshQueued) return;
        refreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!refreshQueued) return;
            refreshQueued = false;
            if (subscribed) Refresh();
        });
    }
    private void Refresh()
    {
        refreshQueued = false;
        foreach (var row in rows)
        {
            var binding = settings.Preferences.Bindings.Single(item => item.Action == row.Action);
            // Disabling the input owner during its save would move keyboard
            // focus and reopen native registration before the held key is up.
            bool ownsInput = ReferenceEquals(recordingButton?.DataContext, row);
            row.Apply(
                recordingRow == row && !recordingGlobal ? "录制中" : MediaShortcutGestures.Display(binding.Local),
                recordingRow == row && recordingGlobal ? "录制中" : MediaShortcutGestures.Display(binding.Global),
                settings.GlobalErrors.GetValueOrDefault(row.Action) ?? "",
                settings.CanEdit || (ownsInput && !recordingGlobal),
                settings.CanEdit || (ownsInput && recordingGlobal),
                settings.CanEdit);
        }
        RefreshButton.IsEnabled = settings.CanEdit && settings.Preferences.GlobalEnabled && !settings.IsRecording;
        ReloadButton.Visibility = string.IsNullOrWhiteSpace(settings.Status) ? Visibility.Collapsed : Visibility.Visible;
        refreshing = true;
        try
        {
            GlobalSwitch.IsChecked = settings.Preferences.GlobalEnabled;
            SystemMediaSwitch.IsChecked = settings.Preferences.SystemMediaEnabled;
        }
        finally { refreshing = false; }
    }
    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (!settings.CanEdit || sender is not Button { DataContext: MediaShortcutRow row, Tag: string column } button) return;
        EndRecording();
        // A previous session may have lost its KeyUp outside this window.
        consumedKeys.Clear();
        row.SetDraftErrors("", "");
        EditorNotice.Message = "";
        recordingRow = row; recordingGlobal = column == "global"; recordingButton = button;
        button.Focus(FocusState.Programmatic);
        settings.SetRecording(true);
        Refresh();
    }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!settings.CanEdit || sender is not Button { DataContext: MediaShortcutRow row, Tag: string column }) return;
        EndRecording();
        await SaveGesture(row, column == "global", null);
    }
    private void EndRecording()
    {
        if (recordingButton is null) return;
        recordingRow = null; recordingButton = null;
        settings.SetRecording(false);
        Refresh();
    }
    private void CompleteRecording()
    {
        // Finish the visual edit immediately, but retain the shared recording
        // gate and focus owner until every consumed key has been released.
        recordingRow = null;
        Refresh();
    }
    private void Window_Deactivated(object? sender, EventArgs e) => EndRecording();
    private void View_LostFocus(object sender, RoutedEventArgs e)
    {
        if (recordingButton is not null && !ReferenceEquals(DesktopVisual.Focused(this), recordingButton)) EndRecording();
    }
    private void View_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ProcessKey(e.OriginalSource, e.OriginalKey, DesktopVisual.Modifiers, true, e.KeyStatus.WasKeyDown)) e.Handled = true;
    }
    private void View_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (ProcessKey(e.OriginalSource, e.OriginalKey, DesktopVisual.Modifiers, false, false)) e.Handled = true;
    }
    private bool ProcessKey(object? target, Key key, ModifierKeys modifiers, bool down, bool repeat)
    {
        if (!consumedKeys.Contains(key) && (target is not DependencyObject source || !DesktopVisual.Contains(this, source))) return false;
        if (!down)
        {
            if (!consumedKeys.Remove(key) && recordingRow is null) return false;
            if (recordingRow is null && consumedKeys.Count == 0) EndRecording();
            return true;
        }
        if (consumedKeys.Contains(key)) return true;
        if (recordingRow is not { } row) return false;
        consumedKeys.Add(key);
        if (repeat) return true;
        if (key is Key.Escape or Key.Tab)
        {
            CompleteRecording();
            if (key == Key.Tab && XamlRoot is { } xamlRoot)
                FocusManager.TryMoveFocus(modifiers.HasFlag(ModifierKeys.Shift)
                    ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next,
                    new FindNextElementOptions { SearchRoot = xamlRoot.Content });
            return true;
        }
        if (MediaShortcutGestures.IsModifierKey(key)) return true;
        var gesture = key is Key.Delete or Key.Back ? null : new MediaShortcutGesture(key, modifiers);
        bool global = recordingGlobal;
        CompleteRecording();
        _ = SaveGesture(row, global, gesture);
        return true;
    }
    private async Task SaveGesture(MediaShortcutRow row, bool global, MediaShortcutGesture? gesture)
    {
        row.SetDraftErrors("", "");
        EditorNotice.Message = "";
        var draft = settings.Preferences with
        {
            Bindings = settings.Preferences.Bindings.Select(binding => binding.Action != row.Action ? binding :
                global ? binding with { Global = gesture } : binding with { Local = gesture }).ToArray()
        };
        if (MediaShortcutGestures.ValidationError(draft) is { } error)
        {
            row.SetDraftErrors(global ? "" : error, global ? error : "");
            EditorNotice.Message = error;
            Refresh();
            return;
        }
        await RunActionAsync(() => settings.SaveAsync(draft), "暂时无法保存快捷键设置");
    }
    private async Task RunActionAsync(Func<Task> action, string failureMessage)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ArgumentException)
        {
            if (subscribed) EditorNotice.Message = "快捷键设置无效";
        }
        catch
        {
            if (subscribed) EditorNotice.Message = failureMessage;
        }
        if (subscribed) Refresh();
    }
    private async void GlobalSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (refreshing || !subscribed) return;
        bool enabled = GlobalSwitch.IsChecked == true;
        EndRecording();
        await RunActionAsync(() => settings.SaveAsync(settings.Preferences with { GlobalEnabled = enabled }), "暂时无法保存快捷键设置");
    }
    private async void SystemMediaSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (refreshing || !subscribed) return;
        bool enabled = SystemMediaSwitch.IsChecked == true;
        EndRecording();
        await RunActionAsync(() => settings.SaveAsync(settings.Preferences with { SystemMediaEnabled = enabled }), "暂时无法保存快捷键设置");
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => settings.RequestRegistrationRefresh();
    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        EndRecording();
        await RunActionAsync(() => settings.LoadAsync(), "暂时无法加载媒体快捷键设置");
    }
}

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class MediaShortcutRow(MediaShortcutAction action) : INotifyPropertyChanged
{
    private bool dirty;
    public MediaShortcutAction Action { get; } = action;
    public string Label => MediaControlDefaults.Label(Action);
    public bool HasGlobal => Action != MediaShortcutAction.ToggleTranslation;
    public Visibility GlobalVisibility => HasGlobal ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnavailableVisibility => HasGlobal ? Visibility.Collapsed : Visibility.Visible;
    public string LocalName => Label + "，应用内快捷键";
    public string GlobalName => Label + "，全局快捷键";
    public string ClearLocalName => "清除" + LocalName;
    public string ClearGlobalName => "清除" + GlobalName;
    public string LocalText { get; private set; } = "";
    public string GlobalText { get; private set; } = "";
    public string LocalError { get; private set; } = "";
    public string DraftGlobalError { get; private set; } = "";
    public string RegistrationError { get; private set; } = "";
    public bool LocalEditorEnabled { get; private set; }
    public bool GlobalEditorEnabled { get; private set; }
    public bool CanClear { get; private set; }
    public string GlobalError => DraftGlobalError.Length > 0 ? DraftGlobalError : RegistrationError;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetDraftErrors(string localError, string draftGlobalError)
    {
        if (LocalError != localError) { LocalError = localError; dirty = true; }
        if (DraftGlobalError != draftGlobalError) { DraftGlobalError = draftGlobalError; dirty = true; }
    }

    internal void Apply(string localText, string globalText, string registrationError,
        bool localEditorEnabled, bool globalEditorEnabled, bool canClear)
    {
        if (LocalText != localText) { LocalText = localText; dirty = true; }
        if (GlobalText != globalText) { GlobalText = globalText; dirty = true; }
        if (RegistrationError != registrationError) { RegistrationError = registrationError; dirty = true; }
        if (LocalEditorEnabled != localEditorEnabled) { LocalEditorEnabled = localEditorEnabled; dirty = true; }
        if (GlobalEditorEnabled != globalEditorEnabled) { GlobalEditorEnabled = globalEditorEnabled; dirty = true; }
        if (CanClear != canClear) { CanClear = canClear; dirty = true; }
        if (!dirty) return;
        dirty = false;
        PropertyChanged?.Invoke(this, new(""));
    }
}
