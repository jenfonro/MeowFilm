using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed class FilmDirectoryRow(string name, string path, bool isDirectory, string? itemId, bool isCurrent)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public bool IsDirectory { get; } = isDirectory;
    public string? ItemId { get; } = itemId;
    public bool IsCurrent { get; } = isCurrent;
    public string Label => IsDirectory ? "目录 · " + Name : IsCurrent ? "当前 · " + Name : Name;

    internal FilmDirectoryRow WithCurrent(bool current) =>
        IsCurrent == current ? this : new(Name, Path, IsDirectory, ItemId, current);
}

/// <summary>Groups returned file paths into a folder tree; clicking forwards semantic itemId to Node.</summary>
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
    private string currentItemId;

    internal FilmDirectory(JsonElement rawItems, string currentItemId)
    {
        current = root;
        this.currentItemId = currentItemId;
        foreach (var file in FilmJson.Array(rawItems))
        {
            string itemId = FilmJson.RequiredText(file, "id");
            string name = FilmJson.Text(file, "fileName");
            if (name.Length == 0) name = FilmJson.Text(file, "title");
            string path = FilmJson.Text(file, "path");
            string[] segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            var folder = root;
            for (int index = 0; index < segments.Length - 1; index++)
            {
                string segment = segments[index];
                if (!folder.Children.TryGetValue(segment, out var child))
                    folder.Children.Add(segment, child = new(folder.Path + "/" + segment, folder));
                folder = child;
            }
            string displayName = name.Length > 0 ? name : segments.LastOrDefault() ?? itemId;
            folder.Files.Add(new(displayName, path, false, itemId, FilmJson.Bool(file, "active")));
        }
    }

    internal string Path => current.Path.Length == 0 ? "/" : current.Path;
    internal bool CanBack => current.Parent is not null;
    internal IReadOnlyList<FilmDirectoryRow> Rows => current.Children.Select(pair =>
        new FilmDirectoryRow(pair.Key, pair.Value.Path, true, null, false)).Concat(current.Files.Select(row =>
            row.WithCurrent(row.IsCurrent || (currentItemId.Length > 0 && row.ItemId == currentItemId)))).ToArray();

    internal bool UpdateCurrent(string itemId)
    {
        if (currentItemId == itemId) return false;
        currentItemId = itemId;
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
