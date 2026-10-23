using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DynamicIsland.Helpers;

/// <summary>
/// A "Dynamic Island" entry in your Start menu, so after quitting you can start the island again from Start
/// (press Win and type "Dynamic") instead of looking for the exe. It is made together with "Start with Windows":
/// the copy that starts with Windows is the one you use, so that's the one the entry points to.
/// </summary>
internal static class StartMenuShortcut
{
    private static readonly string LinkPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Dynamic Island.lnk");

    public static bool Exists => File.Exists(LinkPath);

    /// <summary>True if the entry exists and starts this exe.</summary>
    public static bool PointsHere
    {
        get
        {
            if (!Exists || Environment.ProcessPath is not { } exe)
                return false;
            try
            {
                var link = (IShellLinkW)new ShellLink();
                try
                {
                    ((IPersistFile)link).Load(LinkPath, 0);
                    var target = new StringBuilder(1024);
                    link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                    return string.Equals(target.ToString(), exe, StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    Marshal.ReleaseComObject(link);
                }
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Creates the entry, or points an existing one at this exe.</summary>
    public static void CreateOrUpdate()
    {
        if (Environment.ProcessPath is not { } exe)
            return;
        try
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(exe);
                link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
                link.SetDescription("A Dynamic Island for Windows: now playing, timer, to-do list, volume and battery.");
                ((IPersistFile)link).Save(LinkPath, true);
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not add Dynamic Island to the Start menu", ex);
        }
    }

    // The Windows shell's shortcut object (a .lnk file). Methods are declared in vtable order.
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
