using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinTtsAs.Core;

/// <summary>
/// Edits the per-user PATH directly in <c>HKCU\Environment</c> instead of using
/// <c>setx</c>, which truncates long values, and tells Explorer about the change
/// so newly started terminals inherit it.
/// </summary>
public static class UserEnvironment
{
    private const string EnvironmentKeyPath = "Environment";
    private const string PathValueName = "Path";
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        string? lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    public sealed record PathUpdate(
        bool Changed,
        bool Added,
        bool RemovedStale,
        string OldValue,
        string NewValue,
        string? BackupFile);

    /// <summary>The raw user PATH, with <c>%VARIABLES%</c> left unexpanded.</summary>
    public static string ReadUserPathRaw(out RegistryValueKind kind)
    {
        kind = RegistryValueKind.ExpandString;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKeyPath);
            if (key is null)
            {
                return "";
            }

            if (key.GetValue(PathValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                is not string raw)
            {
                return "";
            }

            kind = key.GetValueKind(PathValueName);
            return raw;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>User PATH plus machine PATH, exactly as a new process would see it.</summary>
    public static string EffectivePath(string? userPathRaw = null)
    {
        var user = userPathRaw ?? ReadUserPathRaw(out _);
        var machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";

        return string.Join(
            ';',
            new[] { user, machine }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(ExpandEnvironment));
    }

    /// <summary>
    /// Appends <paramref name="directory"/> to the user PATH once. Entries that
    /// point at another winTTSas install are dropped, so upgrading from a zip in
    /// Downloads (or moving the app folder) cannot leave a stale copy first.
    /// </summary>
    public static PathUpdate AddToUserPath(string directory, bool useVariable = true)
    {
        var target = NormalizeDirectory(directory);
        var raw = ReadUserPathRaw(out var kind);
        var entries = Split(raw).ToList();

        var removedStale = entries.RemoveAll(entry => IsOtherInstall(entry, target));
        var present = entries.Any(entry => Matches(entry, target));

        if (!present)
        {
            entries.Add(EntryFor(target, kind, useVariable));
        }

        var updated = string.Join(';', entries);
        if (removedStale == 0 && present && string.Equals(updated, raw, StringComparison.Ordinal))
        {
            return new PathUpdate(false, false, false, raw, raw, null);
        }

        var backup = Backup(raw);
        Write(updated, kind);
        NotifyEnvironmentChanged();

        return new PathUpdate(true, !present, removedStale > 0, raw, updated, backup);
    }

    public static PathUpdate RemoveFromUserPath(string directory)
    {
        var target = NormalizeDirectory(directory);
        var raw = ReadUserPathRaw(out var kind);
        var entries = Split(raw).ToList();

        // Only the entry for this install is ours to remove; another copy may
        // live elsewhere and keep its own PATH entry.
        var removed = entries.RemoveAll(entry => Matches(entry, target));

        if (removed == 0)
        {
            return new PathUpdate(false, false, false, raw, raw, null);
        }

        var updated = string.Join(';', entries);
        var backup = Backup(raw);
        Write(updated, kind);
        NotifyEnvironmentChanged();

        return new PathUpdate(true, false, false, raw, updated, backup);
    }

    /// <summary>
    /// Resolves an executable the way a new terminal would, using a child
    /// process instead of this process's cached environment.
    /// </summary>
    public static IReadOnlyList<string> ResolveOnPath(string executable, string? effectivePath = null)
    {
        try
        {
            var startInfo = new ProcessStartInfo("where.exe", executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };
            startInfo.Environment["PATH"] = effectivePath ?? EffectivePath();

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            return output
                .Split('\r', '\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static bool IsOnPath(string executable, string directory, string? effectivePath = null)
    {
        var expected = NormalizeDirectory(Path.Combine(NormalizeDirectory(directory), executable));

        return ResolveOnPath(executable, effectivePath)
            .Any(found => string.Equals(
                NormalizeDirectory(found),
                expected,
                StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsPathUnder(string path, string directory)
    {
        var candidate = NormalizeDirectory(path);
        var parent = NormalizeDirectory(directory);

        return candidate.Length > parent.Length &&
               candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
               (candidate[parent.Length] == Path.DirectorySeparatorChar ||
                candidate[parent.Length] == Path.AltDirectorySeparatorChar);
    }

    public static string NormalizeDirectory(string directory)
    {
        var text = ExpandEnvironment(directory.Trim().Trim('"'));

        try
        {
            text = Path.GetFullPath(text);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Keep the caller's text when it is not a path (for example a
            // malformed PATH entry written by another installer).
        }

        return TrimTrailingSeparators(text);
    }

    public static void NotifyEnvironmentChanged()
    {
        try
        {
            SendMessageTimeout(
                HwndBroadcast,
                WmSettingChange,
                IntPtr.Zero,
                "Environment",
                SmtoAbortIfHung,
                5000,
                out _);
        }
        catch
        {
            // Explorer may not be running. New processes still read the registry.
        }
    }

    private static string[] Split(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Matches(string entry, string normalizedTarget) =>
        string.Equals(NormalizeDirectory(entry), normalizedTarget, StringComparison.OrdinalIgnoreCase);

    private static bool IsOtherInstall(string entry, string target)
    {
        var normalized = NormalizeDirectory(entry);
        if (string.Equals(normalized, target, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Any folder that holds our server executable is another copy of the
        // app, whatever it is called.
        return File.Exists(Path.Combine(normalized, "winTTSas-server.exe"));
    }

    private static string EntryFor(string target, RegistryValueKind kind, bool useVariable)
    {
        if (!useVariable || kind == RegistryValueKind.String)
        {
            return target;
        }

        var roots = new[]
        {
            ("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            ("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            ("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
        };

        foreach (var (variable, root) in roots)
        {
            var relative = RelativeUnder(target, root);
            if (relative is not null)
            {
                return variable + relative;
            }
        }

        return target;
    }

    private static string? RelativeUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var parent = TrimTrailingSeparators(Path.GetFullPath(root));
        if (path.Length <= parent.Length ||
            !path.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var separator = path[parent.Length];
        if (separator != Path.DirectorySeparatorChar && separator != Path.AltDirectorySeparatorChar)
        {
            return null;
        }

        return path[parent.Length..];
    }

    private static string TrimTrailingSeparators(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 2 && trimmed[1] == ':'
            ? trimmed + Path.DirectorySeparatorChar
            : trimmed;
    }

    private static string ExpandEnvironment(string value)
    {
        try
        {
            return Environment.ExpandEnvironmentVariables(value);
        }
        catch
        {
            return value;
        }
    }

    private static string? Backup(string raw)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LocalDir);
            var file = Path.Combine(
                AppPaths.LocalDir,
                $"path-backup-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, raw);
            return file;
        }
        catch
        {
            return null;
        }
    }

    private static void Write(string value, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.CreateSubKey(EnvironmentKeyPath, writable: true)
            ?? throw new InvalidOperationException(@"could not open HKCU\Environment");

        var writeKind = kind == RegistryValueKind.String
            ? RegistryValueKind.String
            : RegistryValueKind.ExpandString;

        key.SetValue(PathValueName, value, writeKind);
    }
}
