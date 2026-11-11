using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities;

public sealed partial class MusicNativeApplication : IMusicDailyStylePreferences
{
    public Task<MusicDailyStyleEditor> OpenDailyStyleEditorAsync(CancellationToken cancellationToken = default) =>
        DailyStyleOperation(token => nativeCatalog.OpenDailyStyleEditorAsync(token), cancellationToken);

    public Task<MusicDailyStyleSaveResult> SaveDailyStyleAsync(MusicDailyStyleEditor editor,
        MusicDailyStyleSelection selection, CancellationToken cancellationToken = default) =>
        DailyStyleOperation(token => nativeCatalog.SaveDailyStyleAsync(editor, selection, token), cancellationToken);

    private async Task<T> DailyStyleOperation<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        lock (gate)
        {
            if (stopping) throw new MusicPlatformException("CLOSED");
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            using var frame = sessions.Begin();
            return await action(request.Token).ConfigureAwait(false);
        }
        finally { lock (gate) { if (--pending == 0) drained.TrySetResult(); } }
    }
}
