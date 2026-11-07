using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public sealed class MusicQualityRow(string key, bool enabled) : INotifyPropertyChanged
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

public sealed partial class MusicCapability
{
    private JsonObject? savedQualityPolicy;
    private string savedQualitySignature = "", qualityStatus = "";
    private bool qualityLoaded, qualityBusy;
    private CancellationTokenSource? qualityCancellation;
    private Task qualityWork = Task.CompletedTask;
    public ObservableCollection<MusicQualityRow> QualityCategories { get; } = [];
    public ObservableCollection<MusicQualityRow> SpatialQualities { get; } = [];
    public string[] PlaybackQualityOptions { get; } = ["auto", "128k", "192k", "320k", "flac", "flac24bit", "master"];
    public string PlaybackQualityRequest { get; private set; } = "flac";
    public string QualityStatus => qualityStatus;
    public bool CanEditQuality => CanEdit && qualityLoaded && !qualityBusy;
    internal void QualityBindings()
    {
        Changed(nameof(CanEditQuality));
        Changed(nameof(QualityStatus)); Changed(nameof(PlaybackQualityRequest));
    }
    private static JsonObject QualityObject(JsonElement value) => JsonNode.Parse(value.GetRawText())!.AsObject();

    // Unlike the general UI Call helper, this awaits the physical owner task.
    // In particular a successful admitted CAS is not changed into cancellation.
    private async Task<JsonElement> QualityCall(string method, object? parameters, CancellationToken token)
    {
        var result = await application.ExecuteAsync(method, parameters, token);
        MusicDto.Require(result.ValueKind == JsonValueKind.Object && result.TryGetProperty("ok", out var ok) &&
            ok.ValueKind is JsonValueKind.True or JsonValueKind.False);
        if (!result.GetProperty("ok").GetBoolean()) throw new MusicOperationException(MusicDto.Text(result, "code"));
        return result;
    }
    private void AcceptQuality(JsonElement reply)
    {
        var priorSignature = savedQualitySignature;
        var policy = QualityObject(reply.GetProperty("settings"));
        MusicQualityPolicy.Validate(policy);
        savedQualityPolicy = policy;
        savedQualitySignature = MusicDto.Text(reply, "signature");
        MusicDto.Require(IsQualityHash(savedQualitySignature));
        if (priorSignature.Length > 0 && priorSignature != savedQualitySignature)
            QualityPolicyChanged(savedQualitySignature);
        else if (preparedAudio is { } prepared &&
            MusicDto.Text(prepared.Receipt, "qualityPolicySignature") != savedQualitySignature)
            CancelMusicPreload();
        PublishQualitySettings();
        qualityLoaded = true;
        QualityBindings();
    }
    private void PublishQualitySettings()
    {
        if (savedQualityPolicy is null) return;
        PlaybackQualityRequest = LJ.S(savedQualityPolicy["request"]);
        void Rows(ObservableCollection<MusicQualityRow> destination, string order, string enabled)
        {
            var active = MusicQualityPolicy.Strings(savedQualityPolicy[enabled]);
            var keys = MusicQualityPolicy.Strings(savedQualityPolicy[order]);
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
        Rows(QualityCategories, "qualityOrder", "qualityEnabled");
        Rows(SpatialQualities, "spatialOrder", "spatialEnabled");
    }
    private JsonObject QualityRequest(string? request = null)
    {
        MusicDto.Require(savedQualityPolicy is not null);
        return LJ.Obj(("ifRevision", savedQualityPolicy!["revision"]),
            ("request", request ?? PlaybackQualityRequest),
            ("qualityOrder", QualityCategories.Select(r => r.Key).ToArray()),
            ("qualityEnabled", QualityCategories.Where(r => r.Enabled).Select(r => r.Key).ToArray()),
            ("spatialOrder", SpatialQualities.Select(r => r.Key).ToArray()),
            ("spatialEnabled", SpatialQualities.Where(r => r.Enabled).Select(r => r.Key).ToArray()));
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
        qualityCancellation?.Cancel();
        await qualityWork;
    }
    public Task ActivateQualitySettingsAsync() => qualityLoaded
        ? Task.CompletedTask
        : EnsureQualitySettingsAsync();
    private Task EnsureQualitySettingsAsync() => BeginQuality(async token =>
    {
        var reply = await QualityCall("music.quality.settings.get", null, token);
        token.ThrowIfCancellationRequested();
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
        return BeginQuality(async token =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                // The accepted policy change retires future refresh authority in
                // AcceptQuality. The already opened media keeps its playback token.
                onlineCancellation?.Cancel();
                var reply = await QualityCall("music.quality.settings.save", request, token);
                AcceptQuality(reply); qualityStatus = "";
                PublishOnlineSettings(reply.GetProperty("onlineSettings"));
            }
            catch (MusicOperationException error) when (error.Code is
                "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE" or "REVISION_CONFLICT")
            {
                bool matches;
                try
                {
                    var current = await QualityCall("music.quality.settings.get", null, token);
                    token.ThrowIfCancellationRequested();
                    AcceptQuality(current);
                    PublishOnlineSettings(current.GetProperty("onlineSettings"));
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
        });
    }
    private Task BeginQuality(Func<CancellationToken, Task> action)
    {
        if (!CanEdit || qualityBusy || stopping) return Task.CompletedTask;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        qualityCancellation = request; qualityBusy = true; qualityStatus = ""; QualityBindings();
        return qualityWork = RunAsync(async () =>
        {
            try { await action(request.Token); }
            catch (OperationCanceledException) { qualityStatus = "已取消"; }
            catch (MusicOperationException error) when (error.Code == "CANCELLED") { qualityStatus = "已取消"; }
            catch (MusicOperationException error)
            {
                qualityStatus = error.Code switch
                {
                    "QUALITY_REQUIRED" => "请至少保留一种可用音质",
                    "INVALID_REQUEST" => "音质设置无效",
                    "PENDING_WRITE" or "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" => "音质设置保存失败",
                    _ => "音质设置操作失败"
                };
            }
            finally
            {
                if (ReferenceEquals(qualityCancellation, request)) qualityCancellation = null;
                request.Dispose(); qualityBusy = false; QualityBindings();
            }
        });
    }
    private static bool IsQualityHash(string value) => value.Length == 64 &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
