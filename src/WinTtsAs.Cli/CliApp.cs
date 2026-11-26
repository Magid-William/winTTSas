using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;
using WinTtsAs.Core;

namespace WinTtsAs.Cli;

public static class CliApp
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "winTTSas";

    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // no console attached
        }

        if (args.Length == 0)
        {
            return Help();
        }

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "help" or "-h" or "--help" => Help(),
                "version" or "-v" or "--version" => Version(),
                "start" => await StartAsync(rest, cancellationToken),
                "stop" => await StopAsync(cancellationToken),
                "sample" => Sample(rest),
                "status" => await StatusAsync(rest, cancellationToken),
                "list" => List(rest),
                "select" => await SelectAsync(rest, cancellationToken),
                "speed" => await SpeedAsync(rest, cancellationToken),
                "update" => await UpdateAsync(rest, cancellationToken),
                "logon" => await LogonAsync(rest, cancellationToken),
                "uninstall" => await UninstallAsync(rest, cancellationToken),
                _ => Unknown(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine($"""
winTTSas {BuildInfo.Version} - local OpenAI-compatible TTS for Windows voices

Usage:
  winTTSas start [--foreground] [--quiet]
  winTTSas stop
  winTTSas sample [text]
  winTTSas status [--json]
  winTTSas list [--json]
  winTTSas select <index|name|slug>
  winTTSas speed [0.25-4.0]
  winTTSas update port [port]
  winTTSas update format [mp3|wav|opus|aac|flac|pcm]
  winTTSas logon on|off
  winTTSas uninstall [--yes] [--keep-data] [--remove-adapter]
  winTTSas help

OpenAI clients:
  Base URL: http://127.0.0.1:5178/v1
  API key:  local (or the configured token)
  Model:    tts-1
  Voice:    alloy

Notes:
  - start runs a background server; --foreground keeps it in this console.
  - update format applies live; update port restarts the server.
  - logon uses HKCU Run and starts the hidden server at login.
""");
        return 0;
    }

    private static int Version()
    {
        Console.WriteLine($"winTTSas {BuildInfo.Version}");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        Console.Error.WriteLine("run: winTTSas help");
        return 1;
    }

    private static async Task<int> StartAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var foreground = HasFlag(args, "--foreground");
        var quiet = HasFlag(args, "--quiet");

        if (foreground)
        {
            return await ServerHost.RunAsync(
                ["--foreground"],
                consoleLogging: true,
                cancellationToken);
        }

        var current = await ControlPipeClient.GetStatusAsync(500, cancellationToken);
        if (current is not null)
        {
            if (quiet)
            {
                Console.WriteLine(current.BaseUrl);
            }
            else
            {
                PrintStatus(current);
            }

            return 0;
        }

        var serverExe = ResolveServerExe();
        if (!File.Exists(serverExe))
        {
            Console.Error.WriteLine($"server executable not found: {serverExe}");
            Console.Error.WriteLine("publish first (scripts\\publish.ps1) or use: winTTSas start --foreground");
            return 1;
        }

        var stale = RuntimeStore.Read();
        if (stale is not null && IsProcessAlive(stale.Pid))
        {
            Console.Error.WriteLine(
                $"server process {stale.Pid} is alive but not answering the control pipe.");
            Console.Error.WriteLine($"log: {stale.LogFile}");
            return 1;
        }

        RuntimeStore.Delete();

        var startInfo = new ProcessStartInfo(serverExe)
        {
            // ShellExecute avoids handing the hidden server the CLI's stdout/stderr
            // pipe. Otherwise `winTTSas start | Out-Null` can wait forever for the
            // pipeline to close even after the CLI has exited.
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        };

        if (HasFlag(args, "--logon"))
        {
            startInfo.Arguments = "--logon";
        }

        var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine("could not start the server process.");
            return 1;
        }

        for (var attempt = 0; attempt < 80; attempt++)
        {
            await Task.Delay(250, cancellationToken);

            var status = await ControlPipeClient.GetStatusAsync(400, cancellationToken);
            if (status is not null)
            {
                if (quiet)
                {
                    Console.WriteLine(status.BaseUrl);
                }
                else
                {
                    PrintStatus(status);
                }

                return 0;
            }

            if (process.HasExited)
            {
                break;
            }
        }

        Console.Error.WriteLine(process.HasExited
            ? $"server exited with code {process.ExitCode}."
            : "server did not become ready in time.");
        PrintLogTail();
        return 1;
    }

    private static int Sample(string[] args)
    {
        var config = ConfigStore.Load();
        var catalog = VoiceCatalog.Load();
        var voice = catalog.Resolve(config.DefaultVoice, config);

        if (voice is null)
        {
            Console.Error.WriteLine("no SAPI voices are installed.");
            return 1;
        }

        var text = string.Join(" ", args).Trim();
        if (text.Length == 0)
        {
            text = SpeechSampler.DefaultSentence;
        }

        if (text.Length > config.MaxTextLength)
        {
            Console.Error.WriteLine(
                $"sample text must be {config.MaxTextLength} characters or fewer.");
            return 1;
        }

        var rate = SpeechRate.FromSpeed(config.DefaultSpeed);

        Console.WriteLine($"voice:  {voice.Name}");
        Console.WriteLine($"speed:  {config.DefaultSpeed:0.00} (SAPI rate {rate})");
        Console.WriteLine($"volume: {config.Volume}");
        Console.WriteLine($"text:   {text}");
        Console.WriteLine();
        Console.WriteLine("speaking...");

        SpeechSampler.Speak(voice.Name, text, rate, config.Volume);

        Console.WriteLine("done.");
        return 0;
    }

    private static async Task<int> StopAsync(CancellationToken cancellationToken)
    {
        var status = await ControlPipeClient.GetStatusAsync(500, cancellationToken);
        if (status is null)
        {
            Console.WriteLine("winTTSas is not running.");
            return 0;
        }

        var response = await ControlPipeClient.SendAsync(
            new ControlRequest { Cmd = "stop" },
            2000,
            cancellationToken);

        if (response is null || !response.Ok)
        {
            Console.Error.WriteLine("server did not acknowledge stop.");
            return 1;
        }

        Console.WriteLine("stopping...");

        var runtime = RuntimeStore.Read();
        if (runtime is not null && TryGetProcess(runtime.Pid, out var process))
        {
            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
                catch
                {
                    // best effort
                }
            }
        }

        RuntimeStore.Delete();
        Console.WriteLine("stopped.");
        return 0;
    }

    private static async Task<int> StatusAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var status = await ControlPipeClient.GetStatusAsync(500, cancellationToken);
        var json = HasFlag(args, "--json");

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                running = status is not null,
                status,
                adapter = AdapterProbe.Get(),
                logon = IsLogonEnabled(),
                configFile = AppPaths.ConfigFile,
                logDir = AppPaths.LogDir
            }, JsonOut));
            return 0;
        }

        if (status is not null)
        {
            PrintStatus(status);
            return 0;
        }

        PrintStopped();
        return 0;
    }

    private static int List(string[] args)
    {
        var config = ConfigStore.Load();
        var catalog = VoiceCatalog.Load();
        var resolved = catalog.Resolve(config.DefaultVoice, config);

        if (HasFlag(args, "--json"))
        {
            var data = catalog.Voices.Select((voice, index) => new
            {
                index = index + 1,
                name = voice.Name,
                slug = voice.Slug,
                language = voice.Language,
                gender = voice.Gender,
                natural = voice.IsNatural,
                @default = ReferenceEquals(voice, resolved),
                description = voice.Description,
                sampleRate = voice.SampleRate
            });

            Console.WriteLine(JsonSerializer.Serialize(data, JsonOut));
            return 0;
        }

        PrintVoiceTable(catalog, resolved);

        if (catalog.NaturalCount == 0)
        {
            Console.WriteLine();
            Console.WriteLine("No natural voices are visible to SAPI.");
            Console.WriteLine("Install Narrator voices, then run: scripts\\install-adapter.ps1");
        }

        return 0;
    }

    private static async Task<int> SelectAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var config = ConfigStore.Load();
        var catalog = VoiceCatalog.Load();
        var resolved = catalog.Resolve(config.DefaultVoice, config);

        if (args.Length == 0)
        {
            PrintVoiceTable(catalog, resolved);

            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("usage: winTTSas select <index|name|slug>");
                return 1;
            }

            Console.Write("Select voice: ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
            {
                return 1;
            }

            args = [input.Trim()];
        }

        var query = string.Join(" ", args).Trim();
        VoiceDescriptor? selected = null;

        if (int.TryParse(query, out var index) &&
            index >= 1 &&
            index <= catalog.Voices.Count)
        {
            selected = catalog.Voices[index - 1];
        }

        selected ??= catalog.FindExact(query);

        if (selected is null)
        {
            Console.Error.WriteLine($"voice not found: {query}");
            Console.Error.WriteLine("run: winTTSas list");
            return 1;
        }

        config.DefaultVoice = selected.Name;
        ConfigStore.Save(config);
        await ReloadAsync(cancellationToken);

        Console.WriteLine($"Default voice set to {selected.Name}.");
        return 0;
    }

    private static async Task<int> SpeedAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var config = ConfigStore.Load();

        if (args.Length == 0)
        {
            Console.WriteLine(config.DefaultSpeed.ToString("0.00", CultureInfo.InvariantCulture));
            return 0;
        }

        if (!double.TryParse(
                args[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var speed) ||
            speed is < 0.25 or > 4.0)
        {
            Console.Error.WriteLine("speed must be between 0.25 and 4.0.");
            return 1;
        }

        config.DefaultSpeed = speed;
        ConfigStore.Save(config);
        await ReloadAsync(cancellationToken);

        Console.WriteLine($"Default speed set to {speed:0.00}.");
        return 0;
    }

    private static async Task<int> UpdateAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: winTTSas update <port|format> [value]");
            return 1;
        }

        return args[0].ToLowerInvariant() switch
        {
            "port" => await UpdatePortAsync(args[1..], cancellationToken),
            "format" => await UpdateFormatAsync(args[1..], cancellationToken),
            _ => Unknown("update " + args[0])
        };
    }

    private static async Task<int> UpdatePortAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var config = ConfigStore.Load();

        if (args.Length == 0)
        {
            Console.WriteLine(config.Port);

            if (Console.IsInputRedirected)
            {
                return 0;
            }

            Console.Write($"New port [{config.Port}]: ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
            {
                return 0;
            }

            args = [input.Trim()];
        }

        if (!int.TryParse(args[0], out var port) || port is < 1024 or > 65535)
        {
            Console.Error.WriteLine("port must be between 1024 and 65535.");
            return 1;
        }

        if (!IsPortFree(config.Bind, port))
        {
            Console.Error.WriteLine($"port {port} is already in use.");
            return 1;
        }

        var wasRunning = await ControlPipeClient.GetStatusAsync(500, cancellationToken) is not null;
        if (wasRunning && await StopAsync(cancellationToken) != 0)
        {
            return 1;
        }

        config.Port = port;
        ConfigStore.Save(config);
        Console.WriteLine($"Port set to {port}.");

        return wasRunning ? await StartAsync([], cancellationToken) : 0;
    }

    private static async Task<int> UpdateFormatAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var config = ConfigStore.Load();

        if (args.Length == 0)
        {
            Console.WriteLine(config.DefaultFormat);
            return 0;
        }

        var requested = args[0].Trim().ToLowerInvariant();
        if (!AudioFormats.IsSupported(requested))
        {
            Console.Error.WriteLine(
                $"unsupported format: {requested}; use {string.Join(", ", AudioFormats.All)}");
            return 1;
        }

        config.DefaultFormat = AudioFormats.Normalize(requested);
        ConfigStore.Save(config);
        await ReloadAsync(cancellationToken);

        Console.WriteLine($"Default format set to {config.DefaultFormat}.");
        return 0;
    }

    private static async Task<int> LogonAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        if (sub == "on")
        {
            var serverExe = ResolveServerExe();
            if (!File.Exists(serverExe))
            {
                Console.Error.WriteLine($"server executable not found: {serverExe}");
                return 1;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key?.SetValue(RunValueName, $"\"{serverExe}\" --logon");
            Console.WriteLine("Logon startup enabled.");

            if (await ControlPipeClient.GetStatusAsync(300, cancellationToken) is null)
            {
                return await StartAsync(["--quiet"], cancellationToken);
            }

            return 0;
        }

        if (sub == "off")
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            Console.WriteLine("Logon startup disabled.");
            return 0;
        }

        Console.WriteLine(IsLogonEnabled() ? "logon: on" : "logon: off");
        return 0;
    }

    private static async Task<int> UninstallAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var yes = HasFlag(args, "--yes");
        var keepData = HasFlag(args, "--keep-data");
        var removeAdapter = HasFlag(args, "--remove-adapter");

        if (!yes)
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("non-interactive uninstall requires --yes.");
                return 1;
            }

            Console.Write("Stop server, remove logon, and delete config/cache/logs? [y/N] ");
            var answer = Console.ReadLine()?.Trim();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("cancelled.");
                return 0;
            }
        }

        await StopAsync(cancellationToken);
        RemoveLogonEntry();

        if (!keepData)
        {
            DeleteDirectory(AppPaths.ConfigDir);
            DeleteDirectory(AppPaths.LocalDir);
        }

        if (removeAdapter)
        {
            UnregisterAdapter();
        }

        Console.WriteLine("winTTSas uninstalled. You can delete this folder.");
        return 0;
    }

    private static async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var response = await ControlPipeClient.SendAsync(
            new ControlRequest { Cmd = "reload" },
            2000,
            cancellationToken);

        if (response is { Ok: false })
        {
            Console.Error.WriteLine("reload failed: " + response.Error);
        }
    }

    private static void PrintStatus(StatusInfo status)
    {
        Console.WriteLine($"winTTSas {status.Version}");
        Console.WriteLine($"state:    running (pid {status.Pid})");
        Console.WriteLine($"base URL: {status.BaseUrl}");
        Console.WriteLine($"voice:    {status.Voice}");
        Console.WriteLine($"format:   {status.Format}");
        Console.WriteLine($"speed:    {status.Speed:0.00}");
        Console.WriteLine($"voices:   {status.Voices} installed ({status.NaturalVoices} natural)");
        Console.WriteLine($"queue:    {status.Queue}/{status.QueueLimit}");
        Console.WriteLine($"workers:  {status.Workers}");
        Console.WriteLine($"uptime:   {FormatUptime(status.UptimeSeconds)}");
        Console.WriteLine($"logon:    {(IsLogonEnabled() ? "on" : "off")}");
        Console.WriteLine($"adapter:  {status.Adapter}");
        Console.WriteLine($"ffmpeg:   {status.Ffmpeg}");
        Console.WriteLine($"logs:     {status.LogDir}");
    }

    private static void PrintStopped()
    {
        var config = ConfigStore.Load();
        var catalog = VoiceCatalog.Load();
        var voice = catalog.Resolve(config.DefaultVoice, config);
        var adapter = AdapterProbe.Get();

        Console.WriteLine($"winTTSas {BuildInfo.Version}");
        Console.WriteLine("state:    stopped");
        Console.WriteLine($"port:     {config.Port}");
        Console.WriteLine($"voice:    {voice?.Name ?? "none"}");
        Console.WriteLine($"format:   {config.DefaultFormat}");
        Console.WriteLine($"speed:    {config.DefaultSpeed:0.00}");
        Console.WriteLine($"voices:   {catalog.Voices.Count} installed ({catalog.NaturalCount} natural)");
        Console.WriteLine($"logon:    {(IsLogonEnabled() ? "on" : "off")}");
        Console.WriteLine($"adapter:  {adapter.Detail}");
        Console.WriteLine($"config:   {AppPaths.ConfigFile}");
        Console.WriteLine($"logs:     {AppPaths.LogDir}");
    }

    private static void PrintVoiceTable(VoiceCatalog catalog, VoiceDescriptor? selected)
    {
        Console.WriteLine("  #  Voice                              Lang   Natural  Default");
        Console.WriteLine("---  ---------------------------------  -----  -------  -------");

        for (var index = 0; index < catalog.Voices.Count; index++)
        {
            var voice = catalog.Voices[index];
            var marker = ReferenceEquals(voice, selected) ? "*" : "";

            Console.WriteLine(
                $"{index + 1,3}  {Truncate(voice.Name, 33),-33}  {voice.Language,-5}  {(voice.IsNatural ? "yes" : "no"),-7}  {marker}");
        }
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..Math.Max(0, length - 1)] + "…";

    private static string FormatUptime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d {span:hh\\:mm\\:ss}"
            : span.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private static string ResolveServerExe()
    {
        var configured = Environment.GetEnvironmentVariable("WINTTSAS_SERVER_EXE");
        return !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "winTTSas-server.exe");
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetProcess(int pid, out Process process)
    {
        try
        {
            process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            process = null!;
            return false;
        }
    }

    private static bool IsPortFree(string bind, int port)
    {
        try
        {
            var address = string.IsNullOrWhiteSpace(bind) ||
                          bind.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? IPAddress.Loopback
                : IPAddress.Parse(bind);

            var listener = new TcpListener(address, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLogonEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveLogonEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch
        {
            // best effort
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not delete {path}: {ex.Message}");
        }
    }

    private static void PrintLogTail()
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogDir))
            {
                return;
            }

            var file = Directory.EnumerateFiles(AppPaths.LogDir, "server-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (file is null)
            {
                return;
            }

            Console.Error.WriteLine($"log: {file}");
            foreach (var line in File.ReadLines(file).TakeLast(20))
            {
                Console.Error.WriteLine("  " + line);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static void UnregisterAdapter()
    {
        var adapter = AdapterProbe.Get();
        var paths = new[] { adapter.X64Path, adapter.X86Path }
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (paths.Length == 0)
        {
            Console.WriteLine("NaturalVoiceSAPIAdapter is not registered.");
            return;
        }

        foreach (var path in paths)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "regsvr32.exe",
                    Arguments = $"/u /s \"{path}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                using var process = Process.Start(startInfo);
                process?.WaitForExit();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"could not unregister {path}: {ex.Message}");
            }
        }

        DeleteDirectory(AppPaths.AdapterDir);
    }
}
