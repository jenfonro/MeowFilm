using MeowFilm.Capabilities.Platforms;
using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private MusicDailyStyleEditor? dailyStyleEditor;
    private string dailyStyleAccount = "", dailyStyleCategory = "", dailyStyleSelectedCategory = "";
    private MusicDailyStyleTag[] dailyStyleSelected = [];
    private readonly HashSet<string> dailyStyleDraft = new(StringComparer.Ordinal);
    private long dailyStyleRevision;
    private bool dailyStyleBusy, dailyStyleNeedsRead;
    public string DailyOtherBranch => IsDailyStyle ? "default" : "style";
    public string DailyOtherBranchTitle => IsDailyStyle ? "每日推荐" : "风格推荐";
    public bool IsDailyStyle => IsCatalogDaily && CatalogDailyBranch == "style";
    public bool IsDailyStyleEditorOpen => dailyStyleEditor is not null;
    public bool CanEditDailyStyle => IsDailyStyle && !dailyStyleBusy && loadCancellation is null && CanCatalogBrowse;
    public bool CanConfirmDailyStyle => CanEditDailyStyle && !dailyStyleNeedsRead && dailyStyleDraft.Count > 0 && IsDailyStyleEditorOpen;
    public string DailyStyleSelectionText => dailyStyleSelected.Length == 0 ? "选择风格" : string.Join(" · ", dailyStyleSelected.Select(t => t.Name));
    public IReadOnlyList<MusicCatalogTagGroup> DailyStyleGroups => dailyStyleEditor?.Categories.Select(category =>
        new MusicCatalogTagGroup(category.Name, category.Tags.Select(tag => new MusicCatalogTag(tag.Id, tag.Name,
            category.Id, category.Id == dailyStyleCategory && dailyStyleDraft.Contains(tag.Id))).ToArray())).ToArray() ?? [];
    private void DailyStyleBindings()
    {
        foreach (var name in new[] { nameof(IsDailyStyle), nameof(IsDailyStyleEditorOpen), nameof(CanEditDailyStyle),
            nameof(DailyOtherBranch), nameof(DailyOtherBranchTitle), nameof(CanConfirmDailyStyle), nameof(DailyStyleSelectionText), nameof(DailyStyleGroups) }) Changed(name);
    }
    private void RetireDailyStyleEditor()
    {
        ++dailyStyleRevision; dailyStyleEditor = null; dailyStyleDraft.Clear(); dailyStyleAccount = "";
        DailyStyleBindings();
    }
    private static JsonElement DailySelectionSnapshot(JsonElement reply) => JsonSerializer.SerializeToElement(new {
        category = MusicCatalogDto.Text(reply, "category"),
        tags = reply.TryGetProperty("tags", out var tags) ? tags.Clone() : JsonSerializer.SerializeToElement(Array.Empty<object>())
    });
    private void PublishDailyStyleSelection(JsonElement reply)
    {
        dailyStyleNeedsRead = false;
        dailyStyleSelectedCategory = MusicCatalogDto.Text(reply, "category");
        dailyStyleSelected = reply.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? tags.EnumerateArray().Select(tag => new MusicDailyStyleTag(MusicCatalogDto.Text(tag, "id"), MusicCatalogDto.Text(tag, "name"))).ToArray() : [];
        DailyStyleBindings();
    }
    public Task SwitchDailyBranchAsync(string branch)
    {
        if (!IsCatalogDaily || !CanCatalogBrowse || dailyStyleBusy || branch is not ("default" or "style")) return Task.CompletedTask;
        RetireDailyStyleEditor();
        return OpenCatalogTargetAsync(Target("daily", "wy", branch == "style" ? "风格推荐" : "每日推荐", new { branch }), branch);
    }
    public async Task OpenDailyStyleEditorAsync()
    {
        if (!CanEditDailyStyle || application is not IMusicDailyStylePreferences preferences) return;
        long revision = ++dailyStyleRevision; string account = CatalogAccountKey("wy");
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(drained.Task);
        dailyStyleBusy = true; DailyStyleBindings();
        try
        {
            var editor = await preferences.OpenDailyStyleEditorAsync(lifetime.Token);
            if (revision != dailyStyleRevision || !IsDailyStyle || account != CatalogAccountKey("wy")) return;
            dailyStyleEditor = editor; dailyStyleAccount = account; dailyStyleCategory = dailyStyleSelectedCategory;
            dailyStyleDraft.Clear(); foreach (var tag in dailyStyleSelected) dailyStyleDraft.Add(tag.Id);
        }
        catch (Exception error) { if (revision == dailyStyleRevision) { status = DailyStyleError(error); Changed(nameof(Status)); } }
        finally { loadDrains.Remove(drained.Task); drained.TrySetResult(); dailyStyleBusy = false; DailyStyleBindings(); }
    }
    public void ToggleDailyStyleTag(MusicCatalogTag tag)
    {
        if (!CanEditDailyStyle || dailyStyleEditor?.Categories.Any(c => c.Id == tag.Group && c.Tags.Any(t => t.Id == tag.Id)) != true) return;
        if (dailyStyleCategory != tag.Group) { dailyStyleCategory = tag.Group; dailyStyleDraft.Clear(); }
        if (!dailyStyleDraft.Add(tag.Id)) dailyStyleDraft.Remove(tag.Id);
        DailyStyleBindings();
    }
    public void CancelDailyStyleEditor() => RetireDailyStyleEditor();
    public async Task ConfirmDailyStyleAsync()
    {
        if (!CanConfirmDailyStyle || application is not IMusicDailyStylePreferences preferences || dailyStyleEditor is not { } editor) return;
        if (dailyStyleAccount != CatalogAccountKey("wy")) { RetireDailyStyleEditor(); return; }
        string saveAccount = dailyStyleAccount;
        long generation = loadGeneration;
        bool CanPublishSave() => !stopping && generation == loadGeneration && IsDailyStyle && saveAccount == CatalogAccountKey("wy");
        void RetireSavedAccountSnapshots()
        {
            foreach (var key in catalogLocations.Where(pair => pair.Value.Pages.Values.Any(page =>
                page.AccountStamp == saveAccount && page.Target?.Operation == "daily" &&
                MusicCatalogDto.Text(page.Target.Parameters, "branch") == "style"))
                .Select(pair => pair.Key).ToArray()) catalogLocations.Remove(key);
        }
        void SettleSavePage()
        {
            RetireSavedAccountSnapshots();
            if (stopping || !IsDailyStyle || saveAccount != CatalogAccountKey("wy")) return;
            bool returned = generation != loadGeneration;
            RetireCatalogLoad();
            generation = loadGeneration;
            InvalidateCatalogAction();
            catalogPages.Clear(); catalogAccountStamp = ""; catalogMembersLoaded = false;
            catalogSongs.Clear(); utilitySections.Clear(); selectedSong = null;
            dailyStyleNeedsRead = true;
            if (returned)
            {
                RetireDailyStyleEditor();
                dailyStyleSelected = []; dailyStyleSelectedCategory = "";
            }
            Changed(nameof(SelectedSong)); RefreshBindings(); DailyStyleBindings();
        }
        var previousPage = catalogPages.GetValueOrDefault(catalogPage);
        var selection = new MusicDailyStyleSelection(dailyStyleCategory, dailyStyleDraft.ToArray());
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(drained.Task);
        dailyStyleBusy = true; dailyStyleNeedsRead = true; DailyStyleBindings();
        try
        {
            RetireCatalogLoad();
            generation = loadGeneration;
            InvalidateCatalogAction();
            RetireSavedAccountSnapshots();
            catalogPages.Clear();
            catalogAccountStamp = "";
            catalogMembersLoaded = false;
            catalogSongs.Clear(); utilitySections.Clear(); selectedSong = null;
            Changed(nameof(SelectedSong)); RefreshBindings();
            var result = await preferences.SaveDailyStyleAsync(editor, selection, lifetime.Token);
            SettleSavePage();
            if (!CanPublishSave()) return;
            if (result.Readback is null && result.Status is (MusicDailyStyleWriteStatus.NotSent or MusicDailyStyleWriteStatus.Rejected) && previousPage is not null)
            {
                catalogPages[1] = previousPage;
                PublishCatalog(location.Route, "", "wy", "歌曲", 1, previousPage);
            }
            if (result.Readback is { } reply)
            {
                var sections = MusicCatalogDto.Read(reply, "wy", "daily", "风格推荐", "songs").ToArray();
                ValidatePersonalCatalog(reply, catalogLoadedTarget, sections.Sum(s => s.Items.Count));
                var state = new CatalogPageState(sections, [], "", "", "风格推荐", MusicCatalogDto.Text(reply, "cover"), false, null,
                    sections.Sum(s => s.Items.Count), Target: catalogLoadedTarget, AccountStamp: saveAccount, ExpiresAt: CatalogExpiry(reply), DailySelection: DailySelectionSnapshot(reply));
                catalogPages.Clear(); catalogPages[1] = state;
                PublishDailyStyleSelection(reply);
                PublishCatalog(location.Route, "", "wy", "歌曲", 1, state);
                if (result.Status == MusicDailyStyleWriteStatus.Confirmed && result.SelectionMatches == true) RetireDailyStyleEditor();
            }
            if (result.Status == MusicDailyStyleWriteStatus.Unknown || result.Status == MusicDailyStyleWriteStatus.Confirmed && result.Readback is null)
            {
                catalogAccountStamp = ""; catalogPages.Clear();
                catalogSongs.Clear(); utilitySections.Clear(); selectedSong = null;
                Changed(nameof(SelectedSong)); RefreshBindings();
            }
            dailyStyleNeedsRead = result.Status == MusicDailyStyleWriteStatus.Unknown ||
                result.Status == MusicDailyStyleWriteStatus.Confirmed && result.Readback is null;
            if (result.Status != MusicDailyStyleWriteStatus.Confirmed || result.SelectionMatches != true)
            { status = result.ErrorCode is { } code ? MusicCatalogDto.ErrorText(code) : "风格偏好尚未确认，请重新读取"; Changed(nameof(Status)); }
        }
        catch (Exception error) { SettleSavePage(); if (CanPublishSave()) { dailyStyleNeedsRead = true; status = DailyStyleError(error); Changed(nameof(Status)); } }
        finally { loadDrains.Remove(drained.Task); drained.TrySetResult(); dailyStyleBusy = false; DailyStyleBindings(); CatalogBindings(); }
    }

    private static string DailyStyleError(Exception error) => error is MusicPlatformException platform
        ? MusicCatalogDto.ErrorText(platform.Code) : ErrorText(error);
}
