namespace MeowFilm.Desktop;

public sealed record DesktopSearchTarget(string Id, string Label);

/// <summary>App-composed search entry. The shell routes opaque target identities only.</summary>
public interface IDesktopSearchPage : IDesktopPage
{
    IReadOnlyList<DesktopSearchTarget> Targets { get; }
    string SelectedTarget { get; }
    string Query { get; }
    IDesktopModule ContentOwner { get; }
    // Activates with the new input directly; do not first restore an old query.
    Task SubmitAsync(string query, string target, CancellationToken token);
}
