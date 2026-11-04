using System.ComponentModel;

namespace MeowFilm.Capabilities;

public sealed class MusicOfficialSourceRow(string platform, string name, bool enabled) : INotifyPropertyChanged
{
    private bool enabled = enabled;

    public string Platform { get; } = platform;
    public string Name { get; } = name;
    public bool Enabled => enabled;
    public string StateText => enabled ? "已启用" : "已停用";
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetEnabled(bool value)
    {
        if (enabled == value) return;
        enabled = value;
        PropertyChanged?.Invoke(this, new(nameof(Enabled)));
        PropertyChanged?.Invoke(this, new(nameof(StateText)));
    }
}
