using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public sealed class MusicQualityRow(string key, bool enabled) : ISettingsPriorityItem
{
    private bool isEnabled = enabled;
    public string Key { get; } = key;
    public string Platform => "";
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

public sealed partial class MusicSettingsController
{
    private JsonObject? savedQualityPolicy;
    private string savedQualitySignature = "", qualityStatus = "";
    private bool qualityLoaded, qualityBusy, qualitySaving;
    private long qualityGeneration;
    private CancellationTokenSource? qualityCancellation;
    private Task qualityWork = Task.CompletedTask;

    public ObservableCollection<MusicQualityRow> QualityCategories { get; } = [];
    public ObservableCollection<MusicQualityRow> SpatialQualities { get; } = [];
    public string[] PlaybackQualityOptions { get; } = ["auto", "128k", "192k", "320k", "flac", "flac24bit", "master"];
    public string PlaybackQualityRequest { get; private set; } = "flac";
    public string QualityStatus => qualityStatus;
    public bool CanEditQuality => CanEdit && qualityLoaded && !qualityBusy;
    internal JsonObject? SavedQualityPolicy => savedQualityPolicy;
    internal string SavedQualitySignature => savedQualitySignature;

    internal void QualityBindings()
    {
        Changed(nameof(CanEditQuality));
        Changed(nameof(QualityStatus));
        Changed(nameof(PlaybackQualityRequest));
    }

    private static JsonObject QualityObject(JsonElement value) => JsonNode.Parse(value.GetRawText())!.AsObject();

    // Unlike the general UI Call helper, this awaits the physical owner task.
    // In particular a successful admitted CAS is not changed into cancellation.
    internal Task<JsonElement> QualityCall(string method, object? parameters, CancellationToken token) =>
        CallSettledReceipt(method, parameters, token);

    internal void AcceptQuality(JsonElement reply)
    {
        var priorSignature = savedQualitySignature;
        var policy = QualityObject(reply.GetProperty("settings"));
        MusicQualityPolicy.Validate(policy);
        savedQualityPolicy = policy;
        savedQualitySignature = MusicDto.Text(reply, "signature");
        MusicDto.Require(IsQualityHash(savedQualitySignature));
        if (priorSignature.Length > 0 && priorSignature != savedQualitySignature)
            OnQualityPolicyChanged(savedQualitySignature);
        else if (GetPreparedAudioReceiptSignature() is { } preparedSig &&
            preparedSig != savedQualitySignature)
            OnCancelMusicPreload();
        PublishQualitySettings();
        qualityLoaded = true;
        QualityBindings();
    }

    private void PublishQualitySettings()
    {
        if (savedQualityPolicy is null) return;
        PlaybackQualityRequest = LJ.S(savedQualityPolicy["request"]);
        void Rows(ObservableCollection<MusicQualityRow> destination, string order, string enabled, string[] options)
        {
            var active = MusicQualityPolicy.Strings(savedQualityPolicy[enabled]);
            var keys = MusicQualityPolicy.Strings(savedQualityPolicy[order]);
            keys = keys.Concat(options.Except(keys)).ToArray();
            var existing = destination.ToDictionary(row => row.Key, StringComparer.Ordinal);
            for (var index = 0; index < keys.Length; index++)
            {
                var key = keys[index];
                if (!existing.TryGetValue(key, out var row))
                    row = new(key, active.Contains(key));
                else
                    row.Apply(active.Contains(key));
                var currentIndex = destination.IndexOf(row);
                if (currentIndex < 0) destination.Insert(index, row);
                else if (currentIndex != index) destination.Move(currentIndex, index);
            }
            while (destination.Count > keys.Length) destination.RemoveAt(destination.Count - 1);
        }
        Rows(QualityCategories, "qualityOrder", "qualityEnabled", MusicQualityPolicy.Categories);
        Rows(SpatialQualities, "spatialOrder", "spatialEnabled", MusicQualityPolicy.Spatial);
    }

    private JsonObject QualityRequest(string? request = null)
    {
        MusicDto.Require(savedQualityPolicy is not null);
        return LJ.Obj(("ifRevision", savedQualityPolicy!["revision"]),
            ("request", request ?? PlaybackQualityRequest),
            ("qualityOrder", savedQualityPolicy["qualityOrder"]),
            ("qualityEnabled", savedQualityPolicy["qualityEnabled"]),
            ("spatialOrder", savedQualityPolicy["spatialOrder"]),
            ("spatialEnabled", savedQualityPolicy["spatialEnabled"]));
    }

    public Task SetPlaybackQualityRequestAsync(string request)
    {
        if (!CanEditQuality || !PlaybackQualityOptions.Contains(request) ||
            request == PlaybackQualityRequest) return Task.CompletedTask;
        return SaveQualitySettings(QualityRequest(request));
    }

    public Task SetQualityEnabledAsync(MusicQualityRow row, bool enabled, bool spatial)
    {
        if (!CanEditQuality) return Task.CompletedTask;
        var rows = spatial ? SpatialQualities : QualityCategories;
        if (!rows.Contains(row) || row.Enabled == enabled) return Task.CompletedTask;
        var request = QualityRequest();
        string field = spatial ? "spatialEnabled" : "qualityEnabled";
        var active = rows.Where(value => value.Enabled).Select(value => value.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (enabled) active.Add(row.Key); else active.Remove(row.Key);
        request[field] = new JsonArray(rows.Where(value => active.Contains(value.Key))
            .Select(value => JsonValue.Create(value.Key)).ToArray());
        string orderField = spatial ? "spatialOrder" : "qualityOrder";
        if (enabled && !MusicQualityPolicy.Strings(request[orderField]).Contains(row.Key))
            request[orderField] = LJ.Arr(MusicQualityPolicy.Strings(request[orderField]).Append(row.Key));
        return SaveQualitySettings(request);
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
        var request = QualityRequest();
        request[spatial ? "spatialOrder" : "qualityOrder"] =
            new JsonArray(order.Select(value => JsonValue.Create(value)).ToArray());
        return SaveQualitySettings(request);
    }

    public async Task DeactivateQualityAsync()
    {
        RetireQualitySettings();
        await qualityWork;
    }

    private void RetireQualitySettings()
    {
        qualityGeneration++;
        qualityLoaded = false;
        if (!qualitySaving) qualityCancellation?.Cancel();
        QualityBindings();
    }

    private bool QualityCurrent(long generation) => generation == qualityGeneration && !stopping && !disposed;

    public Task ActivateQualitySettingsAsync() => qualityLoaded
        ? Task.CompletedTask
        : EnsureQualitySettingsAsync();

    private Task EnsureQualitySettingsAsync() => BeginQuality(async (token, generation) =>
    {
        var reply = await QualityCall("music.quality.settings.get", null, token);
        token.ThrowIfCancellationRequested();
        if (!QualityCurrent(generation)) return;
        AcceptQuality(reply);
        qualityStatus = "";
    });

    private bool QualitySettingsMatch(JsonObject request)
    {
        if (savedQualityPolicy is null || LJ.S(savedQualityPolicy["request"]) != LJ.S(request["request"]))
            return false;
        return MusicQualityPolicy.Strings(savedQualityPolicy["qualityOrder"])
                .SequenceEqual(MusicQualityPolicy.Strings(request["qualityOrder"])) &&
            MusicQualityPolicy.Strings(savedQualityPolicy["qualityEnabled"])
                .SequenceEqual(MusicQualityPolicy.Strings(request["qualityEnabled"])) &&
            MusicQualityPolicy.Strings(savedQualityPolicy["spatialOrder"])
                .SequenceEqual(MusicQualityPolicy.Strings(request["spatialOrder"])) &&
            MusicQualityPolicy.Strings(savedQualityPolicy["spatialEnabled"])
                .SequenceEqual(MusicQualityPolicy.Strings(request["spatialEnabled"]));
    }

    private Task SaveQualitySettings(JsonObject request)
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
                OnCancelOnlinePlayback();
                var reply = await QualityCall("music.quality.settings.save", request, token);
                if (!QualityCurrent(generation)) return;
                AcceptQuality(reply); qualityStatus = "";
                var online = await Call("music.settings.get", null, token);
                if (!QualityCurrent(generation)) return;
                OnPublishOnlineLyricsSettings(online.GetProperty("settings"));
            }
            catch (MusicOperationException error) when (error.Code is
                "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE" or "REVISION_CONFLICT")
            {
                if (!QualityCurrent(generation)) return;
                bool matches;
                try
                {
                    var current = await QualityCall("music.quality.settings.get", null, lifetime.Token);
                    if (!QualityCurrent(generation)) return;
                    AcceptQuality(current);
                    var online = await Call("music.settings.get", null, lifetime.Token);
                    if (!QualityCurrent(generation)) return;
                    OnPublishOnlineLyricsSettings(online.GetProperty("settings"));
                    matches = QualitySettingsMatch(request);
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
        if (!CanEdit || qualityBusy || stopping) return Task.CompletedTask;
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
                else qualityLoaded = false;
            }
        });
    }

    internal static bool IsQualityHash(string value) => value.Length == 64 &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
