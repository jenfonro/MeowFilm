namespace MeowFilm.Core.Runtime;

/// <summary>Best-effort invalidation of an already connected runtime. Never starts or recovers it.</summary>
public interface IRuntimeSettingsNotifications
{
    void SettingsChanged(string block);
}
