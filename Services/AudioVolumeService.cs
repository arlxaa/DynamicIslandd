using System.Runtime.InteropServices;
using System.Windows.Threading;
using DynamicIsland.Helpers;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>
/// Watches the master volume of the default playback device through Windows Core Audio.
/// Event-driven, so nothing is polled. It follows default-device switches (headphones, Bluetooth, HDMI)
/// and needs no NuGet package.
/// </summary>
public sealed class AudioVolumeService : IDisposable
{
    private const int CLSCTX_ALL = 0x17;

    private readonly Dispatcher _dispatcher;
    private readonly VolumeCallback _volumeCallback;
    private readonly DeviceCallback _deviceCallback;
    private readonly Action _raiseOnUiThread;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpointVolume;
    private bool _disposed;

    // Written on a Core Audio worker thread, read on the UI thread.
    private readonly object _gate = new();
    private float _latestLevel;
    private bool _latestMuted;
    private int _uiUpdateQueued; // 1 while an update is already queued, so a burst of changes costs one UI update

    /// <summary>Raised on the UI thread with (level 0..1, muted).</summary>
    public event Action<float, bool>? VolumeChanged;

    public AudioVolumeService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _volumeCallback = new VolumeCallback(this);
        _deviceCallback = new DeviceCallback(this);
        _raiseOnUiThread = RaiseVolumeChanged;
    }

    public void Start()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _enumerator.RegisterEndpointNotificationCallback(_deviceCallback);
            AttachToDefaultDevice();
        }
        catch (Exception ex)
        {
            Log.Error("Core Audio is unavailable", ex);
        }
    }

    private void AttachToDefaultDevice()
    {
        DetachFromDevice();
        if (_enumerator is null || _disposed)
            return;

        IMMDevice? device = null;
        try
        {
            if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device) != 0 || device is null)
                return; // no speakers or headphones connected right now

            Guid iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object? instance) != 0 || instance is null)
                return;

            _endpointVolume = (IAudioEndpointVolume)instance;
            _endpointVolume.RegisterControlChangeNotify(_volumeCallback);
        }
        catch (Exception ex)
        {
            Log.Error("Could not attach to the default audio device", ex);
        }
        finally
        {
            if (device is not null)
                Marshal.ReleaseComObject(device);
        }
    }

    private void DetachFromDevice()
    {
        if (_endpointVolume is null)
            return;
        try { _endpointVolume.UnregisterControlChangeNotify(_volumeCallback); } catch { /* device already gone */ }
        Marshal.ReleaseComObject(_endpointVolume);
        _endpointVolume = null;
    }

    /// <summary>Core Audio thread: just store the newest values and wake the UI thread once.</summary>
    private void OnVolumeNotification(float level, bool muted)
    {
        lock (_gate)
        {
            _latestLevel = level;
            _latestMuted = muted;
        }
        if (Interlocked.Exchange(ref _uiUpdateQueued, 1) == 0)
            _dispatcher.InvokeAsync(_raiseOnUiThread, DispatcherPriority.Normal);
    }

    private void RaiseVolumeChanged()
    {
        Interlocked.Exchange(ref _uiUpdateQueued, 0);
        if (_disposed)
            return;

        float level;
        bool muted;
        lock (_gate)
        {
            level = _latestLevel;
            muted = _latestMuted;
        }
        VolumeChanged?.Invoke(level, muted);
    }

    /// <summary>Never call back into Core Audio from inside its own callback: hop to the UI thread first.</summary>
    private void OnDefaultDeviceChanged() => _dispatcher.InvokeAsync(AttachToDefaultDevice);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        VolumeChanged = null;

        DetachFromDevice();
        if (_enumerator is not null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback); } catch { /* ignore */ }
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }

    // ─────────────── COM callback objects (.NET wraps them in COM-callable wrappers) ───────────────

    private sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly AudioVolumeService _owner;

        public VolumeCallback(AudioVolumeService owner) => _owner = owner;

        public int OnNotify(IntPtr notifyData)
        {
            try
            {
                // AUDIO_VOLUME_NOTIFICATION_DATA layout: GUID guidEventContext (16 bytes), BOOL bMuted, float fMasterVolume, ...
                bool muted = Marshal.ReadInt32(notifyData, 16) != 0;
                float level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(notifyData, 20));
                _owner.OnVolumeNotification(level, muted);
            }
            catch
            {
                // Never let an exception cross the COM boundary.
            }
            return 0; // S_OK
        }
    }

    private sealed class DeviceCallback : IMMNotificationClient
    {
        private readonly AudioVolumeService _owner;

        public DeviceCallback(AudioVolumeService owner) => _owner = owner;

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            if (flow == EDataFlow.eRender && role == ERole.eMultimedia)
                _owner.OnDefaultDeviceChanged();
            return 0;
        }

        public int OnDeviceStateChanged(string deviceId, int newState) => 0;
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}
