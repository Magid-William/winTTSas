namespace WinTtsAs.Core;

public static class AppPaths
{
    public const string AppName = "winTTSas";

    private static string? DataDirOverride =>
        Environment.GetEnvironmentVariable("WINTTSAS_DATA_DIR");

    public static string ConfigDir =>
        DataDirOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string LocalDir =>
        DataDirOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public static string RuntimeFile => Path.Combine(LocalDir, "runtime.json");
    public static string LogDir => Path.Combine(LocalDir, "logs");
    public static string CacheDir => Path.Combine(LocalDir, "cache");

    public static string ServerExe => Path.Combine(AppContext.BaseDirectory, "winTTSas-server.exe");

    public static string AdapterDir =>
        Path.Combine(AppContext.BaseDirectory, "tools", "NaturalVoiceSAPIAdapter");

    public static string AdapterX64Dll =>
        Path.Combine(AdapterDir, "x64", "NaturalVoiceSAPIAdapter.dll");

    public static string AdapterX86Dll =>
        Path.Combine(AdapterDir, "x86", "NaturalVoiceSAPIAdapter.dll");

    public static string BaseUrl(int port, string bind = "127.0.0.1")
    {
        var host = string.IsNullOrWhiteSpace(bind) || bind == "0.0.0.0" || bind == "::"
            ? "127.0.0.1"
            : bind;
        return $"http://{host}:{port}";
    }

    public static string OpenAiBaseUrl(int port, string bind = "127.0.0.1") =>
        BaseUrl(port, bind) + "/v1";

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(LocalDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(CacheDir);
    }
}
