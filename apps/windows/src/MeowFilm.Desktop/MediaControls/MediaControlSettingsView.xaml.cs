using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace MeowFilm.Desktop;

public partial class MediaControlSettingsView : UserControl
{
    private readonly IDesktopMediaControlSettings settings;
    private readonly Row[] rows = Enum.GetValues<MediaShortcutAction>().Select(action => new Row(action)).ToArray();
    private readonly HashSet<Key> consumedKeys = [];
    private Window? window;
    private Button? recordingButton;
    private Row? recordingRow;
    private bool recordingGlobal, subscribed;

    public MediaControlSettingsView(IDesktopMediaControlSettings settings)
    {
        this.settings = settings;
        InitializeComponent();
        DataContext = settings;
        ShortcutRows.ItemsSource = rows;
        Refresh();
    }

    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (subscribed) return;
        subscribed = true;
        settings.PropertyChanged += Settings_Changed;
        InputManager.Current.PreProcessInput += BeforeInput;
        window = Window.GetWindow(this);
        if (window is not null) window.Deactivated += Window_Deactivated;
        if (!settings.IsAvailable) await settings.LoadAsync();
        Refresh();
    }
    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        EndRecording();
        if (!subscribed) return;
        settings.PropertyChanged -= Settings_Changed;
        InputManager.Current.PreProcessInput -= BeforeInput;
        if (window is not null) window.Deactivated -= Window_Deactivated;
        window = null;
        consumedKeys.Clear();
        subscribed = false;
    }
    private void Settings_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(() => Settings_Changed(sender, e))); return; }
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
            row.Changed();
        }
        RefreshButton.IsEnabled = settings.CanEdit && settings.Preferences.GlobalEnabled && !settings.IsRecording;
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
        button.Focus();
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
    private void View_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (!IsVisible) EndRecording(); }
    private void View_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    { if (recordingButton is not null && !ReferenceEquals(e.NewFocus, recordingButton)) EndRecording(); }

    private void BeforeInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is not KeyEventArgs key || !OwnsInput(key)) return;
        if (key.RoutedEvent is not { } routed ||
            (routed != Keyboard.PreviewKeyDownEvent && routed != Keyboard.KeyDownEvent &&
             routed != Keyboard.PreviewKeyUpEvent && routed != Keyboard.KeyUpEvent)) return;
        bool down = routed == Keyboard.PreviewKeyDownEvent || routed == Keyboard.KeyDownEvent;
        if (HandleKey(key, down)) e.Cancel();
    }
    private bool OwnsInput(KeyEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject ?? e.Source as DependencyObject;
        if (source is not null && (IsAncestorOf(source) || ReferenceEquals(source, this))) return true;
        return PresentationSource.FromVisual(this) is { } presentation && presentation == e.InputSource &&
            (source is null || consumedKeys.Contains(ActualKey(e)));
    }
    private void View_KeyDown(object sender, KeyEventArgs e) => HandleKey(e, true);
    private void View_KeyUp(object sender, KeyEventArgs e) => HandleKey(e, false);
    private bool HandleKey(KeyEventArgs e, bool down)
    {
        var key = ActualKey(e);
        if (!down)
        {
            if (!consumedKeys.Remove(key) && recordingRow is null) return false;
            e.Handled = true;
            if (recordingRow is null && consumedKeys.Count == 0) EndRecording();
            return true;
        }
        if (consumedKeys.Contains(key)) { e.Handled = true; return true; }
        if (recordingRow is not { } row) return false;
        consumedKeys.Add(key);
        e.Handled = true;
        if (e.IsRepeat) return true;
        if (key is Key.Escape or Key.Tab)
        {
            var button = recordingButton;
            CompleteRecording();
            if (key == Key.Tab) button?.MoveFocus(new(e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift)
                ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
            return true;
        }
        if (MediaShortcutGestures.IsModifierKey(key)) return true;
        var gesture = key is Key.Delete or Key.Back ? null : new MediaShortcutGesture(key, MediaShortcutInput.Modifiers(e.KeyboardDevice));
        bool global = recordingGlobal;
        CompleteRecording();
        _ = SaveGesture(row, global, gesture);
        return true;
    }
    private static Key ActualKey(KeyEventArgs e) =>
        e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
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
    private async void GlobalSwitch_Click(object sender, RoutedEventArgs e)
    {
        EndRecording();
        await settings.SaveAsync(settings.Preferences with { GlobalEnabled = GlobalSwitch.IsChecked == true });
        GlobalSwitch.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }
    private async void SystemMediaSwitch_Click(object sender, RoutedEventArgs e)
    {
        EndRecording();
        await settings.SaveAsync(settings.Preferences with { SystemMediaEnabled = SystemMediaSwitch.IsChecked == true });
        SystemMediaSwitch.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => settings.RequestRegistrationRefresh();
    private async void Reload_Click(object sender, RoutedEventArgs e)
    { EndRecording(); await settings.LoadAsync(); }

    private sealed class Row(MediaShortcutAction action) : INotifyPropertyChanged
    {
        public MediaShortcutAction Action { get; } = action;
        public string Label => MediaControlDefaults.Label(Action);
        public bool HasGlobal => Action != MediaShortcutAction.ToggleTranslation;
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
        public string GlobalError => DraftGlobalError.Length > 0 ? DraftGlobalError : RegistrationError;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Changed() => PropertyChanged?.Invoke(this, new(""));
    }
}
