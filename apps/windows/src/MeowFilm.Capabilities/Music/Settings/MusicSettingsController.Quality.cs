using System.Collections.ObjectModel;
using System.ComponentModel;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public sealed class MusicQualityRow(string key, bool enabled) : ISettingsPriorityItem
{
    private bool isEnabled = enabled;
    public string Key { get; } = key;
    public bool Enabled => isEnabled;
    public string Label => MusicQualityLabels.Name(Key);
    internal void Apply(bool value)
    {
        if (isEnabled == value) return;
        isEnabled = value;
        PropertyChanged?.Invoke(this, new(nameof(Enabled)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

[Flags]
internal enum QualitySettingsSurface
{
    None = 0,
    Settings = 1,
    Player = 2
}

public sealed partial class MusicSettingsController
{
    private MusicQualityPolicyRecord? savedQualityPolicy;
    private string savedQualitySignature = "", qualityStatus = "";
    private bool qualityLoaded, qualityBusy, qualitySaving;
    private long qualityGeneration;
    private QualitySettingsSurface qualitySurfaces;
    private CancellationTokenSource? qualityCancellation;
    private Task qualityWork = Task.CompletedTask;

    public ObservableCollection<MusicQualityRow> QualityCategories { get; } = [];
    public ObservableCollection<MusicQualityRow> SpatialQualities { get; } = [];
    public string[] PlaybackQualityOptions { get; } = ["auto", "128k", "192k", "320k", "flac", "flac24bit", "master"];
    public string PlaybackQualityRequest { get; private set; } = "flac";
    public string QualityStatus => qualityStatus;
    public bool CanEditQuality => CanEdit && qualityLoaded && !qualityBusy;
    internal MusicQualityPolicyRecord? SavedQualityPolicy => savedQualityPolicy;
    internal string SavedQualitySignature => savedQualitySignature;

    internal void QualityBindings()
    {
        Changed(nameof(CanEditQuality));
        Changed(nameof(QualityStatus));
        Changed(nameof(PlaybackQualityRequest));
    }

    internal void AcceptQuality(MusicQualitySettingsResult result)
    {
        var priorSignature = savedQualitySignature;
        savedQualityPolicy = result.Settings;
        savedQualitySignature = result.Signature;
        MusicDto.Require(IsQualityHash(savedQualitySignature));
        if (priorSignature.Length > 0 && priorSignature != savedQualitySignature)
            playbackPort.QualityPolicyChanged(savedQualitySignature);
        else if (playbackPort.PreparedAudioReceiptSignature is { } preparedSig &&
            preparedSig != savedQualitySignature)
            playbackPort.CancelMusicPreload();
        PublishQualitySettings();
        qualityLoaded = true;
        QualityBindings();
    }

    private void PublishQualitySettings()
    {
        if (savedQualityPolicy is not { } policy) return;
        PlaybackQualityRequest = policy.Request;
        void Rows(ObservableCollection<MusicQualityRow> destination, string[] order, string[] enabled, string[] options)
        {
            var active = enabled.ToHashSet(StringComparer.Ordinal);
            var keys = order.Concat(options.Except(order)).ToArray();
            ReconcilePriorityRows(destination, keys, row => row.Key,
                key => new(key, active.Contains(key)),
                (row, key) => row.Apply(active.Contains(key)));
        }
        Rows(QualityCategories, policy.QualityOrder, policy.QualityEnabled, MusicQualityPolicy.Categories);
        Rows(SpatialQualities, policy.SpatialOrder, policy.SpatialEnabled, MusicQualityPolicy.Spatial);
    }

    private MusicQualityPolicyRecord DesiredQuality(string? request = null)
    {
        MusicDto.Require(savedQualityPolicy is not null);
        return savedQualityPolicy! with { Request = request ?? PlaybackQualityRequest };
    }

    public Task SetPlaybackQualityRequestAsync(string request)
    {
        if (!CanEditQuality || !PlaybackQualityOptions.Contains(request) ||
            request == PlaybackQualityRequest) return Task.CompletedTask;
        return SaveQualitySettings(DesiredQuality(request));
    }

    public Task SetQualityEnabledAsync(MusicQualityRow row, bool enabled, bool spatial)
    {
        if (!CanEditQuality) return Task.CompletedTask;
        var rows = spatial ? SpatialQualities : QualityCategories;
        if (!rows.Contains(row) || row.Enabled == enabled) return Task.CompletedTask;
        var desired = DesiredQuality();
        var active = rows.Where(value => value.Enabled).Select(value => value.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (enabled) active.Add(row.Key); else active.Remove(row.Key);
        var nextEnabled = rows.Where(value => active.Contains(value.Key)).Select(value => value.Key).ToArray();
        if (spatial)
        {
            var nextOrder = enabled && !desired.SpatialOrder.Contains(row.Key)
                ? desired.SpatialOrder.Append(row.Key).ToArray()
                : desired.SpatialOrder;
            desired = desired with { SpatialEnabled = nextEnabled, SpatialOrder = nextOrder };
        }
        else
        {
            var nextOrder = enabled && !desired.QualityOrder.Contains(row.Key)
                ? desired.QualityOrder.Append(row.Key).ToArray()
                : desired.QualityOrder;
            desired = desired with { QualityEnabled = nextEnabled, QualityOrder = nextOrder };
        }
        return SaveQualitySettings(desired);
    }

    public Task MoveQualityAsync(MusicQualityRow source, MusicQualityRow target, bool spatial)
    {
        if (!CanEditQuality || ReferenceEquals(source, target)) return Task.CompletedTask;
        var rows = spatial ? SpatialQualities : QualityCategories;
        if (!rows.Contains(source) || !rows.Contains(target)) return Task.CompletedTask;
        var order = rows.Select(row => row.Key).ToList();
        int from = order.IndexOf(source.Key), to = order.IndexOf(target.Key);
        if (from < 0 || to < 0) return Task.CompletedTask;
        order.RemoveAt(from);
        order.Insert(to, source.Key);
        var desired = DesiredQuality();
        desired = spatial
            ? desired with { SpatialOrder = order.ToArray() }
            : desired with { QualityOrder = order.ToArray() };
        return SaveQualitySettings(desired);
    }

    public Task DeactivateQualityAsync() => DeactivateQualityAsync(QualitySettingsSurface.Player);

    internal Task DeactivateQualityAsync(QualitySettingsSurface surface) =>
        ReleaseQualitySurface(surface) ? qualityWork : Task.CompletedTask;

    private bool ReleaseQualitySurface(QualitySettingsSurface surface)
    {
        if ((qualitySurfaces & surface) == 0) return false;
        qualitySurfaces &= ~surface;
        if (qualitySurfaces != QualitySettingsSurface.None) return false;
        RetireQualitySettings();
        return true;
    }

    private void RetireQualitySettings()
    {
        qualityGeneration++;
        qualityLoaded = false;
        if (!qualitySaving) qualityCancellation?.Cancel();
        QualityBindings();
    }

    private bool QualityCurrent(long generation) => generation == qualityGeneration && !stopping && !disposed;

    public Task ActivateQualitySettingsAsync() => ActivateQualitySettingsAsync(QualitySettingsSurface.Player);

    internal async Task ActivateQualitySettingsAsync(QualitySettingsSurface surface)
    {
        qualitySurfaces |= surface;
        if (qualityLoaded || stopping || disposed) return;
        long generation = qualityGeneration;
        if (qualityBusy) await qualityWork;
        if (!QualityCurrent(generation) || qualityLoaded || qualitySurfaces == QualitySettingsSurface.None) return;
        await EnsureQualitySettingsAsync();
    }

    private Task EnsureQualitySettingsAsync() => BeginQuality(async (token, generation) =>
    {
        var result = await RunSettingReceiptAsync(MusicSettingReceiptKeys.QualityPolicy,
            ct => application.GetQualityPolicyAsync(ct), token);
        token.ThrowIfCancellationRequested();
        if (!QualityCurrent(generation)) return;
        AcceptQuality(result);
        qualityStatus = "";
    });

    private bool QualitySettingsMatch(MusicQualityPolicyRecord desired) =>
        savedQualityPolicy is { } current &&
        current.Request == desired.Request &&
        current.QualityOrder.SequenceEqual(desired.QualityOrder) &&
        current.QualityEnabled.SequenceEqual(desired.QualityEnabled) &&
        current.SpatialOrder.SequenceEqual(desired.SpatialOrder) &&
        current.SpatialEnabled.SequenceEqual(desired.SpatialEnabled);

    private Task SaveQualitySettings(MusicQualityPolicyRecord desired)
    {
        // Seal the complete desired policy before scheduling; the settings surface
        // never owns an unsaved draft distinct from the native owner.
        return BeginQuality(async (token, generation) =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                // The accepted policy change retires future refresh authority in
                // AcceptQuality. The already opened media keeps its playback token.
                ctx.CancelOnlinePlayback();
                var saved = await RunSettingReceiptAsync(MusicSettingReceiptKeys.QualityPolicy,
                    ct => application.SaveQualityPolicyAsync(desired.Revision, desired.Request,
                        desired.QualityOrder, desired.QualityEnabled, desired.SpatialOrder, desired.SpatialEnabled, ct),
                    token);
                if (!QualityCurrent(generation)) return;
                AcceptQuality(saved); qualityStatus = "";
                var online = await application.GetOnlineSettingsAsync(token);
                if (!QualityCurrent(generation)) return;
                lyricsPort.PublishOnlineSettings(online.LyricsEnabled);
            }
            catch (MusicOperationException error) when (error.Code is
                "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE" or "REVISION_CONFLICT")
            {
                if (!QualityCurrent(generation)) return;
                bool matches;
                try
                {
                    var current = await RunSettingReceiptAsync(MusicSettingReceiptKeys.QualityPolicy,
                        ct => application.GetQualityPolicyAsync(ct), lifetime.Token);
                    if (!QualityCurrent(generation)) return;
                    AcceptQuality(current);
                    var online = await application.GetOnlineSettingsAsync(lifetime.Token);
                    if (!QualityCurrent(generation)) return;
                    lyricsPort.PublishOnlineSettings(online.LyricsEnabled);
                    matches = QualitySettingsMatch(desired);
                }
                catch
                {
                    qualityLoaded = false;
                    throw;
                }
                if (matches)
                    qualityStatus = "";
                else if (error.Code == "REVISION_CONFLICT")
                    qualityStatus = "音质设置已更新";
                else
                    throw;
            }
        }, saving: true);
    }

    private Task BeginQuality(Func<CancellationToken, long, Task> action, bool saving = false)
    {
        if (saving && !CanEdit || qualityBusy || stopping || disposed) return Task.CompletedTask;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        long generation = qualityGeneration;
        qualitySaving = saving;
        qualityCancellation = request; qualityBusy = true; qualityStatus = ""; QualityBindings();
        return qualityWork = RunAsync(async () =>
        {
            try { await action(request.Token, generation); }
            catch (OperationCanceledException) { if (QualityCurrent(generation)) qualityStatus = "已取消"; }
            catch (MusicOperationException error) when (error.Code == "CANCELLED")
            { if (QualityCurrent(generation)) qualityStatus = "已取消"; }
            catch (MusicOperationException error)
            {
                if (!QualityCurrent(generation)) return;
                qualityStatus = error.Code switch
                {
                    "INVALID_REQUEST" => "音质设置无效",
                    "PENDING_WRITE" or "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" => saving ? "音质设置保存失败" : "音质设置加载失败",
                    _ => saving ? "音质设置操作失败" : "音质设置加载失败"
                };
            }
            catch when (!QualityCurrent(generation)) { }
            finally
            {
                if (ReferenceEquals(qualityCancellation, request)) qualityCancellation = null;
                request.Dispose(); qualityBusy = false; qualitySaving = false;
                if (QualityCurrent(generation)) QualityBindings();
            }
        });
    }

    internal static bool IsQualityHash(string value) => value.Length == 64 &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
