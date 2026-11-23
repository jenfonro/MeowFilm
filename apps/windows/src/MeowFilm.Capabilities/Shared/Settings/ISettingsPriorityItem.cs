using System.ComponentModel;

namespace MeowFilm.Capabilities;

public interface ISettingsPriorityItem : INotifyPropertyChanged
{
    string Label { get; }
    string? LogoUri => null;
    bool Enabled { get; }
}
