using System.Text.Json;
namespace MeowFilm.Platform.Runtime;

public sealed record RuntimeSettingsSnapshot(JsonElement? Document, long Revision);

/// <summary>Native-owned settings projections and the sole runtime credential write capability.</summary>
public interface IRuntimeSettingsStore
{
    Task<RuntimeSettingsSnapshot> ReadAsync(string block, CancellationToken token = default);
    Task<RuntimeSettingsSnapshot> UpdatePanAsync(JsonElement command, CancellationToken token = default);
}
