using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicIsland.Helpers;

namespace DynamicIsland.Services;

/// <summary>What the island remembers between runs: your to-dos, a running timer and the last timer length.</summary>
public sealed class SavedState
{
    public List<SavedTodo> Todos { get; set; } = new();
    public SavedTimer? Timer { get; set; }
    public int LastTimerMinutes { get; set; } = 5;

    /// <summary>The pill shows just the cover and the visualizer (no song title), like the iPhone.</summary>
    public bool MinimalPill { get; set; }

    /// <summary>The Start menu entry was made once; if you delete it, it stays deleted.</summary>
    public bool StartMenuShortcutCreated { get; set; }
}

public sealed record SavedTodo(string Text, bool Done);

/// <summary>A running timer is saved with the time it ends, a paused one with the time it has left.</summary>
public sealed record SavedTimer(int DurationSeconds, DateTime? EndsAtUtc, int? PausedLeftSeconds);

/// <summary>Compile-time JSON serializer for <see cref="SavedState"/>: no reflection at runtime.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SavedState))]
internal sealed partial class StateJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Reads and writes %LOCALAPPDATA%\DynamicIsland\state.json. A save writes a temporary file first and then
/// swaps it in, so a crash or power cut in the middle of a save never leaves a half-written file behind.
/// </summary>
internal static class StateStore
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland");

    private static readonly string FilePath = Path.Combine(Folder, "state.json");

    public static SavedState Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new SavedState();
            using FileStream stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize(stream, StateJsonContext.Default.SavedState) ?? new SavedState();
        }
        catch (Exception ex)
        {
            // Keep the unreadable file for the curious, and start fresh instead of failing to start.
            Log.Error("Could not read state.json (kept as state.json.bak); starting with an empty list", ex);
            try
            {
                File.Copy(FilePath, FilePath + ".bak", overwrite: true);
            }
            catch
            {
                // Nothing more we can do.
            }
            return new SavedState();
        }
    }

    public static void Save(SavedState state)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(state, StateJsonContext.Default.SavedState));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save state.json", ex);
        }
    }
}
