using System.ComponentModel;

namespace MeowFilm.Capabilities;

public interface ISettingsPriorityItem : INotifyPropertyChanged
{
    string Label { get; }
    string Platform { get; }
    bool Enabled { get; }
}
