using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Key = System.Windows.Input.Key;
using ModifierKeys = System.Windows.Input.ModifierKeys;

namespace MeowFilm.Desktop;

public partial class MediaControlSettingsView : UserControl
{
    private readonly IDesktopMediaControlSettings settings;
    private readonly Row[] rows = Enum.GetValues<MediaShortcutAction>().Select(action => new Row(action)).ToArray();
    private readonly HashSet<Key> consumedKeys = [];
    private readonly KeyEventHandler keyDownHandler, keyUpHandler;
    private DesktopWindow? window;
    private FrameworkElement? inputRoot;
    private Button? recordingButton;
    private Row? recordingRow;
    private bool recordingGlobal, subscribed, refreshing;

    public MediaControlSettingsView(IDesktopMediaControlSettings settings)
    {
        this.settings = settings;
        keyDownHandler = View_KeyDown;
        keyUpHandler = View_KeyUp;
        InitializeComponent();
        DataContext = settings;
        ShortcutRows.ItemsSource = rows;
        LayoutUpdated += View_LayoutUpdated;
        Refresh();
    }

    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (subscribed) return;
        subscribed = true;
        settings.PropertyChanged += Settings_Changed;
        inputRoot = XamlRoot.Content as FrameworkElement;
        if (inputRoot is not null)
        {
            inputRoot.AddHandler(UIElement.PreviewKeyDownEvent, keyDownHandler, true);
            inputRoot.AddHandler(UIElement.PreviewKeyUpEvent, keyUpHandler, true);
        }
        window = DesktopWindow.GetWindow(this);
        if (window is not null) window.Deactivated += Window_Deactivated;
        if (!settings.IsAvailable) await settings.LoadAsync();
        Refresh();
    }
    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        EndRecording();
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
            foreach (var row in rows) row.LocalError = row.DraftGlobalError = "";
        Refresh();
    }
    private void Refresh()
    {
        foreach (var row in rows)
        {
            var binding = settings.Preferences.Bindings.Single(item => item.Action == row.Action);
            row.LocalText = recordingRow == row && !recordingGlobal ? "录制中" : MediaShortcutGestures.Display(binding.Local);
            row.GlobalText = recordingRow == row && recordingGlobal ? "录制中" : MediaShortcutGestures.Display(binding.Global);
            row.RegistrationError = settings.GlobalErrors.GetValueOrDefault(row.Action) ?? "";
            // Disabling the input owner during its save would move keyboard
            // focus and reopen native registration before the held key is up.
            bool ownsInput = ReferenceEquals(recordingButton?.DataContext, row);
            row.LocalEditorEnabled = settings.CanEdit || (ownsInput && !recordingGlobal);
            row.GlobalEditorEnabled = settings.CanEdit || (ownsInput && recordingGlobal);
            row.CanClear = settings.CanEdit;
            row.Changed();
        }
        RefreshButton.IsEnabled = settings.CanEdit && settings.Preferences.GlobalEnabled && !settings.IsRecording;
        ReloadButton.Visibility = string.IsNullOrWhiteSpace(settings.Status) ? Visibility.Collapsed : Visibility.Visible;
        refreshing = true;
        try
        {
            GlobalSwitch.IsOn = settings.Preferences.GlobalEnabled;
            SystemMediaSwitch.IsOn = settings.Preferences.SystemMediaEnabled;
        }
        finally { refreshing = false; }
    }
    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (!settings.CanEdit || sender is not Button { DataContext: Row row, Tag: string column } button) return;
        EndRecording();
        // A previous session may have lost its KeyUp outside this window.
        consumedKeys.Clear();
        row.LocalError = row.DraftGlobalError = "";
        EditorNotice.Message = "";
        recordingRow = row; recordingGlobal = column == "global"; recordingButton = button;
        button.Focus(FocusState.Programmatic);
        settings.SetRecording(true);
        Refresh();
    }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!settings.CanEdit || sender is not Button { DataContext: Row row, Tag: string column }) return;
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
    private void View_LayoutUpdated(object? sender, object args)
    {
        if (recordingButton is not null && !DesktopVisual.IsVisible(this)) EndRecording();
    }
    private void View_LostFocus(object sender, RoutedEventArgs e)
    {
        if (recordingButton is not null && !ReferenceEquals(DesktopVisual.Focused(this), recordingButton)) EndRecording();
    }
    private void View_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ProcessKey(e.OriginalSource, MediaShortcutInput.Key(e), MediaShortcutInput.Modifiers(), true, e.KeyStatus.WasKeyDown)) e.Handled = true;
    }
    private void View_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (ProcessKey(e.OriginalSource, MediaShortcutInput.Key(e), MediaShortcutInput.Modifiers(), false, false)) e.Handled = true;
    }
    internal bool ProcessKey(object? target, Key key, ModifierKeys modifiers, bool down, bool repeat)
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
    private async Task SaveGesture(Row row, bool global, MediaShortcutGesture? gesture)
    {
        row.LocalError = row.DraftGlobalError = "";
        EditorNotice.Message = "";
        var draft = settings.Preferences with
        {
            Bindings = settings.Preferences.Bindings.Select(binding => binding.Action != row.Action ? binding :
                global ? binding with { Global = gesture } : binding with { Local = gesture }).ToArray()
        };
        if (MediaShortcutGestures.ValidationError(draft) is { } error)
        {
            if (global) row.DraftGlobalError = error; else row.LocalError = error;
            EditorNotice.Message = error;
        }
        else
        {
            try { await settings.SaveAsync(draft); }
            catch (ArgumentException) { EditorNotice.Message = "快捷键设置无效"; }
        }
        Refresh();
    }
    private async void GlobalSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (refreshing || !subscribed) return;
        bool enabled = GlobalSwitch.IsOn;
        EndRecording();
        await settings.SaveAsync(settings.Preferences with { GlobalEnabled = enabled });
        Refresh();
    }
    private async void SystemMediaSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (refreshing || !subscribed) return;
        bool enabled = SystemMediaSwitch.IsOn;
        EndRecording();
        await settings.SaveAsync(settings.Preferences with { SystemMediaEnabled = enabled });
        Refresh();
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => settings.RequestRegistrationRefresh();
    private async void Reload_Click(object sender, RoutedEventArgs e)
    { EndRecording(); await settings.LoadAsync(); }

    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed class Row(MediaShortcutAction action) : INotifyPropertyChanged
    {
        public MediaShortcutAction Action { get; } = action;
        public string Label => MediaControlDefaults.Label(Action);
        public bool HasGlobal => Action != MediaShortcutAction.ToggleTranslation;
        public Visibility GlobalVisibility => HasGlobal ? Visibility.Visible : Visibility.Collapsed;
        public Visibility UnavailableVisibility => HasGlobal ? Visibility.Collapsed : Visibility.Visible;
        public string LocalName => Label + "，应用内快捷键";
        public string GlobalName => Label + "，全局快捷键";
        public string ClearLocalName => "清除" + LocalName;
        public string ClearGlobalName => "清除" + GlobalName;
        public string LocalText { get; set; } = "";
        public string GlobalText { get; set; } = "";
        public string LocalError { get; set; } = "";
        public string DraftGlobalError { get; set; } = "";
        public string RegistrationError { get; set; } = "";
        public bool LocalEditorEnabled { get; set; }
        public bool GlobalEditorEnabled { get; set; }
        public bool CanClear { get; set; }
        public string GlobalError => DraftGlobalError.Length > 0 ? DraftGlobalError : RegistrationError;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Changed() => PropertyChanged?.Invoke(this, new(""));
    }
}
