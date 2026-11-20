using System.Diagnostics;
using System.Text;
using MeowFilm.Platform.Storage;
using Microsoft.Data.Sqlite;

namespace MeowFilm.Windows.Tests;

public static class StorageTests
{
    private static TestCase Case(string name, Func<Task> action) => new("storage.sqlite." + name, action);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Current database invariant"); }
    private static async Task Error(DocumentStoreError expected, Func<Task> action)
    {
        try { await action(); }
        catch (DocumentStoreException error) when (error.Error == expected) { return; }
        throw new InvalidOperationException("Expected database failure " + expected);
    }
    private static string Root() => TestWorkspace.DataPath("storage-sqlite", Guid.NewGuid().ToString("N"));
    private static async Task RejectOpen(string root, DocumentStoreError error)
    {
        var database = new NativeDatabase(root);
        try { await Error(error, () => database.Ready); }
        finally { await database.DisposeAsync(); }
    }
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("current-identity-engine-WAL-FULL-FK-and-current-reopen", async () =>
        {
            string root = Root();
            await using (var database = new NativeDatabase(root))
            {
                await database.Ready;
                Check(database.SQLiteVersion.Length > 0 && database.SQLiteSourceId.Length > 0);
                await database.ReadAsync(db =>
                {
                    Check(db.Scalar<string>("PRAGMA journal_mode") == "wal" && db.Scalar<long>("PRAGMA synchronous") == 2 &&
                        db.Scalar<long>("PRAGMA foreign_keys") == 1 && db.Scalar<long>("PRAGMA wal_autocheckpoint") == 0 &&
                        db.Scalar<long>("PRAGMA application_id") == NativeDatabase.ApplicationId &&
                        db.Scalar<long>("PRAGMA user_version") == NativeDatabase.FormatVersion);
                    return true;
                });
                await database.WriteAsync(db => db.CommitRevision("test", StorageRevision.Empty, "receipt"));
            }
            await using (var reopened = new NativeDatabase(root)) Check((await reopened.RevisionAsync("test")).Revision == 1);
            Directory.Delete(root, true);
        });
        yield return Case("typed-values-and-rollback-never-leave-partial-data", async () =>
        {
            await using var database = new NativeDatabase(Root());
            await database.WriteAsync(db => db.CommitRevision("test", StorageRevision.Empty, "first"));
            await Error(DocumentStoreError.Conflict, () => database.WriteAsync<bool>(db =>
            {
                db.Execute("UPDATE owner_revisions SET receipt='uncommitted' WHERE owner='test'");
                throw new DocumentStoreException(DocumentStoreError.Conflict);
            }));
            Check((await database.RevisionAsync("test")) == new StorageRevision(1, "first"));
            await database.ReadAsync(db =>
            {
                Check(db.Scalar<string>("SELECT $value", ("$value", "a\0b")) == "a\0b");
                Check(db.Scalar<long>("SELECT length($value)", ("$value", Array.Empty<byte>())) == 0);
                return true;
            });
        });
        yield return Case("ordinary-reopen-scopes-stored-relations-and-keeps-write-and-backup-integrity", async () =>
        {
            string root = Root();
            await using (var database = new NativeDatabase(root))
                await database.Ready;
            using (var inspection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, NativeDatabase.RelativePath), Mode = SqliteOpenMode.ReadWrite,
                Pooling = false, ForeignKeys = false
            }.ToString()))
            {
                inspection.Open();
                using var command = inspection.CreateCommand();
                command.CommandText = "INSERT INTO operation_receipts VALUES('missing-owner','retained',1)";
                command.ExecuteNonQuery();
            }
            await using var reopened = new NativeDatabase(root);
            await reopened.Ready;
            Check(await reopened.ReadAsync(db => db.Scalar<long>("PRAGMA foreign_keys")) == 1);
            await reopened.WriteAsync(db => db.CommitRevision("unrelated", StorageRevision.Empty, "saved"));
            Check(await reopened.RevisionAsync("unrelated") == new StorageRevision(1, "saved"));
            await Error(DocumentStoreError.Conflict, () => reopened.WriteAsync(db =>
                db.Execute("INSERT INTO operation_receipts VALUES('missing-owner','rejected',1)")));
            Check(await reopened.ReadAsync(db => db.Scalar<long>("SELECT count(*) FROM operation_receipts WHERE owner='missing-owner'")) == 1);
            Check(await reopened.ReadAsync(db => db.Scalar<string>("SELECT operation_id FROM operation_receipts WHERE owner='missing-owner'")) == "retained");
            string destination = Path.Combine(Root(), "storage-v1", "meowfilm.sqlite3");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await Error(DocumentStoreError.Corrupt, () => reopened.BackupAsync(destination));
            Check(!File.Exists(destination));
        });
        yield return Case("independent-connections-serialize-current-row-increments", async () =>
        {
            string root = Root();
            await using var first = new NativeDatabase(root);
            await using var second = new NativeDatabase(root);
            await first.WriteAsync(db => db.CommitRevision("test", StorageRevision.Empty, "first"));
            async Task Write(NativeDatabase database)
            {
                for (int i = 0; i < 40; i++) await database.WriteAsync(db => db.CommitRevision("test", db.Revision("test"), Guid.NewGuid().ToString("D")));
            }
            await Task.WhenAll(Write(first), Write(second));
            Check((await first.RevisionAsync("test")).Revision == 81);
        });
        yield return Case("native-and-attached-node-write-the-same-WAL", async () =>
        {
            string root = Root();
            await using var database = new NativeDatabase(root);
            await database.WriteAsync(db => db.CommitRevision("shared-test", StorageRevision.Empty, "initial"));
            var start = new ProcessStartInfo(TestWorkspace.NodePath)
            {
                WorkingDirectory = TestWorkspace.SourceRoot, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--input-type=module");
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("""
                import { openDatabase } from '@meowfilm/storage';
                import { createDesktopStorageDriver } from '@meowfilm/script-host/storage';
                const db = openDatabase(process.argv[1], { driver: createDesktopStorageDriver, initialize: false });
                try {
                    await db.ready;
                    console.log('ready');
                    for await (const _ of process.stdin) break;
                    for (let i=0; i<40; ++i) await db.execute(
                        "UPDATE owner_revisions SET revision=revision+1,receipt=? WHERE owner='shared-test'", ['node-'+i], {expectChanges:1});
                } finally { await db.close(); }
                """);
            start.ArgumentList.Add(root);
            using var child = Process.Start(start)!;
            var errors = child.StandardError.ReadToEndAsync();
            try
            {
                var hello = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                if (hello != "ready") throw new InvalidOperationException("Attached Node open failed: " + await errors);
                await child.StandardInput.WriteLineAsync("start");
                child.StandardInput.Close();
                for (int i = 0; i < 40; i++)
                    await database.WriteAsync(db => db.CommitRevision("shared-test", db.Revision("shared-test"), "native-" + i));
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                if (child.ExitCode != 0) throw new InvalidOperationException("Attached Node write failed: " + await errors);
                Check((await database.RevisionAsync("shared-test")).Revision == 81);
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        });
        yield return Case("stale-current-revision-rejects-without-retry", async () =>
        {
            await using var database = new NativeDatabase(Root());
            var first = await database.WriteAsync(db => db.CommitRevision("test", StorageRevision.Empty, "first"));
            var next = await database.WriteAsync(db => db.CommitRevision("test", first, "next"));
            await Error(DocumentStoreError.Conflict, () => database.WriteAsync(db => db.CommitRevision("test", first, "stale")));
            Check(await database.RevisionAsync("test") == next);
        });
        yield return Case("accepted-FIFO-writes-survive-cancellation-and-close-drains", async () =>
        {
            var database = new NativeDatabase(Root());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var caller = new CancellationTokenSource();
            try
            {
                var first = database.WriteAsync(db => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return db.CommitRevision("test", StorageRevision.Empty, "first"); });
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var second = database.WriteAsync(db => db.CommitRevision("test", db.Revision("test"), "second"), caller.Token);
                caller.Cancel();
                var close = database.DisposeAsync().AsTask();
                Check(!first.IsCompleted && !second.IsCompleted && !close.IsCompleted);
                await Error(DocumentStoreError.Closed, () => database.ReadAsync(_ => true));
                release.TrySetResult(); await Task.WhenAll(first, second, close);
                await using var reopened = new NativeDatabase(database.ProductRoot!);
                Check(await reopened.RevisionAsync("test") == new StorageRevision(2, "second"));
            }
            finally { release.TrySetResult(); await database.DisposeAsync(); }
        });
        yield return Case("pre-cancelled-admission-does-not-execute-and-a-large-solitary-write-is-accepted", async () =>
        {
            await using var database = new NativeDatabase(Root());
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            int executions = 0;
            try { await database.WriteAsync(_ => ++executions, cancelled.Token); throw new InvalidOperationException("Cancelled admission"); }
            catch (OperationCanceledException) { }
            Check(executions == 0);
            await database.WriteAsync(_ => ++executions, retainedBytes: 32 * 1024 * 1024);
            Check(executions == 1);
        });
        yield return Case("repository-close-preserves-accepted-update-and-is-idempotent-after-database-close", async () =>
        {
            var database = new NativeDatabase(Root());
            var repository = new TypedRecordRepository(database, "appearance-close-test", MeowFilm.AppHost.AppStorageRecords.Appearance);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = database.WriteAsync(_ => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return true; });
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var update = repository.UpdateAsync(_ => System.Text.Json.JsonSerializer.SerializeToElement(new { schema = 1, themeColor = "#315A7D" }));
                var closing = repository.DisposeAsync().AsTask();
                Check(!update.IsCompleted && !closing.IsCompleted);
                try { await repository.ReadAsync(); throw new InvalidOperationException("Retired repository admitted a read"); }
                catch (ObjectDisposedException) { }
                release.TrySetResult();
                await Task.WhenAll(blocker, update, closing);
                await database.DisposeAsync();
                await repository.DisposeAsync();
                await using var reopened = new NativeDatabase(database.ProductRoot!);
                Check(await reopened.ReadAsync(db => db.Scalar<string>("SELECT themeColor FROM windows_app_appearance")) == "#315A7D");
            }
            finally { release.TrySetResult(); await blocker; await repository.DisposeAsync(); await database.DisposeAsync(); }
        });
        yield return Case("queue-and-retained-input-budgets-are-bounded", async () =>
        {
            await using var database = new NativeDatabase(Root());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accepted = new List<Task>();
            try
            {
                accepted.Add(database.WriteAsync(_ => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return true; }));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                for (int i = 0; i < 128; i++) accepted.Add(database.WriteAsync(_ => true));
                await Error(DocumentStoreError.Limit, () => database.WriteAsync(_ => true));
            }
            finally { release.TrySetResult(); await Task.WhenAll(accepted); }
        });
        yield return Case("actual-readonly-failure-is-explicit-and-recovery-is-not-replay", async () =>
        {
            await using var database = new NativeDatabase(Root());
            await database.ReadAsync(db => db.Execute("PRAGMA query_only=ON"));
            await Error(DocumentStoreError.ReadOnly, () => database.WriteAsync(db => db.CommitRevision("test", StorageRevision.Empty, "not-saved")));
            Check(await database.RevisionAsync("test") == StorageRevision.Empty);
            await database.ReadAsync(db => db.Execute("PRAGMA query_only=OFF"));
            Check((await database.WriteAsync(db => db.CommitRevision("test", StorageRevision.Empty, "explicit"))).Revision == 1);
        });
        yield return Case("current-schema-drift-and-corruption-never-rebuild-or-delete", async () =>
        {
            string root = Root(), path = Path.Combine(root, NativeDatabase.RelativePath);
            await using (var database = new NativeDatabase(root)) await database.WriteAsync(db => db.Execute("ALTER TABLE owner_revisions ADD COLUMN unknown TEXT"));
            byte[] original = await File.ReadAllBytesAsync(path);
            await RejectOpen(root, DocumentStoreError.UnsupportedVersion);
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(original));
            string corruptRoot = Root(), corruptPath = Path.Combine(corruptRoot, NativeDatabase.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
            byte[] corrupt = Encoding.UTF8.GetBytes("synthetic corrupt current database"); await File.WriteAllBytesAsync(corruptPath, corrupt);
            await RejectOpen(corruptRoot, DocumentStoreError.Corrupt);
            Check((await File.ReadAllBytesAsync(corruptPath)).SequenceEqual(corrupt));
        });
        yield return Case("existing-empty-file-and-orphaned-journals-are-preserved", async () =>
        {
            foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                string root = Root(), path = Path.Combine(root, NativeDatabase.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                byte[] bytes = suffix.Length == 0 ? [] : Encoding.UTF8.GetBytes("synthetic journal");
                await File.WriteAllBytesAsync(path + suffix, bytes);
                await RejectOpen(root, DocumentStoreError.Corrupt);
                Check((await File.ReadAllBytesAsync(path + suffix)).SequenceEqual(bytes));
                Check(suffix.Length == 0 || !File.Exists(path));
            }
        });
        yield return Case("backup-is-a-verified-current-snapshot-not-a-second-authority", async () =>
        {
            string backupRoot = Root(), directory = Path.Combine(backupRoot, "storage-v1"); Directory.CreateDirectory(directory);
            await using var database = new NativeDatabase(Root());
            await database.WriteAsync(db => db.CommitRevision("test", StorageRevision.Empty, "snapshot"));
            await database.BackupAsync(Path.Combine(directory, "meowfilm.sqlite3"));
            await database.WriteAsync(db => db.CommitRevision("test", db.Revision("test"), "later"));
            await using var backup = new NativeDatabase(backupRoot);
            Check(await backup.RevisionAsync("test") == new StorageRevision(1, "snapshot"));
        });
    }
}
