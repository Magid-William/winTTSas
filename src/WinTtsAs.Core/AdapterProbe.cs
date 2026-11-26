using Microsoft.Win32;

namespace WinTtsAs.Core;

public sealed record AdapterStatus(
    bool Ok,
    bool X64Registered,
    bool X86Registered,
    string? X64Path,
    string? X86Path,
    string Detail)
{
    public bool IsUnder(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var full = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return new[] { X64Path, X86Path }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Any(p => Path.GetFullPath(p!)
                .StartsWith(full, StringComparison.OrdinalIgnoreCase));
    }
}

public static class AdapterProbe
{
    public const string Clsid = "{013ab33b-ad1a-401c-8bee-f6e2b046a94e}";

    private const string X64Key =
        @"SOFTWARE\Classes\CLSID\" + Clsid + @"\InprocServer32";

    private const string X86Key =
        @"SOFTWARE\WOW6432Node\Classes\CLSID\" + Clsid + @"\InprocServer32";

    public static AdapterStatus Get()
    {
        var x64 = ReadPath(X64Key);
        var x86 = ReadPath(X86Key);

        var x64Ok = !string.IsNullOrWhiteSpace(x64) && File.Exists(x64);
        var x86Ok = !string.IsNullOrWhiteSpace(x86) && File.Exists(x86);

        var detail = (x64Ok, x86Ok) switch
        {
            (true, true) => "ok (x64 + x86)",
            (true, false) => "x64 only; 32-bit apps will not see natural voices",
            (false, true) => "x86 only; 64-bit apps will not see natural voices",
            _ => "not registered; natural voices hidden"
        };

        return new AdapterStatus(x64Ok, x64Ok, x86Ok, x64, x86, detail);
    }

    private static string? ReadPath(string keyPath)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue("") as string;
        }
        catch
        {
            return null;
        }
    }
}
