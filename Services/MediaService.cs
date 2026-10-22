using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Helpers;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Services;

/// <summary>Immutable snapshot of the selected media session.</summary>
public sealed record MediaInfo(
    string Title,
    string Artist,
    string AppName,
    bool IsSpotify,
    bool IsPlaying,
    bool CanPlayPause,
    bool CanGoPrevious,
    bool CanGoNext,
    BitmapSource? Artwork,
    Color? ArtworkAccent);

/// <summary>
/// Spotify and any other player, through Windows' Global System Media Transport Controls: the same API that
/// powers the media card in the Windows volume flyout.
/// <para>
/// Compared with the Spotify Web API (SpotifyAPI.Web) this needs no developer app, no OAuth login and no network,
/// gets instant push updates instead of polling every second, and play/pause/next/previous also work on free
/// Spotify accounts (Web API playback control requires Premium).
/// </para>
/// All state is read and written on the UI thread only; WinRT events are marshalled there first.
/// </summary>
public sealed class MediaService : IDisposable
{
    /// <summary>Album art is decoded at this width: sharp on a 64 DIP tile at 200 % scaling, ~64 KB instead of ~1.6 MB.</summary>
    private const int ArtworkDecodeWidth = 128;

    private readonly Dispatcher _dispatcher;
    private readonly MediaSourceMode _mode;
    private readonly List<GlobalSystemMediaTransportControlsSession> _sessions = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _active;
    private string? _activeId;
    private int _loadVersion; // lets superseded async loads detect that they are stale
    private bool _disposed;

    // Metadata of the active session (playback flags are read live in Publish).
    private string _title = "";
    private string _artist = "";
    private BitmapSource? _artwork;
    private Color? _accent;

    public MediaService(Dispatcher dispatcher, MediaSourceMode mode)
    {
        _dispatcher = dispatcher;
        _mode = mode;
    }

    /// <summary>What is playing right now, or null when no (accepted) player has a session.</summary>
    public MediaInfo? Current { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="Current"/> changes.</summary>
    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            Log.Error("The Windows media session manager is unavailable", ex);
            return;
        }

