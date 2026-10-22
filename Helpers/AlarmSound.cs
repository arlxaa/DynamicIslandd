using System.IO;
using DynamicIsland.Interop;

namespace DynamicIsland.Helpers;

/// <summary>
/// The "timer done" sound, played on a loop through the Windows multimedia API (PlaySound): no audio library,
/// and nothing is loaded until a timer actually rings.
/// </summary>
internal static class AlarmSound
{
    public static void Start(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return; // silent alerts

        string path = Environment.ExpandEnvironmentVariables(file);
        bool playing = File.Exists(path) && NativeMethods.PlaySound(path, IntPtr.Zero,
            NativeMethods.SND_FILENAME | NativeMethods.SND_ASYNC | NativeMethods.SND_LOOP | NativeMethods.SND_NODEFAULT);

        // A missing or unreadable file: play the standard Windows alert sound once instead.
        if (!playing)
            NativeMethods.PlaySound("SystemExclamation", IntPtr.Zero, NativeMethods.SND_ALIAS | NativeMethods.SND_ASYNC | NativeMethods.SND_NODEFAULT);
    }

    public static void Stop() => NativeMethods.PlaySound(null, IntPtr.Zero, 0);
}
