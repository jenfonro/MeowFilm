// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Platforms;

public sealed record MusicDailyStyleTag(string Id, string Name);
public sealed record MusicDailyStyleCategory(string Id, string Name, IReadOnlyList<MusicDailyStyleTag> Tags);
/// <summary>Identity captured when editing opens. Contains no credentials or transferable authorization.</summary>
public sealed record MusicDailyStyleEditor(string AccountScope, long AccountGeneration, string UserId,
    IReadOnlyList<MusicDailyStyleCategory> Categories);
public sealed record MusicDailyStyleSelection(string CategoryId, IReadOnlyList<string> TagIds);
public enum MusicDailyStyleWriteStatus { NotSent, Rejected, Confirmed, Unknown }
/// <summary>Confirmed describes the write only. Only Readback establishes the current songs and selection.</summary>
public sealed record MusicDailyStyleSaveResult(MusicDailyStyleWriteStatus Status, JsonElement? Readback,
    bool? SelectionMatches, string? ErrorCode);

public interface IMusicDailyStylePreferences
{
    Task<MusicDailyStyleEditor> OpenDailyStyleEditorAsync(CancellationToken cancellationToken = default);
    /// <summary>Cancellation before admission prevents writing. Once admitted, await physical settlement and readback;
    /// cancellation cannot undo an upstream write. Unknown must be reconciled by reading, never automatically retried.</summary>
    Task<MusicDailyStyleSaveResult> SaveDailyStyleAsync(MusicDailyStyleEditor editor,
        MusicDailyStyleSelection selection, CancellationToken cancellationToken = default);
}

