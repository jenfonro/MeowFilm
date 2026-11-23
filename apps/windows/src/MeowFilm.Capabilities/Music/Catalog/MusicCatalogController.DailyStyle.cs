using MeowFilm.Capabilities.Platforms;
using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCatalogController
{
    private MusicDailyStyleEditor? dailyStyleEditor;
    private string dailyStyleAccount = "", dailyStyleCategory = "", dailyStyleSelectedCategory = "";
    private MusicDailyStyleTag[] dailyStyleSelected = [];
    private readonly HashSet<string> dailyStyleDraft = new(StringComparer.Ordinal);
    private IReadOnlyList<MusicCatalogTagGroup> dailyStyleGroups = [];
    private long dailyStyleRevision;
    private bool dailyStyleBusy, dailyStyleNeedsRead;

    public string DailyOtherBranch => IsDailyStyle ? "default" : "style";
    public string DailyOtherBranchTitle => IsDailyStyle ? "每日推荐" : "风格推荐";
    public bool IsDailyStyle => IsCatalogDaily && CatalogDailyBranch == "style";
    public bool IsDailyStyleEditorOpen => dailyStyleEditor is not null;
    public bool CanEditDailyStyle => IsDailyStyle && !dailyStyleBusy && ctx.LoadCancellation is null && CanCatalogBrowse;
    public bool CanConfirmDailyStyle => CanEditDailyStyle && !dailyStyleNeedsRead && dailyStyleDraft.Count > 0 && IsDailyStyleEditorOpen;
    public string DailyStyleSelectionText => dailyStyleSelected.Length == 0 ? "选择风格" : string.Join(" · ", dailyStyleSelected.Select(t => t.Name));
    public IReadOnlyList<MusicCatalogTagGroup> DailyStyleGroups => dailyStyleGroups;

    private void RebuildDailyStyleGroups()
    {
        dailyStyleGroups = dailyStyleEditor?.Categories.Select(category =>
            new MusicCatalogTagGroup(category.Name, category.Tags.Select(tag => new MusicCatalogTag(tag.Id, tag.Name,
                category.Id, category.Id == dailyStyleCategory && dailyStyleDraft.Contains(tag.Id))).ToArray())).ToArray() ?? [];
        Changed(nameof(DailyStyleGroups));
    }

    private void DailyStyleBindings()
    {
        Changed(nameof(IsDailyStyle));
        Changed(nameof(CanEditDailyStyle));
        Changed(nameof(DailyOtherBranch));
        Changed(nameof(DailyOtherBranchTitle));
        Changed(nameof(CanConfirmDailyStyle));
        Changed(nameof(DailyStyleSelectionText));
    }

    internal void RetireDailyStyleEditor()
    {
        bool wasOpen = IsDailyStyleEditorOpen;
        bool hadGroups = dailyStyleGroups.Count > 0;
        ++dailyStyleRevision; dailyStyleEditor = null; dailyStyleDraft.Clear(); dailyStyleAccount = "";
        if (hadGroups) { dailyStyleGroups = []; Changed(nameof(DailyStyleGroups)); }
        if (wasOpen) Changed(nameof(IsDailyStyleEditorOpen));
        DailyStyleBindings();
    }

    private static JsonElement DailySelectionSnapshot(JsonElement reply) => JsonSerializer.SerializeToElement(new
    {
        category = MusicCatalogDto.Text(reply, "category"),
        tags = MusicCatalogDto.Rows(MusicCatalogDto.Field(reply, "tags")).ToArray()
    });

    private void PublishDailyStyleSelection(JsonElement reply)
    {
        dailyStyleNeedsRead = false;
        dailyStyleSelectedCategory = MusicCatalogDto.Text(reply, "category");
        dailyStyleSelected = MusicCatalogDto.Rows(MusicCatalogDto.Field(reply, "tags"))
            .Select(tag => new MusicDailyStyleTag(MusicCatalogDto.Text(tag, "id"), MusicCatalogDto.Text(tag, "name"))).ToArray();
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
        if (!CanEditDailyStyle) return;
        long revision = ++dailyStyleRevision; string account = CatalogAccountKey("wy");
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(drained.Task);
        dailyStyleBusy = true; DailyStyleBindings();
        try
        {
            var editor = await application.OpenDailyStyleEditorAsync(lifetime.Token);
            if (revision != dailyStyleRevision || !IsDailyStyle || account != CatalogAccountKey("wy")) return;
            bool wasOpen = IsDailyStyleEditorOpen;
            dailyStyleEditor = editor; dailyStyleAccount = account; dailyStyleCategory = dailyStyleSelectedCategory;
            dailyStyleDraft.Clear(); foreach (var tag in dailyStyleSelected) dailyStyleDraft.Add(tag.Id);
            RebuildDailyStyleGroups();
            if (!wasOpen) Changed(nameof(IsDailyStyleEditorOpen));
        }
        catch (Exception error) { if (revision == dailyStyleRevision) ctx.SetStatus(DailyStyleError(error)); }
        finally { loadDrains.Remove(drained.Task); drained.TrySetResult(); dailyStyleBusy = false; DailyStyleBindings(); }
    }

    public void ToggleDailyStyleTag(MusicCatalogTag tag)
    {
        if (!CanEditDailyStyle || dailyStyleEditor?.Categories.Any(c => c.Id == tag.Group && c.Tags.Any(t => t.Id == tag.Id)) != true) return;
        if (dailyStyleCategory != tag.Group) { dailyStyleCategory = tag.Group; dailyStyleDraft.Clear(); }
        if (!dailyStyleDraft.Add(tag.Id)) dailyStyleDraft.Remove(tag.Id);
        RebuildDailyStyleGroups();
        DailyStyleBindings();
    }

    public void CancelDailyStyleEditor() => RetireDailyStyleEditor();

    public async Task ConfirmDailyStyleAsync()
    {
        if (!CanConfirmDailyStyle || dailyStyleEditor is not { } editor) return;
        if (dailyStyleAccount != CatalogAccountKey("wy")) { RetireDailyStyleEditor(); return; }
        string saveAccount = dailyStyleAccount;
        string receiptKey = "music.platform.dailyStyle:wy:" + editor.UserId;
        long submission = Receipts.NextSubmission();
        long generation = ctx.LoadGeneration;
        bool CanPublishSave() => !stopping && generation == ctx.LoadGeneration && IsDailyStyle && saveAccount == CatalogAccountKey("wy");
        void RetireSavedAccountSnapshots()
        {
            bool BelongsToSavedAccount(CatalogPageState page) => page.AccountStamp == saveAccount &&
                page.Target?.Operation == "daily" && MusicCatalogDto.Text(page.Target.Parameters, "branch") == "style";
            foreach (var key in catalogLocations.Where(pair => pair.Value.Pages.Values.Any(BelongsToSavedAccount))
                .Select(pair => pair.Key).ToArray()) RetireCatalogLocation(key);
            foreach (var page in catalogPages.Where(pair => BelongsToSavedAccount(pair.Value)).ToArray())
                catalogPages[page.Key] = page.Value with { ExpiresAt = 0 };
        }
        void SettleSavePage()
        {
            RetireSavedAccountSnapshots();
            if (!CanPublishSave()) return;
            catalogPages.Clear(); catalogAccountStamp = ""; catalogMembersLoaded = false;
            GetCatalogSongsSource().Clear(); utilitySections.Clear();
            dailyStyleNeedsRead = true;
            OnClearSelectionOnly(); ctx.RefreshBindings();
        }
        var previousPage = catalogPages.GetValueOrDefault(catalogPage);
        var selection = new MusicDailyStyleSelection(dailyStyleCategory, dailyStyleDraft.ToArray());
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(drained.Task);
        dailyStyleBusy = true; dailyStyleNeedsRead = true; DailyStyleBindings();
        try
        {
            RetireCatalogLoad();
            generation = ctx.LoadGeneration;
            InvalidateCatalogAction();
            RetireSavedAccountSnapshots();
            catalogPages.Clear();
            catalogAccountStamp = "";
            catalogMembersLoaded = false;
            GetCatalogSongsSource().Clear(); utilitySections.Clear();
            OnClearSelectionOnly(); ctx.RefreshBindings();
            var result = await application.SaveDailyStyleAsync(editor, selection, lifetime.Token);
            if (result.Status is MusicDailyStyleWriteStatus.Confirmed or MusicDailyStyleWriteStatus.Unknown)
                Receipts.Record(receiptKey, "风格偏好", submission, result.Status == MusicDailyStyleWriteStatus.Confirmed);
            SettleSavePage();
            if (!CanPublishSave()) return;
            if (result.Readback is null && result.Status is (MusicDailyStyleWriteStatus.NotSent or MusicDailyStyleWriteStatus.Rejected) && previousPage is not null)
            {
                if (!await PublishCatalogAsync(ctx.CurrentRoute, "", "wy", "歌曲", 1, previousPage,
                    token: lifetime.Token, isCurrent: CanPublishSave,
                    beforePublish: () => catalogPages[1] = previousPage)) return;
            }
            if (result.Readback is { } reply)
            {
                var sections = MusicCatalogDto.Read(reply, "wy", "daily", "风格推荐", "songs").ToArray();
                var state = new CatalogPageState(sections, [], "", "风格推荐", MusicCatalogDto.Text(reply, "cover"), false, null,
                    sections.Sum(s => s.Items.Count), Target: catalogLoadedTarget, AccountStamp: saveAccount, ExpiresAt: CatalogExpiry(reply), DailySelection: DailySelectionSnapshot(reply));
                if (!await PublishCatalogAsync(ctx.CurrentRoute, "", "wy", "歌曲", 1, state,
                    token: lifetime.Token, isCurrent: CanPublishSave, beforePublish: () =>
                    {
                        catalogPages.Clear(); catalogPages[1] = state;
                    })) return;
                if (result.Status == MusicDailyStyleWriteStatus.Confirmed && result.SelectionMatches == true) RetireDailyStyleEditor();
            }
            if (result.Status == MusicDailyStyleWriteStatus.Unknown || result.Status == MusicDailyStyleWriteStatus.Confirmed && result.Readback is null)
            {
                catalogAccountStamp = ""; catalogPages.Clear();
                GetCatalogSongsSource().Clear(); utilitySections.Clear();
                OnClearSelectionOnly(); ctx.RefreshBindings();
            }
            dailyStyleNeedsRead = result.Status == MusicDailyStyleWriteStatus.Unknown ||
                result.Status == MusicDailyStyleWriteStatus.Confirmed && result.Readback is null;
            if (result.Status != MusicDailyStyleWriteStatus.Confirmed || result.SelectionMatches != true || result.Readback is null)
                ctx.SetStatus(result.ErrorCode is { } code ? MusicCatalogDto.ErrorText(code) : "风格偏好尚未确认，请重新读取");
        }
        catch (Exception error)
        {
            if (error is MusicPlatformException { Code: "COMMIT_UNKNOWN" })
                Receipts.Record(receiptKey, "风格偏好", submission, confirmed: false);
            SettleSavePage();
            if (CanPublishSave()) { dailyStyleNeedsRead = true; ctx.SetStatus(DailyStyleError(error)); }
        }
        finally { loadDrains.Remove(drained.Task); drained.TrySetResult(); dailyStyleBusy = false; CatalogBindings(); }
    }

    private static string DailyStyleError(Exception error) => error is MusicPlatformException platform
        ? MusicCatalogDto.ErrorText(platform.Code) : ErrorText(error);
}
