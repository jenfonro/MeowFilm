namespace MeowFilm.Media;

/// <summary>
/// Observed libVLC video-output display counter, not Playing, decoded frames,
/// physical display scan-out, hardware decoding, HDR, or an output capability claim.
/// </summary>
public sealed class VideoOutputObservation(string ownerId, string identity, long generation,
    int displayedPictures) : EventArgs
{
    public string OwnerId { get; } = ownerId;
    public string Identity { get; } = identity;
    public long Generation { get; } = generation;
    public int DisplayedPictures { get; } = displayedPictures;
    public string Evidence => "libvlc-displayed-pictures";
}
