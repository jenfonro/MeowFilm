using System.ComponentModel;
using System.Windows.Input;

namespace MeowFilm.Desktop;

public enum MediaShortcutAction
{
    TogglePlayback, Previous, Next, VolumeUp, VolumeDown, ToggleFavorite, ToggleDesktopLyrics, ToggleTranslation
}

public sealed record MediaShortcutGesture(Key Key, ModifierKeys Modifiers);
public sealed record MediaShortcutBinding(MediaShortcutAction Action, MediaShortcutGesture? Local, MediaShortcutGesture? Global);
public sealed record MediaControlPreferences(bool GlobalEnabled, bool SystemMediaEnabled, IReadOnlyList<MediaShortcutBinding> Bindings);

public interface IDesktopMediaControlSettings : INotifyPropertyChanged
{
    MediaControlPreferences Preferences { get; }
    bool IsAvailable { get; }
    bool CanEdit { get; }
    string Status { get; }
    bool IsRecording { get; }
    IReadOnlyDictionary<MediaShortcutAction, string> GlobalErrors { get; }
    Task LoadAsync();
    Task SaveAsync(MediaControlPreferences preferences);
    void SetRecording(bool recording);
    void SetGlobalErrors(IReadOnlyDictionary<MediaShortcutAction, string> errors);
    void RequestRegistrationRefresh();
    event EventHandler? RegistrationRefreshRequested;
}

public static class MediaControlDefaults
{
    public static MediaControlPreferences Preferences { get; } = new(true, true,
        Array.AsReadOnly(new[]
        {
            Binding(MediaShortcutAction.TogglePlayback, Key.P),
            Binding(MediaShortcutAction.Previous, Key.Left),
            Binding(MediaShortcutAction.Next, Key.Right),
            Binding(MediaShortcutAction.VolumeUp, Key.Up),
            Binding(MediaShortcutAction.VolumeDown, Key.Down),
            Binding(MediaShortcutAction.ToggleFavorite, Key.L),
            Binding(MediaShortcutAction.ToggleDesktopLyrics, Key.D),
            new MediaShortcutBinding(MediaShortcutAction.ToggleTranslation, new(Key.T, ModifierKeys.Control), null)
        }));

    private static MediaShortcutBinding Binding(MediaShortcutAction action, Key key) =>
        new(action, new(key, ModifierKeys.Control), new(key, ModifierKeys.Control | ModifierKeys.Alt));

    public static string Label(MediaShortcutAction action) => action switch
    {
        MediaShortcutAction.TogglePlayback => "播放/暂停",
        MediaShortcutAction.Previous => "上一首",
        MediaShortcutAction.Next => "下一首",
        MediaShortcutAction.VolumeUp => "音量加",
        MediaShortcutAction.VolumeDown => "音量减",
        MediaShortcutAction.ToggleFavorite => "喜欢歌曲",
        MediaShortcutAction.ToggleDesktopLyrics => "打开/关闭歌词",
        MediaShortcutAction.ToggleTranslation => "翻译当前歌词",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}

public static class MediaShortcutGestures
{
    public static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    public static bool IsValid(MediaShortcutGesture gesture) =>
        Enum.IsDefined(gesture.Key) &&
        gesture.Key is not (Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed) &&
        !IsModifierKey(gesture.Key) &&
        (gesture.Modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows)) == 0 &&
        KeyInterop.VirtualKeyFromKey(gesture.Key) != 0;

    public static string Display(MediaShortcutGesture? gesture)
    {
        if (gesture is null) return "未设置";
        var parts = new List<string>(5);
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        int key = (int)gesture.Key;
        parts.Add(key >= (int)Key.D0 && key <= (int)Key.D9 ? (key - (int)Key.D0).ToString() : gesture.Key switch
        {
            Key.Return => "Enter",
            Key.Back => "Backspace",
            _ => gesture.Key.ToString()
        });
        return string.Join("+", parts);
    }

    public static string? ValidationError(MediaControlPreferences preferences)
    {
        var actions = Enum.GetValues<MediaShortcutAction>();
        if (preferences.Bindings is null || preferences.Bindings.Count != actions.Length ||
            preferences.Bindings.Any(binding => binding is null || !Enum.IsDefined(binding.Action)) ||
            preferences.Bindings.Select(binding => binding.Action).Distinct().Count() != actions.Length)
            return "快捷键功能列表无效";
        var assigned = new Dictionary<MediaShortcutGesture, MediaShortcutAction>();
        foreach (var binding in preferences.Bindings)
        {
            if (binding.Action == MediaShortcutAction.ToggleTranslation && binding.Global is not null)
                return "翻译当前歌词不支持全局快捷键";
            foreach (var gesture in new[] { binding.Local, binding.Global })
            {
                if (gesture is null) continue;
                if (!IsValid(gesture)) return $"{MediaControlDefaults.Label(binding.Action)}：请选择有效按键";
                if ((gesture.Key == Key.F && gesture.Modifiers == ModifierKeys.Control) ||
                    (gesture.Key == Key.Left && gesture.Modifiers == ModifierKeys.Alt) ||
                    (gesture.Key is Key.Escape or Key.Space && gesture.Modifiers == ModifierKeys.None))
                    return $"{Display(gesture)} 已用于窗口导航或完整播放器";
                if (assigned.TryGetValue(gesture, out var action) && action != binding.Action)
                    return $"{Display(gesture)} 在{MediaControlDefaults.Label(action)}与{MediaControlDefaults.Label(binding.Action)}之间重复";
                assigned[gesture] = binding.Action;
            }
        }
        return null;
    }
}
