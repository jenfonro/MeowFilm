using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using MeowFilm.Capabilities.Library;
using MeowFilm.Desktop;
using MeowFilm.Platform.Files;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

/// <summary>
/// Shared internal runtime seam for standalone music domain controllers.
/// Controllers own their domain state, bindable properties, and commands directly;
/// views bind only to controllers and never reference <see cref="MusicCapability"/>.
/// </summary>
public abstract class MusicDomainController : MusicStateObject
{
    private protected readonly MusicCapability host;

    internal MusicDomainController(MusicCapability host) => this.host = host;

    private protected string Id => host.Id;
    private protected IMusicApplication application => host.Application;
    private protected IMusicPlayback playback => host.PlaybackDevice;
    private protected IMusicDialogs dialogs => host.Dialogs;
    private protected GrantedMediaFiles files => host.Files;
    private protected IStorageDatabase database => host.Database;
    private protected PlaybackCache.MusicAudioFileCache? audioFileCache => host.AudioFileCache;
    private protected PlaybackCache.MusicChorusCache persistentChorus => host.PersistentChorus;
    private protected PlaybackCache.MusicPresentationCache presentationCache => host.PresentationCache;
    private protected CancellationTokenSource lifetime => host.Lifetime;
    private protected MusicPresentation Presentation => host.Presentation;
    private protected MusicSettingReceiptTracker Receipts => host.Receipts;
    private protected HashSet<Task> loadDrains => host.LoadDrains;
    private protected HashSet<Task> eventWrites => host.EventWrites;
    private protected bool stopping => host.Stopping;
    private protected bool disposed => host.Disposed;
    private protected int commands => host.Commands;
    private protected bool Busy => host.Busy;
    public bool CanEdit => host.CanEdit;
    internal MeowFilm.Desktop.Artwork.ArtworkService ArtworkLoader => host.ArtworkLoader;
    private protected FrameworkElement View => host.View;
    private protected FrameworkElement SettingsView => host.SettingsView;
    private protected FrameworkElement PlayerView => host.PlayerView;

    private protected Task<JsonElement> Call(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
        host.Call(method, parameters, cancellationToken);
    private protected Task<JsonElement> CallSettled(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
        host.CallSettled(method, parameters, cancellationToken);
    private protected Task<JsonElement> CallSettledReceipt(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
        host.CallSettledReceipt(method, parameters, cancellationToken);
    private protected Task RunAsync(Func<Task> action, bool publishStatus = true) =>
        host.RunAsync(action, publishStatus);
    private protected Task RunPlaybackActionAsync(Func<long, Task> action, bool publishStatus = true, bool supersede = true) =>
        host.RunPlaybackActionAsync(action, publishStatus, supersede);
    private protected Task<bool> ConfirmAsync(string message, string title, CancellationToken token = default) =>
        host.ConfirmAsync(message, title, token);
    internal Task Notify(string message) => host.Notify(message);
    private protected Task RequestNavigationAsync(string destination) => host.RequestNavigationAsync(destination);
    private protected static string ErrorText(Exception error) => MusicCapability.ErrorText(error);
    private protected static JsonElement ValidateReply(string method, JsonElement reply) => MusicCapability.ValidateReply(method, reply);

    private protected void Changed([CallerMemberName] string? propertyName = null) => RaisePropertyChanged(propertyName);
    internal void NotifyProperty(string propertyName) => RaisePropertyChanged(propertyName);
    internal virtual void RefreshAvailability() => RaisePropertyChanged(nameof(CanEdit));
}
