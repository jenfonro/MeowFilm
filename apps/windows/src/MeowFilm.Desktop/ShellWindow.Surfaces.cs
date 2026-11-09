using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

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

    private async Task OpenPlayerDetailAsync(ModuleSession session, IDesktopGlobalSurfaceSource source)
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
        UpdatePlayerDetailChrome();
        ApplyPresentation();
        PlayerDetailSurface.UpdateLayout();
        AnimatePlayerDetail(open: true, generation);
        QueueUi(() =>
        {
            if (generation == _playerDetailGeneration && _playerDetailOpen)
                PlayerDetailHost.MoveFocus(new(FocusNavigationDirection.First));
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

    private Task ClosePlayerDetailAsync()
    {
        if (!_playerDetailOpen && PlayerDetailSurface.Visibility != Visibility.Visible) return Task.CompletedTask;
        _playerDetailOpen = false;
        ++_playerDetailGeneration;
        RetireActivation(ref _playerDetailActivation);
        _playerDetailSource?.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
        UpdatePlayerDetailChrome();
        ApplyPresentation();
        AnimatePlayerDetail(open: false, _playerDetailGeneration);
        return Task.CompletedTask;
    }

    private async Task OpenQueueAsync(ModuleSession session, IDesktopGlobalSurfaceSource source)
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
        PlayerSurfaceInput.SetQueueOpen(this, true);
        var generation = ++_queueGeneration;
        QueueDismissSurface.Visibility = Visibility.Visible;
        QueueSurface.Visibility = Visibility.Visible;
        AnimateQueue(open: true, generation);
        QueueUi(() => QueueHost.MoveFocus(new(FocusNavigationDirection.First)));
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

    private Task CloseQueueAsync()
    {
        if (!_queueOpen && QueueSurface.Visibility != Visibility.Visible) return Task.CompletedTask;
        _queueOpen = false;
        PlayerSurfaceInput.SetQueueOpen(this, false);
        ++_queueGeneration;
        RetireActivation(ref _queueActivation);
        _queueSource?.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        QueueDismissSurface.Visibility = Visibility.Collapsed;
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

    private bool CanPresentPlayerSurface(ModuleSession session) =>
        !_isClosing &&
        _current is { IsSettingsOpen: false } &&
        ReferenceEquals(_playerSession, session) &&
        session.Module.PlayerView is not null;

    private void AnimatePlayerDetail(bool open, long generation)
    {
        var presented = PlayerDetailTransform.Y;
        PlayerDetailTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        var height = Math.Max(1, PresentationSurface.ActualHeight);
        var from = Math.Clamp(presented, 0, height);
        if (open && from < 1) from = height;
        var to = open ? 0 : height;
        PlayerDetailTransform.Y = from;
        if (ReducedMotion || !IsVisible || PlayerDetailSurface.Visibility != Visibility.Visible || Math.Abs(to - from) < .5)
        {
            Complete();
            return;
        }
        var duration = TimeSpan.FromMilliseconds((open ? 520d : 360d) * Math.Abs(to - from) / height);
        var animation = new DoubleAnimation(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => Complete();
        PlayerDetailTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, animation,
            HandoffBehavior.SnapshotAndReplace);

        void Complete()
        {
            if (generation != _playerDetailGeneration) return;
            PlayerDetailTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            PlayerDetailTransform.Y = to;
            if (!open)
            {
                PlayerDetailSurface.Visibility = Visibility.Collapsed;
                UpdatePlayerDetailChrome();
                PlayerDetailHost.Content = null;
                _playerDetailSource = null;
                _playerDetailSession = null;
            }
        }
    }

    private void AnimateQueue(bool open, long generation)
    {
        var presented = QueueTransform.X;
        QueueTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        var width = Math.Max(1, QueueSurface.ActualWidth > 0 ? QueueSurface.ActualWidth : QueueSurface.Width);
        var from = Math.Clamp(presented, 0, width + 24);
        if (open && from < 1) from = width + 24;
        var to = open ? 0 : width + 24;
        QueueTransform.X = from;
        if (ReducedMotion || !IsVisible)
        {
            Complete();
            return;
        }
        var duration = TimeSpan.FromMilliseconds(open ? 180 : 120);
        var animation = new DoubleAnimation(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => Complete();
        QueueTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, animation,
            HandoffBehavior.SnapshotAndReplace);

        void Complete()
        {
            if (generation != _queueGeneration) return;
            QueueTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            QueueTransform.X = to;
            if (!open)
            {
                QueueSurface.Visibility = Visibility.Collapsed;
                QueueHost.Content = null;
                _queueSource = null;
                _queueSession = null;
            }
        }
    }

    private void OnQueueDismissClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RunUiAction(CloseQueueAsync);
    }

    private void OnQueueHostSizeChanged(object sender, SizeChangedEventArgs e) =>
        QueueHost.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(new Point(), e.NewSize), 11, 11);

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
        PlayerSurfaceInput.SetQueueOpen(this, false);
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
