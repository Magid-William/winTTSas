using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WinTtsAs.Core;

/// <summary>
/// Per-user shell integration: a Start Menu shortcut, the "Apps &amp; Features"
/// entry that runs <c>winTTSas uninstall</c>, and the install folder marker the
/// uninstaller uses to find what to remove.
/// </summary>
public static class ShellIntegration
{
    public const string InstallKeyPath = @"Software\winTTSas";
    public const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\winTTSas";
    public const string ShortcutName = "winTTSas.lnk";
    public const string Publisher = "Magid William";
    public const string ProjectUrl = "https://github.com/Magid-William/winTTSas";

    public sealed record InstallInfo(
        string InstallDir,
        string ExePath,
        string Version,
        string Publisher,
        string Url,
        int EstimatedSizeKb);

    public static string StartMenuShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft",
        "Windows",
        "Start Menu",
        "Programs",
        ShortcutName);

    public static string? ReadInstallDir()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InstallKeyPath);
            return key?.GetValue("InstallDir") as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes the Start Menu shortcut, Apps &amp; Features entry, and install marker.</summary>
    public static void Install(InstallInfo info)
    {
        // Registry first: the uninstaller needs the marker even if the shortcut
        // cannot be created.
        RegisterUninstallEntry(info);
        WriteInstallMarker(info);
        CreateStartMenuShortcut(info.ExePath, info.InstallDir);
    }

    /// <summary>
    /// Removes the Mark-of-the-Web (the <c>Zone.Identifier</c> stream) from one
    /// file. While it is set the shell refuses to launch the executable and
    /// SmartScreen asks about every run.
    /// </summary>
    /// <returns><c>true</c> when a stream was removed.</returns>
    public static bool UnblockFile(string path)
    {
        var zone = path + ":Zone.Identifier";

        try
        {
            if (!File.Exists(zone))
            {
                return false;
            }

            File.Delete(zone);
            return true;
        }
        catch
        {
            // Missing stream, or a file system that cannot hold one.
            return false;
        }
    }

    /// <summary>
    /// Removes the Mark-of-the-Web (the <c>Zone.Identifier</c> stream) from
    /// every file in <paramref name="directory"/>, the way an installer does
    /// for the files it copies out of a downloaded package.
    /// </summary>
    /// <returns>How many files were unblocked.</returns>
    public static int UnblockFiles(string directory)
    {
        var unblocked = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(
                         directory,
                         "*",
                         SearchOption.AllDirectories))
            {
                if (UnblockFile(file))
                {
                    unblocked++;
                }
            }
        }
        catch
        {
            // Enumeration failed; leave the rest alone.
        }

        return unblocked;
    }

    public static void Uninstall()
    {
        RemoveStartMenuShortcut();
        RemoveUninstallEntry();
        RemoveInstallMarker();
    }

    public static void CreateStartMenuShortcut(string exePath, string workingDirectory)
    {
        var shortcut = StartMenuShortcutPath;
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);

        object? link = null;
        try
        {
            link = new ShellLink();
            var shellLink = (IShellLinkW)link;
            shellLink.SetPath(exePath);
            shellLink.SetWorkingDirectory(workingDirectory);
            shellLink.SetDescription("winTTSas - local OpenAI-compatible text-to-speech server");
            shellLink.SetIconLocation(exePath, 0);
            ((IPersistFile)shellLink).Save(shortcut, true);
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link))
            {
                Marshal.ReleaseComObject(link);
            }
        }
    }

    public static void RemoveStartMenuShortcut()
    {
        try
        {
            if (File.Exists(StartMenuShortcutPath))
            {
                File.Delete(StartMenuShortcutPath);
            }
        }
        catch
        {
            // best effort
        }
    }

    public static void RegisterUninstallEntry(InstallInfo info)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, writable: true)
            ?? throw new InvalidOperationException(
                @"could not open HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\winTTSas");

        var uninstall = $"\"{info.ExePath}\" uninstall --yes";

        key.SetValue("DisplayName", "winTTSas");
        key.SetValue("DisplayVersion", info.Version);
        key.SetValue("Publisher", info.Publisher);
        key.SetValue("DisplayIcon", info.ExePath);
        key.SetValue("InstallLocation", info.InstallDir);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("URLInfoAbout", info.Url);
        key.SetValue("UninstallString", uninstall);
        key.SetValue("QuietUninstallString", uninstall);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

        if (info.EstimatedSizeKb > 0)
        {
            key.SetValue("EstimatedSize", info.EstimatedSizeKb, RegistryValueKind.DWord);
        }
    }

    public static void RemoveUninstallEntry()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// True when the MSI installed this copy (<c>InstalledBy=MSI</c> under
    /// <c>HKCU\Software\winTTSas</c>), so the app must not create a second
    /// shortcut or Apps &amp; Features entry.
    /// </summary>
    public static bool IsMsiInstall()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InstallKeyPath);
            return string.Equals(
                key?.GetValue("InstalledBy") as string,
                "MSI",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteInstallMarker(InstallInfo info)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InstallKeyPath, writable: true)
            ?? throw new InvalidOperationException(@"could not open HKCU\Software\winTTSas");

        key.SetValue("InstallDir", info.InstallDir);
        key.SetValue("ExePath", info.ExePath);
        key.SetValue("Version", info.Version);
        key.SetValue("InstalledAt", DateTime.UtcNow.ToString("O"));
    }

    private static void RemoveInstallMarker()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(InstallKeyPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // best effort
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cch,
            IntPtr pfd,
            uint fFlags);

        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);

        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName,
            int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir,
            int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs,
            int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);

        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cch,
            out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save(
            [MarshalAs(UnmanagedType.LPWStr)] string? pszFileName,
            [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
