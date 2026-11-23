using System.Collections.ObjectModel;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Feedback;

internal static class MusicFeedbackSettingsStore
{
    internal const string Owner = MusicFeedbackSettingsOwner.StorageOwner;
    private const long MaximumRevision = 9007199254740991;

    internal static Task<(MusicFeedbackSettings Settings, StorageRevision StorageRevision)> ReadAsync(
        IStorageDatabase database, CancellationToken token) =>
        database.ReadSnapshotAsync(db =>
        {
            var storageRevision = db.Revision(Owner);
            var enabled = db.Query("""
                SELECT platform
                FROM windows_music_feedback_platforms
                ORDER BY platform
                """,
                row => row.GetString(0)).ToArray();

            if (storageRevision == StorageRevision.Empty)
            {
                if (enabled.Length > 0)
                    throw new MusicFeedbackException("CORRUPT_STORAGE");
                return (new MusicFeedbackSettings(0, Array.Empty<string>()), storageRevision);
            }

            if (storageRevision.Revision is < 1 or > MaximumRevision ||
                enabled.Length > 3 ||
                enabled.Distinct(StringComparer.Ordinal).Count() != enabled.Length ||
                enabled.Any(v => v is not ("wy" or "tx" or "kg")))
                throw new MusicFeedbackException("CORRUPT_STORAGE");

            return (new MusicFeedbackSettings(storageRevision.Revision, new ReadOnlyCollection<string>(enabled)), storageRevision);
        }, token);

    internal static Task<StorageRevision> SaveAsync(
        IStorageDatabase database,
        MusicFeedbackSettings next,
        StorageRevision expected,
        CancellationToken token) =>
        database.WriteAsync(db =>
        {
            if (db.Revision(Owner) != expected || next.Revision != expected.Revision + 1)
                throw new StorageException(StorageError.Conflict);

            var nextSet = next.Enabled.ToHashSet(StringComparer.Ordinal);
            foreach (string platform in new[] { "wy", "tx", "kg" })
            {
                if (nextSet.Contains(platform))
                    db.Execute("INSERT OR IGNORE INTO windows_music_feedback_platforms(platform) VALUES($plat)", ("$plat", platform));
                else
                    db.Execute("DELETE FROM windows_music_feedback_platforms WHERE platform=$plat", ("$plat", platform));
            }

            return db.CommitRevision(Owner, expected);
        }, token);
}
