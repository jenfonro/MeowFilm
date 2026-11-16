namespace MeowFilm.Media;

/// <summary>Native publication controls. PreserveFrom identifies the exact same-episode donor, not the new attempt.</summary>
public sealed record RuntimeMediaPlaybackIntent(string OwnerId, string Identity,
    double Position, bool Paused, NativePlaybackScope? PreserveFrom = null);
