// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace MeowFilm.Capabilities.Platforms;

public sealed record MusicDailyStyleTag(string Id, string Name);
public sealed record MusicDailyStyleCategory(string Id, string Name, IReadOnlyList<MusicDailyStyleTag> Tags);
public sealed record MusicDailyStyleEditor(string AccountScope, long AccountGeneration, string UserId,
    IReadOnlyList<MusicDailyStyleCategory> Categories);
public sealed record MusicDailyStyleSelection(string CategoryId, IReadOnlyList<string> TagIds);
public enum MusicDailyStyleWriteStatus { NotSent, Rejected, Confirmed, Unknown }
public sealed record MusicDailyStyleSaveResult(MusicDailyStyleWriteStatus Status, JsonElement? Readback,
    bool? SelectionMatches, string? ErrorCode);

public interface IMusicDailyStylePreferences
{
    Task<MusicDailyStyleEditor> OpenDailyStyleEditorAsync(CancellationToken cancellationToken = default);
    Task<MusicDailyStyleSaveResult> SaveDailyStyleAsync(MusicDailyStyleEditor editor,
        MusicDailyStyleSelection selection, CancellationToken cancellationToken = default);
}

public sealed partial class MusicPlatformCatalog
{
    private static readonly JsonSerializerOptions ModelJson = new() { PropertyNameCaseInsensitive = true };
    public async Task<MusicDailyStyleEditor> OpenDailyStyleEditorAsync(CancellationToken cancellationToken = default)
    {
        var account = await sessions.AcquireAsync("wy", cancellationToken).ConfigureAwait(false)
            ?? throw new MusicPlatformException("AUTH_REQUIRED");
        var data = await client.RequestAsync("wy", "dailyStyle.get", new { }, account, cancellationToken).ConfigureAwait(false);
        return new(account.Scope, account.Generation, account.UserId,
            data.GetProperty("categories").Deserialize<MusicDailyStyleCategory[]>(ModelJson)!);
    }
    public async Task<MusicDailyStyleSaveResult> SaveDailyStyleAsync(MusicDailyStyleEditor editor,
        MusicDailyStyleSelection selection, CancellationToken cancellationToken = default)
    {
        JsonElement result;
        try
        {
            var account = await sessions.AcquireAsync("wy", cancellationToken).ConfigureAwait(false)
                ?? throw new MusicPlatformException("AUTH_REQUIRED");
            if (account.Scope != editor.AccountScope || account.Generation != editor.AccountGeneration || account.UserId != editor.UserId)
                throw new MusicPlatformException("STALE_SESSION");
            result = await client.RequestAsync("wy", "dailyStyle.save", new
            { categoryId = selection.CategoryId, tagIds = selection.TagIds.ToArray() }, account, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(MusicDailyStyleWriteStatus.NotSent, null, null, "CANCELLED"); }
        catch (MusicPlatformException error) { return new(MusicDailyStyleWriteStatus.NotSent, null, null, error.Code); }
        catch (MeowFilm.Capabilities.Accounts.MusicAccountException error)
        { return new(MusicDailyStyleWriteStatus.NotSent, null, null, error.Code); }
        try
        {
            var status = result.GetProperty("status").GetString() switch
            {
                "confirmed" => MusicDailyStyleWriteStatus.Confirmed, "rejected" => MusicDailyStyleWriteStatus.Rejected,
                "unknown" => MusicDailyStyleWriteStatus.Unknown, "notSent" => MusicDailyStyleWriteStatus.NotSent,
                _ => throw new MusicPlatformException("INVALID_RESPONSE")
            };
            return new(status, result.TryGetProperty("readback", out var readback) && readback.ValueKind != JsonValueKind.Null ? readback.Clone() : null,
                result.TryGetProperty("selectionMatches", out var matches) && matches.ValueKind != JsonValueKind.Null ? matches.GetBoolean() : null,
                result.TryGetProperty("errorCode", out var error) ? error.GetString() : null);
        }
        catch { return new(MusicDailyStyleWriteStatus.Unknown, null, null, "INVALID_RESPONSE"); }
    }
}
