using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities;

public sealed partial class MusicNativeApplication
{
    public Task<MusicDailyStyleEditor> OpenDailyStyleEditorAsync(CancellationToken cancellationToken = default) =>
        DailyStyleOperation(token => nativeCatalog.OpenDailyStyleEditorAsync(token), cancellationToken);

    public Task<MusicDailyStyleSaveResult> SaveDailyStyleAsync(MusicDailyStyleEditor editor,
        MusicDailyStyleSelection selection, CancellationToken cancellationToken = default) =>
        DailyStyleOperation(token => nativeCatalog.SaveDailyStyleAsync(editor, selection, token), cancellationToken);

    private async Task<T> DailyStyleOperation<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        CancellationToken generation;
        lock (gate)
        {
            if (stopping || closed) throw new MusicPlatformException("CLOSED");
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            generation = lifetime.Token;
        }
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token, generation);
            using var frame = sessions.Begin();
            try
            {
                var result = await action(request.Token).ConfigureAwait(false);
                if (result is MusicDailyStyleSaveResult { ErrorCode: "AUTH_EXPIRED" or "AUTH_REQUIRED" } &&
                    frame.Session is { } failed)
                    accounts.Revoke(failed);
                return result;
            }
            catch (MusicPlatformException error) when (error.Code is "AUTH_EXPIRED" or "AUTH_REQUIRED")
            {
                if (frame.Session is { } failed) accounts.Revoke(failed);
                throw;
            }
        }
        finally { lock (gate) { if (--pending == 0) drained.TrySetResult(); } }
    }
}
