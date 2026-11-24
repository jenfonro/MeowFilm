using System.Runtime.InteropServices;
using MeowFilm.Core.Debug;

namespace MeowFilm.Media;

/// <summary>
/// CoreAudio render endpoint change monitor via Win32 IMMDeviceEnumerator + IMMNotificationClient.
/// </summary>
internal sealed class AudioEndpointMonitor : IDisposable
{
    private const int DataFlowRender = 0;
    private const int RoleMultimedia = 1;
    private const uint DeviceStateActive = 0x00000001;

    private const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    private const int AudclntEDeviceInUse = unchecked((int)0x8889000A);
    private const int AudclntEEndpointCreateFailed = unchecked((int)0x8889000F);
    private const int AudclntEServiceNotRunning = unchecked((int)0x88890010);
    private const int AudclntEResourcesInvalidated = unchecked((int)0x88890026);
    private const int ErrorNotFound = unchecked((int)0x80070490);

    private readonly object gate = new();
    private IMMDeviceEnumerator? enumerator;
    private NotificationClient? client;
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
            try
            {
                var comEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                var callback = new NotificationClient(this);
                int hr = comEnumerator.RegisterEndpointNotificationCallback(callback);
                if (hr < 0)
                {
                    ReleaseCom(comEnumerator);
                    if (DebugLog.IsEnabled)
                        DebugLog.Warn(DebugCategories.MusicPlayback, "audiokernel", "endpoint_monitor.register_failed",
                            new { hresult = hr });
                    return;
                }
                enumerator = comEnumerator;
                client = callback;
            }
            catch (Exception ex)
            {
                if (DebugLog.IsEnabled)
                    DebugLog.Warn(DebugCategories.MusicPlayback, "audiokernel", "endpoint_monitor.start_failed", error: ex);
            }
        }
    }

    public void Dispose()
    {
        IMMDeviceEnumerator? toRelease;
        NotificationClient? toUnregister;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            toRelease = enumerator;
            toUnregister = client;
            enumerator = null;
            client = null;
        }
        if (toRelease is not null)
        {
            if (toUnregister is not null)
            {
                try { _ = toRelease.UnregisterEndpointNotificationCallback(toUnregister); }
                catch { }
            }
            ReleaseCom(toRelease);
        }
    }

    private void OnDefaultChanged(string? deviceId)
    {
        if (Volatile.Read(ref disposed)) return;
        DefaultRenderDeviceChanged?.Invoke(deviceId);
    }

    private void OnDeviceUnavailable(string deviceId)
    {
        if (Volatile.Read(ref disposed)) return;
        RenderDeviceUnavailable?.Invoke(deviceId);
    }

    private void OnDeviceAvailable(string deviceId)
    {
        if (Volatile.Read(ref disposed)) return;
        RenderDeviceAvailable?.Invoke(deviceId);
    }

    private static void ReleaseCom(object obj)
    {
        try
        {
            if (Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj);
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, NativePropertyKey key);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class NotificationClient(AudioEndpointMonitor owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, uint newState)
        {
            try
            {
                if (!string.IsNullOrEmpty(deviceId))
                {
                    if (newState == DeviceStateActive) owner.OnDeviceAvailable(deviceId);
                    else owner.OnDeviceUnavailable(deviceId);
                }
            }
            catch { }
            return 0;
        }

        public int OnDeviceAdded(string deviceId)
        {
            try
            {
                if (!string.IsNullOrEmpty(deviceId))
                    owner.OnDeviceAvailable(deviceId);
            }
            catch { }
            return 0;
        }

        public int OnDeviceRemoved(string deviceId)
        {
            try
            {
                if (!string.IsNullOrEmpty(deviceId))
                    owner.OnDeviceUnavailable(deviceId);
            }
            catch { }
            return 0;
        }

        public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
        {
            try
            {
                if (flow == DataFlowRender && role == RoleMultimedia)
                    owner.OnDefaultChanged(defaultDeviceId);
            }
            catch { }
            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, NativePropertyKey key) => 0;
    }
}
