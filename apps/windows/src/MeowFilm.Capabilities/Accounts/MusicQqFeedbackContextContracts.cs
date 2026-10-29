// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json.Serialization;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

// Same-assembly native capability, not an account DTO, command, Node port or comm override.
internal interface IMusicQqFeedbackContextSource
{
    // Every acquire performs live exact-account verification. Expected failures are
    // MusicPlatformException codes; caller cancellation is OperationCanceledException.
    ValueTask<IMusicQqFeedbackContextLease> AcquireForAccountAsync(
        MusicPlatformSession exactAccount, CancellationToken token);
}

internal interface IMusicQqFeedbackContextLease : IAsyncDisposable
{
    [JsonIgnore] MusicPlatformSession Account { get; }
    [JsonIgnore] bool IsCurrent { get; }
    [JsonIgnore] CancellationToken Revoked { get; }
    [JsonIgnore] MusicQqFeedbackDeviceSnapshot Device { get; }
}

[DebuggerDisplay("MusicQqFeedbackDeviceSnapshot (redacted)")]
internal sealed class MusicQqFeedbackDeviceSnapshot
{
    internal MusicQqFeedbackDeviceSnapshot(string installationId, long deviceGeneration, long sessionGeneration,
        string openUdid, string openUdid2, string androidId, string osVersion, string model,
        string qimei36, long qimeiRetrievedAt, string uid, string sid, long sessionRetrievedAt)
    {
        InstallationId = installationId; DeviceGeneration = deviceGeneration; SessionGeneration = sessionGeneration;
        OpenUdid = openUdid; OpenUdid2 = openUdid2; AndroidId = androidId; OsVersion = osVersion; Model = model;
        Qimei36 = qimei36; QimeiRetrievedAt = qimeiRetrievedAt; Uid = uid; Sid = sid; SessionRetrievedAt = sessionRetrievedAt;
    }
    // Synthetic Android-compatible installation profile, NOT Windows hardware.
    // QIMEI/uid/sid originate only in validated upstream responses, never entropy.
    [JsonIgnore] public string InstallationId { get; }
    [JsonIgnore] public long DeviceGeneration { get; }
    [JsonIgnore] public long SessionGeneration { get; }
    [JsonIgnore] public string OpenUdid { get; }
    [JsonIgnore] public string OpenUdid2 { get; }
    [JsonIgnore] public string AndroidId { get; }
    [JsonIgnore] public string OsVersion { get; }
    [JsonIgnore] public string Model { get; }
    [JsonIgnore] public string Qimei36 { get; }
    [JsonIgnore] public long QimeiRetrievedAt { get; }
    [JsonIgnore] public string Uid { get; }
    [JsonIgnore] public string Sid { get; }
    [JsonIgnore] public long SessionRetrievedAt { get; }
    public override string ToString() => "MusicQqFeedbackDeviceSnapshot (redacted)";
}