        if (_disposed)
            return;
        _manager.SessionsChanged += OnSessionsChanged;
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        RebuildSessionList();
    }

    // ─────────────── Commands ───────────────

    public async Task TogglePlayPauseAsync()
    {
        GlobalSystemMediaTransportControlsSession? session = _active;
        if (session is null)
            return;
        try
        {
            // Some players only expose separate Play/Pause commands, others only the toggle.
            var playback = session.GetPlaybackInfo();
            var controls = playback?.Controls;
            bool playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing && controls?.IsPauseEnabled == true)
                await session.TryPauseAsync();
            else if (!playing && controls?.IsPlayEnabled == true)
                await session.TryPlayAsync();
            else
                await session.TryTogglePlayPauseAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Play/pause failed: {ex.Message}");
        }
    }

    public async Task SkipNextAsync()
    {
        try
        {
            if (_active is { } session)
                await session.TrySkipNextAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Next failed: {ex.Message}");
        }
    }

    public async Task SkipPreviousAsync()
    {
        try
        {
            if (_active is { } session)
                await session.TrySkipPreviousAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Previous failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Position and length of the current track. Players report the position only now and then (on play, pause
    /// or seek), so while playing it is extrapolated from the time of the last report.
    /// </summary>
    public bool TryGetTimeline(out TimeSpan position, out TimeSpan duration)
    {
        position = duration = TimeSpan.Zero;
        if (_active is not { } session)
            return false;
        try
        {
            var timeline = session.GetTimelineProperties();
            duration = timeline.EndTime - timeline.StartTime;
            if (duration <= TimeSpan.Zero)
                return false; // this player does not report a timeline

            position = timeline.Position - timeline.StartTime;
            TimeSpan sinceReport = DateTimeOffset.Now - timeline.LastUpdatedTime;
            if (Current?.IsPlaying == true && sinceReport > TimeSpan.Zero && sinceReport < TimeSpan.FromHours(6))
                position += sinceReport;

            position = position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ─────────────── WinRT events (thread-pool threads) → UI thread ───────────────

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        => Post(RebuildSessionList);

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => Post(SelectActiveSession);

    // Any session starting/stopping playback can change which one we show, so re-run the selection.
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => Post(SelectActiveSession);

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => Post(() =>
        {
            if (SafeId(sender) == _activeId)
                _ = LoadMetadataAsync();
        });

    private void Post(Action action)
    {
        if (_disposed)
            return;
        _dispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
                action();
        });
    }

    // ─────────────── Session bookkeeping (UI thread) ───────────────

    private void RebuildSessionList()
    {
        if (_manager is null)
            return;

        foreach (var session in _sessions)
            Unhook(session);
        _sessions.Clear();

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> all;
        try
        {
            all = _manager.GetSessions();
        }
        catch (Exception ex)
        {
            Log.Error("Could not enumerate media sessions", ex);
            all = Array.Empty<GlobalSystemMediaTransportControlsSession>();
        }

        foreach (var session in all)
        {
            if (_mode == MediaSourceMode.SpotifyOnly && !IsSpotify(SafeId(session)))
                continue;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _sessions.Add(session);
        }

        SelectActiveSession();
    }

    private void Unhook(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        }
        catch
        {
            // The player may already be gone.
        }
    }

    private void SelectActiveSession()
    {
        if (_manager is null)
            return;

        GlobalSystemMediaTransportControlsSession? pick = _mode == MediaSourceMode.AnyApp ? CurrentSessionIfTracked() : PickPreferred();
        string? id = pick is null ? null : SafeId(pick);

        _active = pick;
        if (id != _activeId)
        {
            // Another player took over: forget the old metadata and load the new one.
            _activeId = id;
            _title = _artist = "";
            _artwork = null;
            _accent = null;
            if (pick is not null)
                _ = LoadMetadataAsync();
        }
        Publish();
    }

    /// <summary>
    /// Priority: the active session if it is playing → Spotify playing → anything playing →
    /// the active session (paused) → Spotify (paused) → Windows' current session → anything.
    /// Keeping the active player while nothing plays stops the island from jumping between paused apps.
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? PickPreferred()
    {
        GlobalSystemMediaTransportControlsSession? stillActive = null, spotifyPlaying = null, anyPlaying = null, spotify = null;
        foreach (var session in _sessions)
        {
            string id = SafeId(session);
            bool playing = IsPlaying(session);
            if (id == _activeId)
            {
                if (playing)
                    return session;
                stillActive = session;
            }
            if (playing)
            {
                anyPlaying ??= session;
                if (IsSpotify(id))
                    spotifyPlaying ??= session;
            }
            if (IsSpotify(id))
                spotify ??= session;
        }
        return spotifyPlaying ?? anyPlaying ?? stillActive ?? spotify ?? CurrentSessionIfTracked() ?? _sessions.FirstOrDefault();
    }

    private GlobalSystemMediaTransportControlsSession? CurrentSessionIfTracked()
    {
        try
        {
            string? currentId = _manager?.GetCurrentSession() is { } current ? SafeId(current) : null;
            return currentId is null ? null : _sessions.FirstOrDefault(s => SafeId(s) == currentId);
        }
        catch
        {
            return null;
        }
    }

    // ─────────────── Metadata ───────────────

    private async Task LoadMetadataAsync()
    {
        GlobalSystemMediaTransportControlsSession? session = _active;
        if (session is null)
            return;
        int version = ++_loadVersion;

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (version != _loadVersion || _disposed || props is null)
                return;

            string title = props.Title ?? "";
            string artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
            bool trackChanged = title != _title || artist != _artist;
            _title = title;
            _artist = artist;
            if (trackChanged && props.Thumbnail is null)
            {
                _artwork = null;
                _accent = null;
            }
            Publish(); // show the new text right away; the artwork follows a moment later

            // Players often send the new title first and the new cover in a second update; we just reload each time.
            if (props.Thumbnail is null)
                return;
            BitmapSource? artwork = await LoadThumbnailAsync(props.Thumbnail);
            if (version != _loadVersion || _disposed || artwork is null)
                return;
            _artwork = artwork;
            _accent = ColorExtractor.GetAccentColor(artwork);
            Publish();
        }
        catch (Exception ex)
        {
            // Common and harmless while a player is switching tracks.
            Debug.WriteLine($"Media properties unavailable: {ex.Message}");
        }
    }

    private static async Task<BitmapSource?> LoadThumbnailAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 16 * 1024 * 1024)
                return null;

            var bytes = new byte[(int)stream.Size];
            using (var reader = new DataReader(stream))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
                reader.DetachStream(); // the stream is disposed by its own using
            }

            using var memory = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // decode now, so the stream can be released
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = ArtworkDecodeWidth; // decode small: the big source pixels are never kept
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Album art unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Builds a fresh snapshot and raises <see cref="Changed"/> only if it actually differs.</summary>
    private void Publish()
    {
        MediaInfo? info = null;
        if (_active is { } session)
        {
            GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback = null;
            try { playback = session.GetPlaybackInfo(); } catch { /* player exiting */ }
            var controls = playback?.Controls;

            info = new MediaInfo(
                Title: _title,
                Artist: _artist,
                AppName: FriendlyAppName(_activeId),
                IsSpotify: IsSpotify(_activeId),
                IsPlaying: playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                CanPlayPause: controls is null || controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
                CanGoPrevious: controls?.IsPreviousEnabled ?? true,
                CanGoNext: controls?.IsNextEnabled ?? true,
                Artwork: _artwork,
                ArtworkAccent: _accent);
        }

        if (info == Current)
            return;
        Current = info;
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // A bug in a subscriber must not be mistaken for a player hiccup (and silently swallowed below).
            Log.Error("Updating the UI for a media change failed", ex);
        }
    }

    // ─────────────── Helpers ───────────────

    private static string SafeId(GlobalSystemMediaTransportControlsSession session)
    {
        try { return session.SourceAppUserModelId ?? ""; }
        catch { return ""; }
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try { return session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    /// <summary>Desktop Spotify reports "Spotify.exe", the Microsoft Store version "SpotifyAB.SpotifyMusic_…!Spotify".</summary>
    private static bool IsSpotify(string? appId) => appId?.Contains("spotify", StringComparison.OrdinalIgnoreCase) == true;

    private static string FriendlyAppName(string? appId)
    {
        if (string.IsNullOrEmpty(appId))
            return "";

        string id = appId.ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("msedge")) return "Microsoft Edge";
        if (id.Contains("chrome")) return "Google Chrome";
        if (id.Contains("firefox")) return "Firefox";
        if (id.Contains("opera")) return "Opera";
        if (id.Contains("brave")) return "Brave";
        if (id.Contains("zunemusic")) return "Media Player";
        if (id.Contains("vlc")) return "VLC";

        // "Publisher.App_hash!App" → "App", "player.exe" → "player"
        string name = appId;
        int bang = name.LastIndexOf('!');
        if (bang >= 0)
            name = name[(bang + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1)
            name = name[(dot + 1)..];
        return name;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Changed = null;

        foreach (var session in _sessions)
            Unhook(session);
        _sessions.Clear();

        if (_manager is not null)
        {
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager = null;
        }
        _active = null;
        _artwork = null;
    }
}
