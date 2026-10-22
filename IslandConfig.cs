using System.Windows;

namespace DynamicIsland;

/// <summary>
/// Every user-tweakable setting in one place: edit a value and rebuild.
/// Sizes are in device-independent pixels (1 DIP = 1 physical pixel at 100 % display scaling).
/// </summary>
public static class IslandConfig
{
    // ─────────────── Size of each state ───────────────

    /// <summary>Idle state: a small black circle.</summary>
    public const double DotSize = 14;

    /// <summary>The pill used for live activities: now playing, volume change, charger connected.</summary>
    public const double CompactWidth = 300;
    public const double CompactHeight = 36;

    /// <summary>The card shown on hover or click.</summary>
    public const double ExpandedWidth = 420;
    public const double ExpandedMediaHeight = 200;   // with media controls
    public const double ExpandedIdleHeight = 132;    // nothing playing: clock, date, battery, weather

    /// <summary>The pill and the dot are fully round; the expanded card's corners stop growing at this radius.</summary>
    public const double MaxCornerRadius = 30;

    // ─────────────── Placement ───────────────

    /// <summary>Gap between the top edge of the screen (or of a top taskbar) and the island.</summary>
    public const double TopMargin = 6;

    /// <summary>
    /// Invisible, mouse-catching margin around the island (alpha 1/255 instead of 0), which makes the
    /// tiny dot easy to hover, including when you push the mouse against the top edge of the screen.
    /// Set it to 0 so that only the visible black shape is clickable and everything else is click-through.
    /// </summary>
    public static readonly double HoverPadding = 8;

    /// <summary>Spare transparent room inside the window so the spring overshoot is never clipped.</summary>
    public const double OvershootRoom = 24;

    public static readonly Thickness IslandMargin = new(HoverPadding, TopMargin, HoverPadding, HoverPadding);
    public static readonly double WindowWidth = ExpandedWidth + 2 * (HoverPadding + OvershootRoom);
    public static readonly double WindowHeight = TopMargin + ExpandedMediaHeight + HoverPadding + OvershootRoom;

    // ─────────────── Behaviour ───────────────

    /// <summary>Hover intent: the pointer must rest this long before the island expands (stops accidental pops).</summary>
    public static readonly TimeSpan HoverExpandDelay = TimeSpan.FromMilliseconds(160);

    /// <summary>Grace period after the pointer leaves before the island collapses.</summary>
    public static readonly TimeSpan HoverCollapseDelay = TimeSpan.FromMilliseconds(320);

    /// <summary>How long the volume indicator stays up after the last change.</summary>
    public static readonly TimeSpan VolumeHudDuration = TimeSpan.FromSeconds(1.8);

    /// <summary>How long the "Charging" activity shows after plugging in.</summary>
    public static readonly TimeSpan ChargingHudDuration = TimeSpan.FromSeconds(3);

    /// <summary>
    /// After a pause, the now-playing pill lingers this long before shrinking to the dot. This also absorbs
    /// the brief "paused" blip that players report while skipping tracks, so the island does not flicker.
    /// </summary>
    public static readonly TimeSpan PausedToDotDelay = TimeSpan.FromSeconds(5);

    /// <summary>Fade the island out while a fullscreen app (game, video, F11 browser) is in front.</summary>
    public static readonly bool HideWhenFullscreen = true;
    public static readonly TimeSpan FullscreenCheckInterval = TimeSpan.FromSeconds(1.5);

    // ─────────────── Media ───────────────

    public static readonly MediaSourceMode MediaSource = MediaSourceMode.PreferSpotify;

    // ─────────────── Weather (optional) ───────────────

    /// <summary>
    /// City name ("Warsaw", "New York") or coordinates ("52.23,21.01") for current weather from Open-Meteo
    /// (free, no API key). Leave empty to turn weather off: then the app makes no network requests at all.
    /// </summary>
    public static readonly string WeatherCity = "";
    public static readonly bool UseFahrenheit = false;
    public static readonly TimeSpan WeatherRefreshInterval = TimeSpan.FromMinutes(30);

    // ─────────────── Performance / RAM ───────────────

    /// <summary>
    /// Render on the CPU instead of the GPU. For a window this small that costs well under 1 % CPU while
    /// animating, and keeps Direct3D plus the graphics driver's DLLs out of the process, which saves a lot of RAM.
    /// </summary>
    public static readonly bool UseSoftwareRendering = true;

    /// <summary>After startup and after the island collapses, return unused memory pages to Windows.</summary>
    public static readonly bool TrimMemoryWhenIdle = true;
}

public enum MediaSourceMode
{
    /// <summary>Show whatever is playing (Spotify first); when nothing plays, keep the last player, else Spotify.</summary>
    PreferSpotify,

    /// <summary>Ignore every app except Spotify.</summary>
    SpotifyOnly,

    /// <summary>Follow Windows' own "current" media session, whichever app owns it.</summary>
    AnyApp,
}