public sealed partial class MusicPlatformCatalog
{
    public async Task<MusicDailyStyleEditor> OpenDailyStyleEditorAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync("wy", "daily-style-config", new { }, true, async c =>
        {
            c.Auth();
            var categories = await ReadDailyStyleCategories(c);
            var session = c.Session!;
            var editor = new MusicDailyStyleEditor(session.Scope, session.Generation, session.UserId, categories);
            return JsonSerializer.SerializeToNode(editor, PJ.Options)!.AsObject();
        }, cancellationToken, cacheable: false).ConfigureAwait(false);
        return result.Deserialize<MusicDailyStyleEditor>(PJ.Options)!;
    }

    private static Task<IReadOnlyList<MusicDailyStyleCategory>> ReadDailyStyleCategories(Call c) =>
        c.Xeapi<IReadOnlyList<MusicDailyStyleCategory>>("/api/homepage/daily/song/config/get", new(), root =>
        {
            if (PJ.At(root, "data", "categorys") is not JsonArray rows) throw new MusicPlatformException("INVALID_RESPONSE");
            var categories = rows.Select(row =>
            {
                if (row is not JsonObject category) throw new MusicPlatformException("INVALID_RESPONSE");
                var tags = DailyStyleTags(category["tagVOList"]);
                var name = PJ.Metadata(category["categoryName"]);
                PJ.Require(name.Length > 0, "INVALID_RESPONSE");
                return new MusicDailyStyleCategory(PJ.Id(category["categoryId"]), name, tags);
            }).ToArray();
            PJ.Require(categories.Length > 0 && categories.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() == categories.Length,
                "INVALID_RESPONSE");
            return Array.AsReadOnly(categories);
        });

    private static IReadOnlyList<MusicDailyStyleTag> DailyStyleTags(JsonNode? value)
    {
        if (value is not JsonArray rows) throw new MusicPlatformException("INVALID_RESPONSE");
        var tags = rows.Select(row =>
        {
            if (row is not JsonObject tag) throw new MusicPlatformException("INVALID_RESPONSE");
            var name = PJ.Metadata(tag["tagName"]);
            PJ.Require(name.Length > 0, "INVALID_RESPONSE");
            return new MusicDailyStyleTag(PJ.Id(tag["tagId"]), name);
        }).ToArray();
        PJ.Require(tags.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() == tags.Length, "INVALID_RESPONSE");
        return Array.AsReadOnly(tags);
    }

    public async Task<MusicDailyStyleSaveResult> SaveDailyStyleAsync(MusicDailyStyleEditor editor,
        MusicDailyStyleSelection selection, CancellationToken cancellationToken = default)
    {
        var status = MusicDailyStyleWriteStatus.NotSent;
        string? errorCode = null;
        try
        {
            if (editor is null || selection is null || selection.TagIds is null)
                throw new MusicPlatformException("INVALID_REQUEST");
            PJ.Require(!cancellationToken.IsCancellationRequested, "CANCELLED");
            // Copy the caller's draft before admission; UI mutation cannot alter an in-flight write.
            var ids = selection.TagIds.ToArray();
            PJ.Require(ids.Length > 0 && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length);
            var categoryId = selection.CategoryId;
            PJ.IdInput(categoryId);
            foreach (var id in ids) PJ.IdInput(id);
            static bool Contains(IReadOnlyList<MusicDailyStyleCategory> categories, string category, string[] tags) =>
                categories.Any(c => c.Id == category && tags.All(id => c.Tags.Any(t => t.Id == id)));
            PJ.Require(editor.Categories is not null && Contains(editor.Categories, categoryId, ids));
            // XEAPI's pinned recipe carries numeric IDs (JavaScript safe integers), not quoted string IDs.
            var numericCategory = DailyStyleNumber(categoryId);
            var numericIds = ids.Select(DailyStyleNumber).ToArray();
            var readback = await RunAsync("wy", "daily-style-save", new { Confirmation = Guid.NewGuid() }, true, async c =>
            {
                c.Auth();
                var session = c.Session!;
                PJ.Require(session.Scope == editor.AccountScope && session.Generation == editor.AccountGeneration &&
                    session.UserId == editor.UserId, "STALE_SESSION");
                RetireDailyStyleReads(c.Scope);
                try
                {
                    // Revalidate current service membership without inventing a count limit.
                    var categories = await ReadDailyStyleCategories(c);
                    PJ.Require(Contains(categories, categoryId, ids), "INVALID_REQUEST");
                    try
                    {
                        await c.Xeapi("/api/homepage/daily/song/tag/save",
                            PJ.Obj(("tags", PJ.Encode(PJ.Obj(("categoryId", numericCategory), ("tagIds", numericIds))))),
                            _ => { status = MusicDailyStyleWriteStatus.Confirmed; return true; },
                            dispatched: () => status = MusicDailyStyleWriteStatus.Unknown,
                            rejected: () => status = MusicDailyStyleWriteStatus.Rejected);
                    }
                    catch (MusicPlatformException error) { errorCode = error.Code; }
                }
                finally { RetireDailyStyleReads(c.Scope); }
                c.Guard();
                // The new epoch can share reads and retain the authoritative one-minute cache.
                var refreshed = await RunAsync("wy", "daily-style", new MusicDailyRequest(Branch: MusicDailyBranch.Style, Refresh: true),
                    true, next =>
                    {
                        PJ.Require(Same(session, next.Session), "STALE_SESSION");
                        return next.Xeapi("/api/homepage/category/daily/song/list", new(), data => DailyBatch(next, data, true));
                    }, CancellationToken.None, privateCache: true).ConfigureAwait(false);
                return JsonNode.Parse(refreshed.GetRawText())!.AsObject();
            }, CancellationToken.None, cacheable: false).ConfigureAwait(false);
            var matches = readback.GetProperty("category").GetString() == categoryId &&
                ids.ToHashSet(StringComparer.Ordinal).SetEquals(readback.GetProperty("tags").EnumerateArray()
                    .Select(tag => tag.GetProperty("id").GetString()!));
            return new(status, readback, matches, errorCode);
        }
        catch (MusicPlatformException error) { return new(status, null, null, error.Code); }
    }

    private static long DailyStyleNumber(string id)
    {
        PJ.Require(long.TryParse(id, out var number) && number is > 0 and <= 9_007_199_254_740_991);
        return number;
    }

    private void RetireDailyStyleReads(string scope)
    {
        Call[] retired;
        lock (gate)
        {
            dailyStyleEpochs[scope] = dailyStyleEpochs.GetValueOrDefault(scope) + 1;
            foreach (var key in cache.Where(entry => entry.Value.Platform == "wy" &&
                entry.Value.Operation == "daily-style" && entry.Value.Scope == scope).Select(entry => entry.Key).ToArray()) Erase(key);
            foreach (var key in flights.Where(entry => entry.Value.Call.Platform == "wy" &&
                entry.Value.Call.Operation == "daily-style" && entry.Value.Call.Scope == scope).Select(entry => entry.Key).ToArray()) flights.Remove(key);
            retired = active.Where(c => c.Platform == "wy" && c.Operation == "daily-style" && c.Scope == scope).ToArray();
            foreach (var call in retired) call.Fence("SNAPSHOT_CHANGED");
        }
        foreach (var call in retired) call.Cancel();
    }
}
