// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowFilm.Capabilities.Accounts;

/// <summary>Explicit user-file capability. Ordinary DTO serialization never exposes the file bytes.</summary>
[DebuggerDisplay("MusicAccountTransferFile (redacted)")]
public sealed class MusicAccountTransferFile : IDisposable
{
    public const int MaximumBytes = 1024 * 1024;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)] private byte[]? bytes;
    internal MusicAccountTransferFile(byte[] bytes) { this.bytes = bytes; }
    public static async Task<MusicAccountTransferFile> ReadAsync(Stream source, CancellationToken token = default)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16384];
        try
        {
            while (true)
            {
                int count = await source.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length,
                    MaximumBytes + 1 - (int)buffer.Length)), token).ConfigureAwait(false);
                if (count == 0) break;
                buffer.Write(chunk, 0, count);
                AccountRules.Require(buffer.Length <= MaximumBytes, "LIMIT");
            }
            return new(buffer.ToArray());
        }
        catch (MusicAccountException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch { throw new MusicAccountException("TRANSFER_FILE"); }
        finally
        {
            CryptographicOperations.ZeroMemory(chunk);
            CryptographicOperations.ZeroMemory(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }
    }
    public async Task WriteAsync(Stream destination, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(bytes is null, this);
        try
        {
            await destination.WriteAsync(bytes, token).ConfigureAwait(false);
            await destination.FlushAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new MusicAccountException("TRANSFER_FILE"); }
    }
    internal List<TransferEntry> Decode()
    {
        ObjectDisposedException.ThrowIf(bytes is null, this);
        AccountRules.Require(bytes.Length <= MaximumBytes, "LIMIT");
        try
        {
            // UTF-8 BOM is a file encoding marker, not part of the JSON root.
            int offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            using var document = JsonDocument.Parse(bytes.AsMemory(offset));
            var root = document.RootElement;
            AccountRules.Fields(root, ["format", "version", "accounts"], ["format", "version", "accounts"], "TRANSFER_FORMAT");
            AccountRules.Require(root.GetProperty("format").ValueKind == JsonValueKind.String &&
                root.GetProperty("format").GetString() == "meowfilm.music-accounts" &&
                root.GetProperty("version").ValueKind == JsonValueKind.Number &&
                root.GetProperty("version").TryGetDouble(out var version) && version == 1, "TRANSFER_FORMAT");
            var rows = root.GetProperty("accounts");
            AccountRules.Require(rows.ValueKind == JsonValueKind.Array, "TRANSFER_FORMAT");
            var platforms = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<TransferEntry>();
            foreach (var row in rows.EnumerateArray())
            {
                AccountRules.Fields(row, ["platform", "userId", "credentials"], ["platform", "userId", "credentials"], "TRANSFER_FORMAT");
                AccountRules.Require(row.GetProperty("platform").ValueKind == JsonValueKind.String &&
                    row.GetProperty("userId").ValueKind == JsonValueKind.String, "TRANSFER_FORMAT");
                string platform = row.GetProperty("platform").GetString()!;
                AccountRules.Require(AccountRules.Platforms.Contains(platform) && platforms.Add(platform), "TRANSFER_FORMAT");
                var credentials = row.GetProperty("credentials");
                AccountRules.Require(credentials.ValueKind == JsonValueKind.Object, "TRANSFER_FORMAT");
                var parts = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var field in credentials.EnumerateObject())
                    AccountRules.Require(field.Value.ValueKind == JsonValueKind.String &&
                        parts.TryAdd(field.Name, field.Value.GetString()!), "TRANSFER_FORMAT");
                result.Add(new(platform, row.GetProperty("userId").GetString()!, parts));
            }
            return result.OrderBy(row => Array.IndexOf(AccountRules.Platforms, row.Platform)).ToList();
        }
        catch (MusicAccountException) { throw; }
        catch { throw new MusicAccountException("TRANSFER_FORMAT"); }
    }
    internal static MusicAccountTransferFile Encode(IReadOnlyList<TransferEntry> entries)
    {
        using var buffer = new MemoryStream();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject(); writer.WriteString("format", "meowfilm.music-accounts"); writer.WriteNumber("version", 1);
                writer.WriteStartArray("accounts");
                foreach (var entry in entries)
                {
                    writer.WriteStartObject(); writer.WriteString("platform", entry.Platform); writer.WriteString("userId", entry.UserId);
                    writer.WriteStartObject("credentials");
                    foreach (var part in entry.Fields) writer.WriteString(part.Key, part.Value);
                    writer.WriteEndObject(); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            AccountRules.Require(buffer.Length <= MaximumBytes, "LIMIT");
            return new(buffer.ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)); }
    }
    public void Dispose()
    {
        if (bytes is { } value) CryptographicOperations.ZeroMemory(value);
        bytes = null;
    }
    public override string ToString() => "MusicAccountTransferFile (redacted)";
}

[DebuggerDisplay("TransferEntry (redacted)")]
internal sealed class TransferEntry(string platform, string userId, Dictionary<string, string> fields)
{
    internal string Platform { get; } = platform;
    internal string UserId { get; } = userId;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)] internal Dictionary<string, string> Fields { get; } = fields;
    public override string ToString() => "TransferEntry (redacted)";
}

public enum MusicAccountTransferPolicy { Skip, Overwrite, Cancel }
public sealed record MusicAccountTransferConflict(string Platform);
public sealed record MusicAccountTransferItem(string Platform, string Status, string? Code = null);
public sealed record MusicAccountTransferSummary(IReadOnlyList<MusicAccountTransferItem> Items);

/// <summary>One owner, one preparation baseline, one policy choice. Holds no public credentials.</summary>
[DebuggerDisplay("MusicAccountTransferPlan (redacted)")]
public sealed class MusicAccountTransferPlan : IDisposable
{
    [DebuggerBrowsable(DebuggerBrowsableState.Never)] internal List<TransferEntry> Entries { get; }
    [DebuggerBrowsable(DebuggerBrowsableState.Never)] internal Dictionary<string, (StoredAccount Account, long Epoch)> Baselines { get; }
    internal MusicAccountOwner Owner { get; }
    internal bool Consumed;
    [JsonIgnore] public IReadOnlyList<MusicAccountTransferConflict> Conflicts { get; }
    internal MusicAccountTransferPlan(MusicAccountOwner owner, List<TransferEntry> entries,
        Dictionary<string, (StoredAccount, long)> baselines, IReadOnlyList<MusicAccountTransferConflict> conflicts)
    { Owner = owner; Entries = entries; Baselines = baselines; Conflicts = conflicts; }
    public void Dispose()
    {
        lock (this)
        {
            Consumed = true;
            foreach (var entry in Entries) entry.Fields.Clear();
            Entries.Clear(); Baselines.Clear();
        }
    }
    public override string ToString() => "MusicAccountTransferPlan (redacted)";
}

/// <summary>Trusted in-process file transfer, deliberately separate from JSON commands.</summary>
public interface IMusicAccountTransferSource
{
    Task<MusicAccountTransferPlan> PrepareAccountTransferAsync(MusicAccountTransferFile file, CancellationToken token = default);
    Task<MusicAccountTransferSummary> ImportAccountTransferAsync(MusicAccountTransferPlan plan, MusicAccountTransferPolicy policy,
        CancellationToken token = default);
    Task<MusicAccountTransferFile?> ExportAccountTransferAsync(CancellationToken token = default);
}

internal interface IMusicAccountTransferProtocol
{
    Task<MusicAccountDetailsObservation> ValidateTransferAsync(string platform, string cookie, CancellationToken token);
}
