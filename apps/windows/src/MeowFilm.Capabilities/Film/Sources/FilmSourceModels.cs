namespace MeowFilm.Capabilities;

public sealed record FilmSourceKey(string InstallationId, string SiteKey);

public sealed record FilmSourceSite(
    string Key, string Name, string Api,
    IReadOnlyList<string> Actions, IReadOnlyDictionary<string, string> Methods);

public sealed record FilmCatSource(
    string InstallationId, string Name, string SourceType, string? SourceUrl, bool Enabled,
    long? ImportedAt, long? UpdatedAt, string UpdateState, string? UpdateError,
    long Generation, string State, string? SourceHash, string? Error,
    IReadOnlyList<FilmSourceSite> Sites)
{
    public long? ModifiedAt => UpdatedAt ?? ImportedAt;
    public bool CanRestart => Enabled;
    public bool CanDetect => Enabled && State == "ready" && Generation > 0;
}

public sealed record FilmVideoOptions(
    bool Enabled = true, bool HomeVisible = true, bool SearchEnabled = true, bool AggregateImages = false);

public sealed record FilmSourceProbeStep(string Operation, string State, string? Error = null);

public sealed record FilmSourceDetection(
    string Result, long CheckedAt, bool PlaybackResolved, IReadOnlyList<FilmSourceProbeStep> Steps)
{
    public bool MediaPlaybackVerified => false;
    public string? Error => string.Join("  ", Steps.Where(step => step.Error is not null)
        .Select(step => $"{step.Operation}: {step.Error}")) is { Length: > 0 } text ? text : null;
}

public sealed record FilmVideoSource(
    FilmSourceKey Key, string Name, string Api,
    IReadOnlyList<string> Actions, IReadOnlyDictionary<string, string> Methods,
    FilmVideoOptions Options, int Order, FilmSourceDetection? Detection = null);

public sealed class FilmSourceException(string code, string? message = null) : Exception(message ?? code)
{
    public string Code { get; } = code;
}
