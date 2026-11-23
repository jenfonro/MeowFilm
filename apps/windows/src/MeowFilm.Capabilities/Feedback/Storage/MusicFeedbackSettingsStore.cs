using System.Collections.ObjectModel;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Feedback;

internal static class MusicFeedbackSettingsStore
{
    internal const string Owner = MusicFeedbackSettingsOwner.StorageOwner;
    private const string DefaultScope = "local";
    private const long MaximumRevision = 9007199254740991;

    internal static Task<(MusicFeedbackSettings Settings, StorageRevision StorageRevision)> ReadAsync(
        IStorageDatabase database, CancellationToken token) =>
        database.ReadSnapshotAsync(db =>
        {
            var storageRevision = db.Revision(Owner);
            var enabled = db.Query("""
                SELECT platform
                FROM music_feedback_settings
                WHERE scope=$scope AND enabled=1
                ORDER BY position
                """,
                row => row.GetString(0),
                ("$scope", DefaultScope)).ToArray();

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

            Array.Sort(enabled, StringComparer.Ordinal);
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

            db.Execute("DELETE FROM music_feedback_settings WHERE scope=$scope", ("$scope", DefaultScope));

            for (int i = 0; i < next.Enabled.Count; i++)
                db.Execute("""
                    INSERT INTO music_feedback_settings(scope,platform,enabled,generation,position)
                    VALUES($scope,$plat,1,0,$pos)
                    """, ("$scope", DefaultScope), ("$plat", next.Enabled[i]), ("$pos", i));

            return db.CommitRevision(Owner, expected);
        }, token);
}
