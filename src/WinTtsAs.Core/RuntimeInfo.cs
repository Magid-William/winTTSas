using System.Text.Json;

namespace WinTtsAs.Core;

public sealed class RuntimeInfo
{
    public int Pid { get; set; }
    public int Port { get; set; }
    public string BaseUrl { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public string Version { get; set; } = "";
    public string LogFile { get; set; } = "";
}

public static class RuntimeStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static RuntimeInfo? Read()
    {
        try
        {
            if (!File.Exists(AppPaths.RuntimeFile))
            {
                return null;
            }

            return JsonSerializer.Deserialize<RuntimeInfo>(
                File.ReadAllText(AppPaths.RuntimeFile), Json);
        }
        catch
        {
            return null;
        }
    }

    public static void Write(RuntimeInfo info)
    {
        Directory.CreateDirectory(AppPaths.LocalDir);
        var temp = AppPaths.RuntimeFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(info, Json));
        File.Move(temp, AppPaths.RuntimeFile, overwrite: true);
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(AppPaths.RuntimeFile))
            {
                File.Delete(AppPaths.RuntimeFile);
            }
        }
        catch
        {
            // best effort
        }
    }
}
