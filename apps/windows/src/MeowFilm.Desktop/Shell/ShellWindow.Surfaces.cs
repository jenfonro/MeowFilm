using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Microsoft.UI.Xaml.Media.Animation;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    private readonly List<(IDesktopGlobalSurfaceSource Source,
        EventHandler<DesktopGlobalSurfaceRequestEventArgs> Handler)> _globalSurfaceSubscriptions = [];
    private CancellationTokenSource? _playerDetailActivation;
    private CancellationTokenSource? _queueActivation;
    private IDesktopGlobalSurfaceSource? _playerDetailSource;
    private IDesktopGlobalSurfaceSource? _queueSource;
    private ModuleSession? _playerDetailSession;
    private ModuleSession? _queueSession;
    private long _playerDetailGeneration;
    private long _queueGeneration;
    private bool _playerDetailOpen;
    private bool _queueOpen;

    private void SubscribeGlobalSurfaces(ModuleSession session)
    {
        if (session.Module is not IDesktopGlobalSurfaceSource source) return;
        EventHandler<DesktopGlobalSurfaceRequestEventArgs> handler = (_, args) =>
        {
            if (args is null) return;
            QueueUi(() =>
            {
                if (!CanPresentPlayerSurface(session)) return;
                RunUiAction(() => ToggleGlobalSurfaceAsync(session, source, args.Surface));
            });
        };
        source.GlobalSurfaceRequested += handler;
        _globalSurfaceSubscriptions.Add((source, handler));
    }

    private Task ToggleGlobalSurfaceAsync(ModuleSession session, IDesktopGlobalSurfaceSource source,
        DesktopGlobalSurface surface) => surface switch
    {
        DesktopGlobalSurface.PlayerDetail => _playerDetailOpen
            ? ClosePlayerDetailAsync()
            : OpenPlayerDetailAsync(session, source),
        DesktopGlobalSurface.PlayQueue => _queueOpen
            ? CloseQueueAsync()
            : OpenQueueAsync(session, source),
        _ => Task.CompletedTask
    };

    internal async Task OpenPlayerDetailAsync(ModuleSession session, IDesktopGlobalSurfaceSource source)
    {
        if (!CanPresentPlayerSurface(session)) return;
        var view = source.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail)
            ?? throw new InvalidOperationException("GLOBAL_SURFACE_UNAVAILABLE");
        if (!ReferenceEquals(PlayerDetailHost.Content, view))
        {
            PlayerDetailHost.Content = null;
            ValidateView(view, PlayerDetailHost);
            PlayerDetailHost.Content = view;
        }
        RetireActivation(ref _playerDetailActivation);
        var activation = new CancellationTokenSource();
        _playerDetailActivation = activation;
        _playerDetailSource = source;
        _playerDetailSession = session;
        _playerDetailOpen = true;
        var generation = ++_playerDetailGeneration;
        PlayerDetailSurface.Visibility = Visibility.Visible;
        ApplyPresentation();
        PlayerDetailSurface.UpdateLayout();
        AnimatePlayerDetail(open: true, generation);
        QueueUi(() =>
        {
            if (generation == _playerDetailGeneration && _playerDetailOpen)
                DesktopVisual.FocusFirst(PlayerDetailHost);
        });
        try
        {
            await source.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, activation.Token);
            activation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { return; }
        catch
        {
            if (generation == _playerDetailGeneration && _playerDetailOpen)
                SetStatus("播放器暂时无法加载完整内容", error: true);
        }
    }

    internal Task ClosePlayerDetailAsync()
    {
        if (!_playerDetailOpen && PlayerDetailSurface.Visibility != Visibility.Visible) return Task.CompletedTask;
        _playerDetailOpen = false;
        ++_playerDetailGeneration;
        RetireActivation(ref _playerDetailActivation);
        _playerDetailSource?.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
        ApplyPresentation();
        AnimatePlayerDetail(open: false, _playerDetailGeneration);
        return Task.CompletedTask;
    }

    internal async Task OpenQueueAsync(ModuleSession session, IDesktopGlobalSurfaceSource source)
    {
        if (!CanPresentPlayerSurface(session)) return;
        var view = source.GetGlobalSurface(DesktopGlobalSurface.PlayQueue)
            ?? throw new InvalidOperationException("GLOBAL_SURFACE_UNAVAILABLE");
        if (!ReferenceEquals(QueueHost.Content, view))
        {
            QueueHost.Content = null;
            ValidateView(view, QueueHost);
            QueueHost.Content = view;
        }
        RetireActivation(ref _queueActivation);
        var activation = new CancellationTokenSource();
        _queueActivation = activation;
        _queueSource = source;
        _queueSession = session;
        _queueOpen = true;
        PlayerMenuInput.For(Root).QueueOpened(() => RunUiAction(CloseQueueAsync));
        PlayerSurfaceInput.SetQueueOpen(Root, true);
        var generation = ++_queueGeneration;
        QueueDismissSurface.Visibility = Visibility.Visible;
        QueueSurface.Visibility = Visibility.Visible;
        ApplyPresentation();
        AnimateQueue(open: true, generation);
        QueueUi(() => DesktopVisual.FocusFirst(QueueHost));
        try
        {
            await source.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, activation.Token);
            activation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (generation == _queueGeneration && _queueOpen)
                SetStatus("播放列表暂时无法加载", error: true);
        }
    }

    internal Task CloseQueueAsync()
    {
        if (!_queueOpen && QueueSurface.Visibility != Visibility.Visible) return Task.CompletedTask;
        _queueOpen = false;
        PlayerMenuInput.For(Root).QueueClosed();
        PlayerSurfaceInput.SetQueueOpen(Root, false);
        ++_queueGeneration;
        RetireActivation(ref _queueActivation);
        _queueSource?.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        QueueDismissSurface.Visibility = Visibility.Collapsed;
        ApplyPresentation();
        AnimateQueue(open: false, _queueGeneration);
        return Task.CompletedTask;
    }

    private void ApplyGlobalSurfacePresentation(ModuleSession current)
    {
        if (_playerDetailOpen && (_playerDetailSession is not { } detail ||
            !CanPresentPlayerSurface(detail)))
            _ = ClosePlayerDetailAsync();
        if (_queueOpen && (_queueSession is not { } queue ||
            !CanPresentPlayerSurface(queue)))
            _ = CloseQueueAsync();
    }

    internal bool CanPresentPlayerSurface(ModuleSession session) =>
        !_isClosing &&
        _current is not null &&
        (_currentPage is not null || _current.IsSettingsOpen || _current.Module is not IDesktopImmersiveSource { IsImmersive: true }) &&
        ReferenceEquals(_playerSession, session) &&
        session.Module.PlayerView is not null;

    private void AnimatePlayerDetail(bool open, long generation)
    {
        var presented = PlayerDetailTransform.Y;
        _motion.StopDetail();
        var height = Math.Max(1, PresentationSurface.ActualHeight);
        var from = Math.Clamp(presented, 0, height);
        if (open && from < 1) from = height;
        var to = open ? 0 : height;
        PlayerDetailTransform.Y = from;
        _nativeCaption?.Refresh();
        if (ShellSurfaceMotion.ReducedMotion || !IsVisible || PlayerDetailSurface.Visibility != Visibility.Visible || Math.Abs(to - from) < .5)
        {
            Complete();
            return;
        }
        var duration = TimeSpan.FromMilliseconds((open ? 520d : 360d) * Math.Abs(to - from) / height);
        _motion.StartDetail(PlayerDetailTransform, from, to, duration, Complete);

        void Complete()
        {
            if (generation != _playerDetailGeneration) return;
            _motion.StopDetail();
            PlayerDetailTransform.Y = to;
            if (!open)
            {
                PlayerDetailSurface.Visibility = Visibility.Collapsed;
                _playerDetailSource = null;
                _playerDetailSession = null;
            }
            else
            {
                DesktopViewport.NotifyTransformSettled(PlayerDetailSurface);
            }
            _nativeCaption?.RefreshImmediate();
        }
    }

    private void AnimateQueue(bool open, long generation)
    {
        var presented = QueueTransform.X;
        _motion.StopQueue();
        var width = Math.Max(1, QueueSurface.ActualWidth > 0 ? QueueSurface.ActualWidth : QueueSurface.Width);
        var from = Math.Clamp(presented, 0, width + 24);
        if (open && from < 1) from = width + 24;
        var to = open ? 0 : width + 24;
        QueueTransform.X = from;
        if (ShellSurfaceMotion.ReducedMotion || !IsVisible)
        {
            Complete();
            return;
        }
        var duration = TimeSpan.FromMilliseconds(open ? 180 : 120);
        _motion.StartQueue(QueueTransform, from, to, duration, Complete);

        void Complete()
        {
            if (generation != _queueGeneration) return;
            _motion.StopQueue();
            QueueTransform.X = to;
            if (!open)
            {
                QueueSurface.Visibility = Visibility.Collapsed;
                _queueSource = null;
                _queueSession = null;
            }
            else
            {
                DesktopViewport.NotifyTransformSettled(QueueSurface);
            }
        }
    }

    private void OnQueueDismissClick(object sender, RoutedEventArgs e)
    {
        RunUiAction(CloseQueueAsync);
    }

    private static void RetireActivation(ref CancellationTokenSource? source)
    {
        var prior = source;
        source = null;
        if (prior is null) return;
        _ = RetireAsync(prior);

        static async Task RetireAsync(CancellationTokenSource value)
        {
            try { await ShellRequest.ObserveAsync(value.CancelAsync()); }
            finally { value.Dispose(); }
        }
    }

    private void DisposeGlobalSurfaces()
    {
        _playerDetailOpen = false;
        _queueOpen = false;
        PlayerMenuInput.For(Root).QueueClosed();
        PlayerSurfaceInput.SetQueueOpen(Root, false);
        RetireActivation(ref _playerDetailActivation);
        RetireActivation(ref _queueActivation);
        _playerDetailSource?.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
        _queueSource?.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        foreach (var (source, handler) in _globalSurfaceSubscriptions)
            source.GlobalSurfaceRequested -= handler;
        _globalSurfaceSubscriptions.Clear();
        PlayerDetailHost.Content = null;
        QueueHost.Content = null;
    }
}
