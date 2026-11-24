using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed class FilmDirectoryRow(string name, string path, bool isDirectory, string? candidateId,
    JsonElement? playbackTarget, bool isCurrent)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public bool IsDirectory { get; } = isDirectory;
    public string? CandidateId { get; } = candidateId;
    public JsonElement? PlaybackTarget { get; } = playbackTarget;
    public bool IsCurrent { get; } = isCurrent;
    public string Label => IsDirectory ? "目录 · " + Name : IsCurrent ? "当前 · " + Name : Name;

    internal FilmDirectoryRow WithCurrent(bool current) =>
        IsCurrent == current ? this : new(Name, Path, IsDirectory, CandidateId, PlaybackTarget, current);
}

/// <summary>Only groups already returned exact file paths. Browsing never requests or transfers a file.</summary>
internal sealed class FilmDirectory
{
    private sealed class Folder(string path, Folder? parent)
    {
        internal readonly string Path = path;
        internal readonly Folder? Parent = parent;
        internal readonly Dictionary<string, Folder> Children = new(StringComparer.Ordinal);
        internal readonly List<FilmDirectoryRow> Files = [];
    }

    private readonly Folder root = new("", null);
    private Folder current;
    private string currentCandidate;

    internal FilmDirectory(JsonElement browsing, string currentCandidate)
    {
        current = root;
        this.currentCandidate = currentCandidate;
        foreach (var file in FilmJson.Array(browsing, "originalEpisodes"))
        {
            string name = FilmJson.Text(file, "name"), path = FilmJson.Text(file, "path");
            string[] segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            var folder = root;
            for (int index = 0; index < segments.Length - 1; index++)
            {
                string segment = segments[index];
                if (!folder.Children.TryGetValue(segment, out var child))
                    folder.Children.Add(segment, child = new(folder.Path + "/" + segment, folder));
                folder = child;
            }
            var target = FilmJson.Member(file, "playbackTarget");
            var exact = FilmJson.FileTarget(target);
            string candidate = FilmJson.RequiredText(exact.Source!.Value, "candidateId");
            folder.Files.Add(new(name.Length > 0 ? name : segments.LastOrDefault() ?? "", path, false,
                candidate, target.Clone(), false));
        }
    }

    internal string Path => current.Path.Length == 0 ? "/" : current.Path;
    internal bool CanBack => current.Parent is not null;
    internal IReadOnlyList<FilmDirectoryRow> Rows => current.Children.Select(pair =>
        new FilmDirectoryRow(pair.Key, pair.Value.Path, true, null, null, false)).Concat(current.Files.Select(row =>
            row.WithCurrent(row.CandidateId == currentCandidate))).ToArray();

    internal bool UpdateCurrent(string candidateId)
    {
        if (currentCandidate == candidateId) return false;
        currentCandidate = candidateId;
        return true;
    }

    internal void Back() { if (current.Parent is { } parent) current = parent; }

    internal void Enter(FilmDirectoryRow row)
    {
        if (!row.IsDirectory || !current.Children.TryGetValue(row.Name, out var folder) || folder.Path != row.Path)
            throw new FilmPlaybackException("INVALID_REQUEST");
        current = folder;
    }
}
