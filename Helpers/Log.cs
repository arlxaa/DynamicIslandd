using System.Diagnostics;
using System.IO;

namespace DynamicIsland.Helpers;

/// <summary>
/// Tiny error log at %LOCALAPPDATA%\DynamicIsland\log.txt. Nothing is written unless something goes wrong.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static void Error(string message, Exception? ex = null)
    {
        Debug.WriteLine($"[DynamicIsland] {message}: {ex}");
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "log.txt");

            lock (Gate)
            {
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                    File.Delete(file); // keep the log from growing forever
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
