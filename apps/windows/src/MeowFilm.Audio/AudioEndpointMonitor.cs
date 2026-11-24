using System.Runtime.InteropServices;
using MeowFilm.Core.Debug;
using NAudio.CoreAudioApi;

namespace MeowFilm.Media;

/// <summary>
/// CoreAudio render endpoint change monitor via NAudio MMDeviceEnumerator + MMDeviceNotificationClient.
/// </summary>
internal sealed class AudioEndpointMonitor : IDisposable
{
    private const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    private const int AudclntEDeviceInUse = unchecked((int)0x8889000A);
    private const int AudclntEEndpointCreateFailed = unchecked((int)0x8889000F);
    private const int AudclntEServiceNotRunning = unchecked((int)0x88890010);
    private const int AudclntEResourcesInvalidated = unchecked((int)0x88890026);
    private const int ErrorNotFound = unchecked((int)0x80070490);

    private readonly object gate = new();
    private MMDeviceEnumerator? enumerator;
    private MMDeviceNotificationClient? client;
    private bool disposed;

    internal event Action<string?>? DefaultRenderDeviceChanged;
    internal event Action<string>? RenderDeviceUnavailable;
    internal event Action<string>? RenderDeviceAvailable;

    internal static bool IsDeviceInvalidation(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is COMException com && com.HResult is
                AudclntEDeviceInvalidated or
                AudclntEDeviceInUse or
                AudclntEEndpointCreateFailed or
                AudclntEServiceNotRunning or
                AudclntEResourcesInvalidated or
                ErrorNotFound)
            {
                return true;
            }
        }
        return false;
    }

    internal void Start()
    {
        lock (gate)
        {
            if (disposed || enumerator is not null) return;
            MMDeviceEnumerator? deviceEnumerator = null;
            MMDeviceNotificationClient? notificationClient = null;
            try
            {
                deviceEnumerator = new MMDeviceEnumerator();
                notificationClient = deviceEnumerator.CreateNotificationClient(useSynchronizationContext: false);
                notificationClient.DefaultDeviceChanged += HandleDefaultDeviceChanged;
                notificationClient.DeviceStateChanged += HandleDeviceStateChanged;
                notificationClient.DeviceAdded += HandleDeviceAdded;
                notificationClient.DeviceRemoved += HandleDeviceRemoved;
                enumerator = deviceEnumerator;
                client = notificationClient;
            }
            catch (Exception ex)
            {
                if (notificationClient is not null)
                {
                    Unsubscribe(notificationClient);
                    try { notificationClient.Dispose(); } catch { }
                }
                try { deviceEnumerator?.Dispose(); } catch { }
                if (DebugLog.IsEnabled)
                    DebugLog.Warn(DebugCategories.MusicPlayback, "audiokernel", "endpoint_monitor.start_failed", error: ex);
            }
        }
    }

    public void Dispose()
    {
        MMDeviceEnumerator? toRelease;
        MMDeviceNotificationClient? toUnregister;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            toRelease = enumerator;
            toUnregister = client;
            enumerator = null;
            client = null;
        }
        if (toUnregister is not null)
        {
            Unsubscribe(toUnregister);
            try { toUnregister.Dispose(); } catch { }
        }
        if (toRelease is not null)
        {
            try { toRelease.Dispose(); } catch { }
        }
    }

    private void Unsubscribe(MMDeviceNotificationClient notificationClient)
    {
        notificationClient.DefaultDeviceChanged -= HandleDefaultDeviceChanged;
        notificationClient.DeviceStateChanged -= HandleDeviceStateChanged;
        notificationClient.DeviceAdded -= HandleDeviceAdded;
        notificationClient.DeviceRemoved -= HandleDeviceRemoved;
    }

    private void HandleDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (Volatile.Read(ref disposed)) return;
        if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia)
            DefaultRenderDeviceChanged?.Invoke(e.DeviceId);
    }

    private void HandleDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e)
    {
        if (Volatile.Read(ref disposed) || string.IsNullOrEmpty(e.DeviceId)) return;
        if (e.NewState == DeviceState.Active) RenderDeviceAvailable?.Invoke(e.DeviceId);
        else RenderDeviceUnavailable?.Invoke(e.DeviceId);
    }

    private void HandleDeviceAdded(object? sender, DeviceNotificationEventArgs e)
    {
        if (Volatile.Read(ref disposed) || string.IsNullOrEmpty(e.DeviceId)) return;
        RenderDeviceAvailable?.Invoke(e.DeviceId);
    }

    private void HandleDeviceRemoved(object? sender, DeviceNotificationEventArgs e)
    {
        if (Volatile.Read(ref disposed) || string.IsNullOrEmpty(e.DeviceId)) return;
        RenderDeviceUnavailable?.Invoke(e.DeviceId);
    }
}
