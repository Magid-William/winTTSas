using System.Diagnostics;
using WinTtsAs.Core;

namespace WinTtsAs.Cli;

public static partial class CliApp
{
    private static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        AppPaths.AppName);

    private static async Task<int> SetupAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var assumeYes = HasFlag(args, "--yes") || HasFlag(args, "-y");
        var portable = HasFlag(args, "--portable");
        var skipAdapter = HasFlag(args, "--skip-adapter");
        var skipLogon = HasFlag(args, "--no-logon");
        var skipPath = HasFlag(args, "--skip-path");
        var skipVoice = HasFlag(args, "--skip-voice");
        var skipTest = HasFlag(args, "--skip-test");
        var noStart = HasFlag(args, "--no-start");
        var openTestPage = HasFlag(args, "--open");
        var requestedDir = ReadOptionValue(args, "--dir");

        if (HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            PrintSetupHelp();
            return 0;
        }

        if (!assumeYes && Console.IsInputRedirected)
        {
            Console.Error.WriteLine("non-interactive setup requires --yes.");
            Console.Error.WriteLine(
                "usage: winTTSas setup --yes [--dir <path>] [--portable] [--skip-adapter]");
            return 1;
        }

        var sourceDir = UserEnvironment.NormalizeDirectory(AppContext.BaseDirectory);
        if (!File.Exists(Path.Combine(sourceDir, "winTTSas-server.exe")))
        {
            Console.Error.WriteLine("winTTSas-server.exe was not found next to this executable.");
            Console.Error.WriteLine(
                "extract a release zip first, or run scripts\\publish.ps1 from a source checkout.");
            return 1;
        }

        Console.WriteLine($"winTTSas {BuildInfo.Version} setup");
        Console.WriteLine();

        if (portable && !string.IsNullOrWhiteSpace(requestedDir))
        {
            Console.WriteLine("note: --portable keeps the current folder; ignoring --dir.");
        }

        var installDir = portable
            ? sourceDir
            : ResolveInstallDir(requestedDir, assumeYes);

        if (!string.Equals(installDir, sourceDir, StringComparison.OrdinalIgnoreCase) &&
            UserEnvironment.IsPathUnder(installDir, sourceDir))
        {
            Console.Error.WriteLine("error: the install folder cannot be inside the release folder.");
            Console.Error.WriteLine($"  release: {sourceDir}");
            Console.Error.WriteLine($"  install: {installDir}");
            return 1;
        }

        var sameLocation = string.Equals(
            installDir,
            sourceDir,
            StringComparison.OrdinalIgnoreCase);

        if (!sameLocation)
        {
            if (await ControlPipeClient.GetStatusAsync(500, cancellationToken) is not null)
            {
                Console.WriteLine("stopping the running server before copying files...");
                await StopAsync(cancellationToken);
            }

            Console.WriteLine($"copying files to {installDir}");
            var copied = CopyRelease(sourceDir, installDir);
            Console.WriteLine($"copied  {copied / (1024.0 * 1024.0):0.0} MB");
        }
        else if (portable)
        {
            Console.WriteLine($"portable install: files stay in {installDir}");
            Console.WriteLine("keep this folder where it is; PATH and the adapter point at it.");
        }
        else
        {
            Console.WriteLine($"using the files already in {installDir}");
        }

        Console.WriteLine();

        // Files extracted from a downloaded zip keep the Mark-of-the-Web, which
        // makes the shell refuse to start the server with "The operation was
        // canceled by the user". Clear it on both copies before launching.
        var unblocked = ShellIntegration.UnblockFiles(installDir);
        if (!sameLocation)
        {
            ShellIntegration.UnblockFiles(sourceDir);
        }
        if (unblocked > 0)
        {
            Console.WriteLine(
                $"unblocked {unblocked} downloaded file(s) so Windows stops asking about them");
        }

        await StepAdapterAsync(installDir, skipAdapter, assumeYes, cancellationToken);
        StepPath(installDir, skipPath);
        await StepServerAsync(
            installDir,
            skipLogon,
            assumeYes,
            noStart,
            cancellationToken);

        Console.WriteLine();
        await StepVoiceAsync(skipVoice, assumeYes, cancellationToken);

        if (!skipTest && !assumeYes &&
            Confirm("Speak a test sentence now?", defaultYes: true))
        {
            Console.WriteLine();
            if (await SampleAsync(
                    ["Hello from winTTSas. The server is ready."],
                    cancellationToken) != 0)
            {
                Console.WriteLine(
                    "sample failed; if it was silent run: winTTSas sample --devices");
            }
        }

        var sizeKb = EstimateSizeKb(installDir);
        var exePath = Path.Combine(installDir, "winTTSas.exe");

        if (File.Exists(exePath))
        {
            var info = new ShellIntegration.InstallInfo(
                installDir,
                exePath,
                BuildInfo.Version,
                ShellIntegration.Publisher,
                ShellIntegration.ProjectUrl,
                sizeKb);

            try
            {
                ShellIntegration.Install(info);
                Console.WriteLine("shell:    Start Menu shortcut and Apps & Features entry created");
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"warning: could not create the shortcut or uninstall entry: {ex.Message}");
            }
        }

        await PrintSetupSummaryAsync(
            installDir,
            sizeKb,
            openTestPage,
            skipPath,
            assumeYes,
            cancellationToken);

        return 0;
    }

    private static void PrintSetupHelp()
    {
        Console.WriteLine("""
Usage: winTTSas setup [options]

Installs winTTSas for the current user:
  1. copies the release to %LOCALAPPDATA%\Programs\winTTSas
  2. registers the natural voice adapter (one UAC prompt)
  3. adds the folder to the user PATH
  4. starts the server and enables logon startup
  5. picks a default voice
  6. adds a Start Menu shortcut and an Apps & Features entry

Options:
  --yes              Accept every default; no prompts, no test speech
  --dir <path>       Install folder (default %LOCALAPPDATA%\Programs\winTTSas)
  --portable         Keep the files where they are instead of copying
  --skip-adapter     Do not register the natural voice adapter
  --no-logon         Do not start the server at login
  --skip-path        Do not change the user PATH
  --skip-voice       Keep the current default voice
  --skip-test        Do not speak a test sentence
  --no-start         Do not start the server now
  --open             Open the test page when done
  --help             Show this help
""");
    }

    private static async Task PrintSetupSummaryAsync(
        string installDir,
        int sizeKb,
        bool openTestPage,
        bool pathSkipped,
        bool assumeYes,
        CancellationToken cancellationToken)
    {
        var config = ConfigStore.Load();
        var server = await ControlPipeClient.GetStatusAsync(500, cancellationToken);
        var rootUrl = server?.BaseUrl;
        if (string.IsNullOrWhiteSpace(rootUrl))
        {
            rootUrl = AppPaths.BaseUrl(config.Port, config.Bind);
        }
        else if (rootUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            // The control pipe reports the OpenAI base URL, not the server root.
            rootUrl = rootUrl[..^3];
        }

        rootUrl = rootUrl.TrimEnd('/');
        var voice = VoiceCatalog.Load().Resolve(config.DefaultVoice, config);
        var command = pathSkipped
            ? Path.Combine(installDir, "winTTSas.exe")
            : "winTTSas";

        Console.WriteLine();
        Console.WriteLine($"installed: {installDir} ({sizeKb / 1024.0:0.0} MB)");
        Console.WriteLine(pathSkipped
            ? $"command:   {command}"
            : "command:   winTTSas (open a new terminal to use it)");
        Console.WriteLine(
            $"server:    {(server is null ? "not running" : $"running (pid {server.Pid})")}");
        Console.WriteLine($"base URL:  {rootUrl}/v1");
        Console.WriteLine($"voice:     {voice?.Name ?? "none"}");
        Console.WriteLine($"logon:     {(IsLogonEnabled() ? "on" : "off")}");
        Console.WriteLine($"adapter:   {AdapterProbe.Get().Detail}");
        Console.WriteLine();
        Console.WriteLine("Try in a new terminal:");
        Console.WriteLine($"  {command} status");
        Console.WriteLine($"  {command} sample \"Hello from winTTSas.\"");
        Console.WriteLine();
        Console.WriteLine($"Test page: {rootUrl}/");

        if (openTestPage ||
            (!assumeYes && Confirm("Open the test page?", defaultYes: false)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(rootUrl + "/") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"could not open the browser: {ex.Message}");
            }
        }
    }

    private static async Task StepAdapterAsync(
        string installDir,
        bool skip,
        bool assumeYes,
        CancellationToken cancellationToken)
    {
        var adapterDir = Path.Combine(installDir, "tools", "NaturalVoiceSAPIAdapter");
        var adapterPresent = File.Exists(
            Path.Combine(adapterDir, "x64", "NaturalVoiceSAPIAdapter.dll"));

        if (skip)
        {
            Console.WriteLine("natural voices: skipped");
            return;
        }

        if (!adapterPresent)
        {
            Console.WriteLine(
                "natural voices: adapter not bundled; classic SAPI voices still work");
            return;
        }

        var adapter = AdapterProbe.Get();
        if (adapter.Ok && adapter.IsUnder(adapterDir))
        {
            Console.WriteLine($"natural voices: {adapter.Detail}");
            return;
        }

        if (adapter.Ok)
        {
            Console.WriteLine($"natural voices: registered from {adapter.X64Path}");
            Console.WriteLine("re-registering from the new folder...");
        }

        if (!assumeYes &&
            !Confirm("Register the natural voice adapter (one UAC prompt)?", defaultYes: true))
        {
            Console.WriteLine("natural voices: skipped");
            return;
        }

        if (await RegisterAdapterAsync(installDir, cancellationToken))
        {
            Console.WriteLine($"natural voices: {AdapterProbe.Get().Detail}");
        }
        else
        {
            Console.WriteLine(
                "natural voices: registration did not complete; run scripts\\install-adapter.ps1 later");
        }
    }

    private static async Task<bool> RegisterAdapterAsync(
        string installDir,
        CancellationToken cancellationToken)
    {
        var script = Path.Combine(installDir, "scripts", "install-adapter.ps1");
        if (!File.Exists(script))
        {
            Console.Error.WriteLine($"adapter installer not found: {script}");
            return false;
        }

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            WorkingDirectory = installDir
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("-AdapterDir");
        startInfo.ArgumentList.Add(Path.Combine(installDir, "tools", "NaturalVoiceSAPIAdapter"));

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine("could not start powershell.exe.");
            return false;
        }

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine(
                $"adapter registration exited with code {process.ExitCode}.");
            return false;
        }

        return AdapterProbe.Get().Ok;
    }

    private static void StepPath(string installDir, bool skip)
    {
        if (skip)
        {
            Console.WriteLine("PATH:     skipped");
            return;
        }

        var update = UserEnvironment.AddToUserPath(installDir);
        if (!update.Changed)
        {
            Console.WriteLine("PATH:     already contains this folder");
        }
        else
        {
            if (update.Added)
            {
                Console.WriteLine($"PATH:     added {installDir}");
            }
            else if (update.RemovedStale)
            {
                Console.WriteLine($"PATH:     removed a stale entry and kept {installDir}");
            }
            else
            {
                Console.WriteLine($"PATH:     refreshed the entry for {installDir}");
            }

            if (update.BackupFile is not null)
            {
                Console.WriteLine($"PATH:     previous value saved to {update.BackupFile}");
            }
        }

        if (UserEnvironment.IsOnPath("winTTSas.exe", installDir))
        {
            Console.WriteLine("PATH:     verified in a child process");
        }
        else
        {
            Console.WriteLine(
                "PATH:     could not verify in a child process; open a new terminal and run: winTTSas version");
        }
    }

    private static async Task StepServerAsync(
        string installDir,
        bool skipLogon,
        bool assumeYes,
        bool noStart,
        CancellationToken cancellationToken)
    {
        var serverExe = Path.Combine(installDir, "winTTSas-server.exe");
        var logonWasEnabled = IsLogonEnabled();

        // A failure to start the server must not abort the rest of the install:
        // PATH, the shortcut, and the voice are still worth setting up.
        try
        {
            var enableLogon = !skipLogon &&
                (logonWasEnabled || assumeYes ||
                 Confirm("Start winTTSas at login?", defaultYes: true));

            if (enableLogon)
            {
                if (await LogonAsync(
                        ["on"],
                        cancellationToken,
                        serverExe,
                        startNow: !noStart) != 0)
                {
                    Console.WriteLine("warning: could not start the server; run: winTTSas start");
                }

                return;
            }

            if (logonWasEnabled && skipLogon)
            {
                await LogonAsync(["off"], cancellationToken);
            }

            if (!noStart && await StartAsync(["--quiet"], cancellationToken, serverExe) != 0)
            {
                Console.WriteLine("warning: could not start the server; run: winTTSas start");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"warning: could not start the server: {ex.Message}");
            Console.WriteLine("run: winTTSas start");
        }
    }

    private static async Task StepVoiceAsync(
        bool skip,
        bool assumeYes,
        CancellationToken cancellationToken)
    {
        if (skip || assumeYes)
        {
            var config = ConfigStore.Load();
            var voice = VoiceCatalog.Load().Resolve(config.DefaultVoice, config);
            Console.WriteLine($"voice:    {voice?.Name ?? "none"}");
            return;
        }

        Console.WriteLine();
        if (await SelectAsync([], cancellationToken) != 0)
        {
            Console.WriteLine("voice:    keeping the current default");
        }
    }

    private static string ResolveInstallDir(string? requested, bool assumeYes)
    {
        var previous = ShellIntegration.ReadInstallDir();
        var fallback = string.IsNullOrWhiteSpace(previous)
            ? DefaultInstallDir
            : UserEnvironment.NormalizeDirectory(previous);

        if (!string.IsNullOrWhiteSpace(requested))
        {
            var value = UserEnvironment.NormalizeDirectory(requested);
            if (!IsUsableInstallDir(value, out var error))
            {
                throw new InvalidOperationException(error);
            }

            return value;
        }

        if (assumeYes)
        {
            return fallback;
        }

        while (true)
        {
            Console.Write($"Install folder [{fallback}]: ");
            var input = Console.ReadLine();
            var value = string.IsNullOrWhiteSpace(input)
                ? fallback
                : UserEnvironment.NormalizeDirectory(input.Trim().Trim('"'));

            if (IsUsableInstallDir(value, out var error))
            {
                return value;
            }

            Console.Error.WriteLine("error: " + error);
        }
    }

    private static bool IsUsableInstallDir(string directory, out string error)
    {
        error = "";
        var root = Path.GetPathRoot(directory);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(
                directory.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            error = $"refusing to install into a drive root: {directory}";
            return false;
        }

        if (File.Exists(directory))
        {
            error = $"{directory} is a file.";
            return false;
        }

        return true;
    }

    private static long CopyRelease(string sourceDir, string targetDir)
    {
        long copied = 0;

        foreach (var file in Directory.EnumerateFiles(
                     sourceDir,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            if (relative.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.Combine(targetDir, relative);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
                copied += new FileInfo(destination).Length;
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"could not copy {relative}: {ex.Message}", ex);
            }
        }

        return copied;
    }

    private static int EstimateSizeKb(string directory)
    {
        try
        {
            var total = Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(file =>
                {
                    try
                    {
                        return new FileInfo(file).Length;
                    }
                    catch
                    {
                        return 0L;
                    }
                });

            return (int)Math.Min(int.MaxValue, total / 1024);
        }
        catch
        {
            return 0;
        }
    }

    private static bool Confirm(string question, bool defaultYes)
    {
        if (Console.IsInputRedirected)
        {
            return defaultYes;
        }

        Console.Write($"{question} [{(defaultYes ? "Y/n" : "y/N")}] ");
        var answer = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(answer))
        {
            return defaultYes;
        }

        return answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
               answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadOptionValue(string[] args, string option)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (TryReadOption(args, ref i, option, out var value))
            {
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }
}
