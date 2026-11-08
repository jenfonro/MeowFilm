using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Scripting;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Files;

namespace MeowFilm.Windows.Tests;
public static class SelectedScriptSnapshotTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (var test in new SelectedSnapshotCases(script: true).Cases()) yield return test;
        yield return new("selected-script.complete-snapshot-import-does-not-reopen-the-selected-path", ImportSnapshot);
    }
    private static async Task ImportSnapshot()
    {
        string root = Path.Combine(Path.GetTempPath(), "meowfilm-script-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "猫.js");
            byte[] original = Encoding.UTF8.GetBytes("\uFEFF// selected\r\n" + new string('x', 1024 * 1024 + 17));
            await File.WriteAllBytesAsync(path, original);
            var snapshot = await SelectedScriptSnapshot.ReadAsync(path);
            try
            {
                await File.WriteAllTextAsync(path, "// replaced after selection");
                var runtime = new SnapshotRuntime(original, snapshot.Bytes);
                await using var library = new MusicLibraryOwner(new QualityFixture.DocumentStore());
                await using var owner = new MusicScriptOwner(library, runtime);
                var receipt = await owner.ImportSourceAsync(snapshot.Bytes, snapshot.Basename);
                Check(receipt.GetProperty("ok").GetBoolean() && runtime.Uploads == 1 && runtime.Installs == 1);
                Check(snapshot.Bytes.AsSpan().SequenceEqual(original), "the owner borrows the caller's complete snapshot");
                Check(await File.ReadAllTextAsync(path) == "// replaced after selection");
            }
            finally { CryptographicOperations.ZeroMemory(snapshot.Bytes); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static void Check(bool condition, string message = "snapshot assertion")
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class SnapshotRuntime(byte[] original, byte[] snapshot) : IRuntimeClient
    {
        internal int Uploads, Installs;
        private string installation = "";
        private long revision;
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(module == "music" && Guid.TryParseExact(installationId, "D", out _) && sourceRevision > 0);
            Check(basename == "猫.js" && source.Equals((ReadOnlyMemory<byte>)snapshot) && source.Span.SequenceEqual(original));
            installation = installationId; revision = sourceRevision; Uploads++;
            return Task.FromResult(Reply(new { installationId, sourceRevision }));
        }
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = JsonSerializer.SerializeToElement(parameters);
            Check(method == "music.script.sources.add" && request.EnumerateObject().Count() == 2 &&
                request.GetProperty("installationId").GetString() == installation &&
                request.GetProperty("sourceRevision").GetInt64() == revision);
            Installs++;
            return Task.FromResult(Reply(new { item = new { installationId = installation } }));
        }
        private static JsonElement Reply(object value) =>
            JsonSerializer.SerializeToElement(new { ok = true, contract = "music.script.v3", value });
    }
}
