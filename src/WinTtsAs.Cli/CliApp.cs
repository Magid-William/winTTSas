using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;
using WinTtsAs.Core;

namespace WinTtsAs.Cli;

public static partial class CliApp
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
                "sample" => await SampleAsync(rest, cancellationToken),
                "status" => await StatusAsync(rest, cancellationToken),
                "list" => List(rest),
                "select" => await SelectAsync(rest, cancellationToken),
                "volume" or "gain" => await VolumeAsync(rest, cancellationToken),
                "speed" => await SpeedAsync(rest, cancellationToken),
                "pitch" => await PitchAsync(rest, cancellationToken),
                "normalize" or "loudnorm" => await NormalizeAsync(rest, cancellationToken),
                "port" => await PortAsync(rest, cancellationToken),
                "format" => await FormatAsync(rest, cancellationToken),
                "sanitize" => await SanitizeAsync(rest, cancellationToken),
                "markdown" => await MarkdownAsync(rest, cancellationToken),
                "selectedtext" or "selection" => await HotkeyCommandAsync(HotkeyTarget.SelectedText, rest, cancellationToken),
                "clipboard" => await HotkeyCommandAsync(HotkeyTarget.Clipboard, rest, cancellationToken),
                "logon" => await LogonAsync(rest, cancellationToken),
                "setup" or "install" => await SetupAsync(rest, cancellationToken),
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
  winTTSas sample [options] [text]
  winTTSas sample --devices
  winTTSas volume [dB]
  winTTSas status [--json]
  winTTSas list [--json]
  winTTSas select <index|name|slug>
  winTTSas speed [0.25-4.0]
  winTTSas pitch [-12..+12]
  winTTSas normalize [on|off]
  winTTSas port [port]
  winTTSas format [mp3|wav|opus|aac|flac|pcm]
  winTTSas sanitize [on|off|--test <text>]
  winTTSas markdown [auto|on|off]
  winTTSas selectedText [on|off|change [shortcut]]
  winTTSas clipboard [on|off|change [shortcut]]
  winTTSas logon on|off
  winTTSas setup [--yes] [--dir <path>] [--portable] [--configure]
                 [--skip-adapter] [--no-logon] [--skip-path]
                 [--skip-voice] [--skip-test] [--no-start] [--open]
  winTTSas uninstall [--yes] [--keep-data] [--keep-files] [--remove-adapter]
  winTTSas help

OpenAI clients:
  Base URL: http://127.0.0.1:5178/v1
  API key:  local (or the configured token)
  Model:    tts-1
  Voice:    alloy

Notes:
  - start runs a background server; --foreground keeps it in this console.
  - format applies live; port restarts the server.
  - logon uses HKCU Run and starts the hidden server at login.
  - selectedText and clipboard set global read-aloud hotkeys on the server.
  - setup copies the app to %LOCALAPPDATA%\Programs\winTTSas, adds it to PATH,
    registers natural voices, enables logon, and adds shortcuts. --portable
    keeps the current folder instead of copying. --configure only configures
    the copy the MSI installed: PATH, voices, logon, voice, and the server.
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
        CancellationToken cancellationToken,
        string? serverExeOverride = null)
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

        var serverExe = serverExeOverride ?? ResolveServerExe();
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

        // A freshly extracted release carries the Mark-of-the-Web, which makes
        // the shell refuse to run it ("The operation was canceled by the user").
        ShellIntegration.UnblockFile(serverExe);

        var startInfo = new ProcessStartInfo(serverExe)
        {
            // ShellExecute avoids handing the hidden server the CLI's stdout/stderr
            // pipe. Otherwise `winTTSas start | Out-Null` can wait forever for the
            // pipeline to close even after the CLI has exited.
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(serverExe)
                ?? AppContext.BaseDirectory
        };

        if (HasFlag(args, "--logon"))
        {
            startInfo.Arguments = "--logon";
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not start the server: {ex.Message}");
            Console.Error.WriteLine($"run it directly to see why: \"{serverExe}\"");
            return 1;
        }

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

    private static async Task<int> SampleAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var textParts = new List<string>();
        int? deviceIndex = null;
        double? speedOverride = null;
        double? gainOverride = null;
        double? pitchOverride = null;
        bool? normalizeOverride = null;
        int? levelOverride = null;
        string? voiceQuery = null;
        string? savePath = null;
        string? formatOverride = null;
        var raw = false;
        var parseOptions = true;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (!parseOptions)
            {
                textParts.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                parseOptions = false;
                continue;
            }

            if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                PrintSampleHelp();
                return 0;
            }

            if (arg.Equals("--devices", StringComparison.OrdinalIgnoreCase))
            {
                return PrintAudioDevices();
            }

            if (arg.Equals("--raw", StringComparison.OrdinalIgnoreCase))
            {
                raw = true;
                continue;
            }

            if (TryReadOption(args, ref i, "--device", out var deviceValue))
            {
                if (!int.TryParse(deviceValue, out var parsedDevice) || parsedDevice < 0)
                {
                    return SampleUsageError("--device <index>");
                }

                deviceIndex = parsedDevice;
                continue;
            }

            if (TryReadOption(args, ref i, "--speed", out var speedValue))
            {
                if (!double.TryParse(
                        speedValue,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var parsedSpeed) ||
                    !double.IsFinite(parsedSpeed) ||
                    parsedSpeed is < 0.25 or > 4.0)
                {
                    return SampleUsageError("--speed <0.25-4.0>");
                }

                speedOverride = parsedSpeed;
                continue;
            }

            if (TryReadOption(args, ref i, "--volume", out var gainValue) ||
                TryReadOption(args, ref i, "--gain", out gainValue))
            {
                if (!double.TryParse(
                        gainValue,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var parsedGain) ||
                    !double.IsFinite(parsedGain) ||
                    parsedGain is < -24.0 or > 24.0)
                {
                    return SampleUsageError("--volume <dB> or --gain <dB>");
                }

                gainOverride = Math.Round(parsedGain, 1);
                continue;
            }

            if (TryReadOption(args, ref i, "--level", out var levelValue))
            {
                if (!int.TryParse(levelValue, out var parsedLevel) ||
                    parsedLevel is < 0 or > 100)
                {
                    return SampleUsageError("--level <0-100>");
                }

                levelOverride = parsedLevel;
                continue;
            }

            if (arg.Equals("--normalize", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--no-normalize", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--normalize=", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--no-normalize=", StringComparison.OrdinalIgnoreCase))
            {
                var enabledByFlag = arg.StartsWith("--normalize", StringComparison.OrdinalIgnoreCase);
                string? value = null;
                var equals = arg.IndexOf('=');
                if (equals >= 0)
                {
                    value = arg[(equals + 1)..];
                }
                else if (i + 1 < args.Length && TryParseOnOff(args[i + 1], out _))
                {
                    value = args[++i];
                }

                if (value is null)
                {
                    normalizeOverride = enabledByFlag;
                }
                else if (!TryParseOnOff(value, out var parsedNormalize))
                {
                    return SampleUsageError("--normalize [on|off]");
                }
                else
                {
                    normalizeOverride = parsedNormalize;
                }

                continue;
            }

            if (TryReadOption(args, ref i, "--pitch", out var pitchValue))
            {
                if (!double.TryParse(
                        pitchValue,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var parsedPitch) ||
                    !double.IsFinite(parsedPitch) ||
                    parsedPitch is < -12.0 or > 12.0)
                {
                    return SampleUsageError("--pitch <-12..12>");
                }

                pitchOverride = Math.Round(parsedPitch, 1);
                continue;
            }

            if (TryReadOption(args, ref i, "--voice", out var voiceValue))
            {
                if (string.IsNullOrWhiteSpace(voiceValue))
                {
                    return SampleUsageError("--voice <index|name|slug>");
                }

                voiceQuery = voiceValue;
                continue;
            }

            if (TryReadOption(args, ref i, "--save", out var saveValue) ||
                TryReadOption(args, ref i, "-o", out saveValue))
            {
                if (string.IsNullOrWhiteSpace(saveValue))
                {
                    return SampleUsageError("--save <path>");
                }

                savePath = saveValue;
                continue;
            }

            if (TryReadOption(args, ref i, "--format", out var formatValue))
            {
                if (string.IsNullOrWhiteSpace(formatValue) ||
                    !AudioFormats.IsSupported(formatValue))
                {
                    return SampleUsageError(
                        "--format <mp3|wav|opus|aac|flac|pcm>",
                        $"unsupported format: {formatValue}");
                }

                formatOverride = AudioFormats.Normalize(formatValue);
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                return SampleUsageError(null, $"unknown option: {arg}");
            }

            textParts.Add(arg);
        }

        var config = ConfigStore.Load();
        var catalog = VoiceCatalog.Load();

        if (formatOverride is not null && savePath is null)
        {
            return SampleUsageError(null, "--format requires --save.");
        }

        if (deviceIndex is not null && savePath is not null)
        {
            return SampleUsageError(null, "--device cannot be used with --save.");
        }

        VoiceDescriptor? voice;
        if (string.IsNullOrWhiteSpace(voiceQuery))
        {
            voice = catalog.Resolve(config.DefaultVoice, config);
        }
        else
        {
            voice = null;

            if (int.TryParse(voiceQuery, out var voiceIndex) &&
                voiceIndex >= 1 &&
                voiceIndex <= catalog.Voices.Count)
            {
                voice = catalog.Voices[voiceIndex - 1];
            }

            voice ??= catalog.FindExact(voiceQuery);

            if (voice is null)
            {
                Console.Error.WriteLine($"voice not found: {voiceQuery}");
                Console.Error.WriteLine("run: winTTSas list");
                return 1;
            }
        }

        if (voice is null)
        {
            Console.Error.WriteLine("no SAPI voices are installed.");
            return 1;
        }

        string? outputPath = null;
        string? outputFormat = null;
        if (savePath is not null)
        {
            outputPath = Path.GetFullPath(savePath);
            var extension = Path.GetExtension(outputPath).TrimStart('.');
            outputFormat = formatOverride ??
                (AudioFormats.IsSupported(extension)
                    ? AudioFormats.Normalize(extension)
                    : config.DefaultFormat);
        }

        if (outputPath is not null)
        {
            Console.WriteLine($"file:   {outputPath}");
            Console.WriteLine($"format: {outputFormat}");
        }
        else if (deviceIndex is not null)
        {
            var devices = WaveOutPlayer.GetDevices();
            var selectedDevice = devices.FirstOrDefault(d => d.Index == deviceIndex.Value);
            if (selectedDevice is null)
            {
                Console.Error.WriteLine($"audio device {deviceIndex} not found.");
                Console.Error.WriteLine("run: winTTSas sample --devices");
                return 1;
            }

            Console.WriteLine($"output: {selectedDevice.Name}");
        }
        else
        {
            Console.WriteLine("output: system default");
        }

        var text = string.Join(" ", textParts).Trim();
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

        var spoken = raw ? text : TextSanitizer.Apply(text, config);
        if (spoken.Length == 0)
        {
            Console.Error.WriteLine(
                "error: sanitized text is empty; use --raw or adjust sanitization.");
            return 1;
        }

        if (spoken.Length > config.MaxTextLength)
        {
            Console.Error.WriteLine(
                $"sanitized sample text must be {config.MaxTextLength} characters or fewer.");
            return 1;
        }

        var speed = speedOverride ?? config.DefaultSpeed;
        var gainDb = gainOverride ?? config.GainDb;
        var pitchSemitones = pitchOverride ?? config.PitchSemitones;
        var normalize = normalizeOverride ?? config.NormalizeLoudness;
        var level = levelOverride ?? config.Volume;
        var rate = SpeechRate.FromSpeed(speed);

        Console.WriteLine($"voice:  {voice.Name}");
        Console.WriteLine($"speed:  {speed:0.00} (SAPI rate {rate})");
        Console.WriteLine($"gain:   {FormatGain(gainDb)} dB");
        Console.WriteLine($"pitch:  {FormatPitch(pitchSemitones)} semitones");
        Console.WriteLine($"normal: {(normalize ? "on" : "off")}");
        Console.WriteLine($"level:  {level}");
        if (!string.Equals(text, spoken, StringComparison.Ordinal))
        {
            Console.WriteLine($"input:  {text}");
        }

        Console.WriteLine($"text:   {spoken}");
        Console.WriteLine();
        Console.WriteLine(outputPath is null ? "speaking..." : "saving...");

        var wav = SpeechSampler.SynthesizeWav(
            voice.Name,
            spoken,
            rate,
            level);

        if (outputPath is not null)
        {
            var saveFormat = outputFormat ?? config.DefaultFormat;
            var needsFfmpeg = saveFormat != "wav" ||
                Math.Abs(gainDb) >= 0.05 ||
                Math.Abs(pitchSemitones) >= 0.05 ||
                normalize;
            byte[] audio;

            if (!needsFfmpeg)
            {
                audio = wav;
            }
            else
            {
                var ffmpeg = new FfmpegTranscoder(config.FfmpegPath, config.Mp3Bitrate);
                if (!ffmpeg.IsAvailable)
                {
                    Console.Error.WriteLine(
                        $"error: ffmpeg is required for {saveFormat} output, pitch, gain, " +
                        "or loudness normalization.");
                    return 1;
                }

                try
                {
                    audio = await ffmpeg.ToFormatAsync(
                        wav,
                        saveFormat,
                        gainDb,
                        pitchSemitones,
                        normalize,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("error: audio conversion failed: " + ex.Message);
                    return 1;
                }
            }

            try
            {
                var parent = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                await File.WriteAllBytesAsync(outputPath, audio, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: could not write {outputPath}: {ex.Message}");
                return 1;
            }

            Console.WriteLine($"saved:  {outputPath} ({audio.Length:N0} bytes)");
            return 0;
        }

        if (Math.Abs(gainDb) >= 0.05 ||
            Math.Abs(pitchSemitones) >= 0.05 ||
            normalize)
        {
            var ffmpeg = new FfmpegTranscoder(config.FfmpegPath, config.Mp3Bitrate);
            if (!ffmpeg.IsAvailable)
            {
                Console.Error.WriteLine(
                    "error: ffmpeg is required for pitch, gain, or loudness normalization.");
                return 1;
            }

            try
            {
                wav = await ffmpeg.ToFormatAsync(
                    wav,
                    "wav",
                    gainDb,
                    pitchSemitones,
                    normalize,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: audio processing failed: " + ex.Message);
                return 1;
            }
        }

        WaveOutPlayer.Play(wav, deviceIndex);

        Console.WriteLine("done.");
        return 0;
    }

    private static bool TryReadOption(
        string[] args,
        ref int index,
        string option,
        out string? value)
    {
        var arg = args[index];

        if (arg.Equals(option, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= args.Length)
            {
                value = null;
                return true;
            }

            value = args[++index];
            return true;
        }

        var prefix = option + "=";
        if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = arg[prefix.Length..];
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryParseOnOff(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "on" or "true" or "yes" or "1":
                result = true;
                return true;
            case "off" or "false" or "no" or "0":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static int PrintAudioDevices()
    {
        var devices = WaveOutPlayer.GetDevices();
        if (devices.Count == 0)
        {
            Console.Error.WriteLine("no audio output devices found.");
            return 1;
        }

        foreach (var item in devices)
        {
            Console.WriteLine($"{item.Index,2}  {item.Name}");
        }

        return 0;
    }

    private static int SampleUsageError(string? usage, string? message = null)
    {
        if (message is not null)
        {
            Console.Error.WriteLine(message);
        }

        Console.Error.WriteLine("usage: winTTSas sample [options] [text]");
        if (usage is not null)
        {
            Console.Error.WriteLine($"  {usage}");
        }

        Console.Error.WriteLine("run: winTTSas sample --help");
        return 1;
    }

    private static void PrintSampleHelp()
    {
        Console.WriteLine("""
Usage: winTTSas sample [options] [text]

Options:
  --speed <0.25-4.0>       Override speed for this sample
  --volume <dB>            Override gain in dB (alias: --gain)
  --pitch <-12..12>        Shift pitch in semitones for this sample
  --normalize [on|off]     Loudness normalization for this sample
  --no-normalize           Shorthand for --normalize off
  --level <0-100>          Override SAPI volume
  --voice <index|name|slug>  Select a voice
  --save <path>            Write audio to a file instead of playing (alias: -o)
  --format <format>        Output format for --save; inferred from the extension
  --raw                    Skip markdown and symbol sanitization
  --device <index>         Play on a specific output device
  --devices                List audio output devices
  --help                   Show this help
  --                       Stop parsing options; the rest is text
""");
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
        var server = await ReloadAsync(cancellationToken);

        Console.WriteLine($"Default voice set to {selected.Name}.");
        if (server is not null)
        {
            Console.WriteLine($"Server voice: {server.Voice}");
        }

        return 0;
    }

    private static async Task<int> VolumeAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var config = ConfigStore.Load();

        if (args.Length == 0)
        {
            Console.WriteLine($"{FormatGain(config.GainDb)} dB");
            return 0;
        }

        if (!double.TryParse(
                args[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var gain) ||
            !double.IsFinite(gain) ||
            gain is < -24 or > 24)
        {
            Console.Error.WriteLine("gain must be between -24 and +24 dB.");
            return 1;
        }

        config.GainDb = Math.Round(gain, 1);
        ConfigStore.Save(config);
        var server = await ReloadAsync(cancellationToken);

        Console.WriteLine($"Gain set to {FormatGain(config.GainDb)} dB.");
        if (server is not null)
        {
            Console.WriteLine($"Server gain: {FormatGain(server.GainDb)} dB.");
        }

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
            !double.IsFinite(speed) ||
            speed is < 0.25 or > 4.0)
        {
            Console.Error.WriteLine("speed must be between 0.25 and 4.0.");
            return 1;
        }

        config.DefaultSpeed = speed;
        ConfigStore.Save(config);
        var server = await ReloadAsync(cancellationToken);

        Console.WriteLine($"Default speed set to {speed:0.00}.");
        if (server is not null)
        {
            Console.WriteLine($"Server speed: {server.Speed:0.00} (SAPI rate {server.Rate}).");
        }

        return 0;
    }

    private static async Task<int> PitchAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var config = ConfigStore.Load();

        if (args.Length == 0)
        {
            Console.WriteLine(FormatPitch(config.PitchSemitones));
            return 0;
        }

        if (!double.TryParse(
                args[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var pitch) ||
            !double.IsFinite(pitch) ||
            pitch is < -12.0 or > 12.0)
        {
            Console.Error.WriteLine("pitch must be between -12 and +12 semitones.");
            return 1;
        }

        config.PitchSemitones = Math.Round(pitch, 1);
        ConfigStore.Save(config);
        var server = await ReloadAsync(cancellationToken);

        Console.WriteLine($"Default pitch set to {FormatPitch(config.PitchSemitones)} semitones.");
        if (server is not null)
        {
            Console.WriteLine($"Server pitch: {FormatPitch(server.PitchSemitones)} semitones.");
        }

        return 0;
    }

    private static async Task<int> NormalizeAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var config = ConfigStore.Load();

        if (args.Length == 0)
        {
            Console.WriteLine(config.NormalizeLoudness ? "on" : "off");
            return 0;
        }

        var mode = args[0].Trim().ToLowerInvariant();
        if (mode is not ("on" or "off"))
        {
            Console.Error.WriteLine("usage: winTTSas normalize [on|off]");
            return 1;
        }

        config.NormalizeLoudness = mode == "on";
        ConfigStore.Save(config);
        var server = await ReloadAsync(cancellationToken);

        Console.WriteLine($"Loudness normalization {mode}.");
        if (server is not null)
        {
            Console.WriteLine($"Server normalization: {(server.Normalize ? "on" : "off")}.");
        }

        return 0;
    }

    private static async Task<int> SanitizeAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var config = ConfigStore.Load();

        if (args.Length == 0)
        {
            PrintSanitize(config);
            return 0;
        }

        var first = args[0];
        if (first.Equals("--test", StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith("--test=", StringComparison.OrdinalIgnoreCase))
        {
            var text = first.StartsWith("--test=", StringComparison.OrdinalIgnoreCase)
                ? first["--test=".Length..]
                : string.Join(" ", args.Skip(1));

            if (string.IsNullOrWhiteSpace(text))
            {
                Console.Error.WriteLine("usage: winTTSas sanitize --test <text>");
                return 1;
            }

            Console.WriteLine(TextSanitizer.Apply(text, config));
            return 0;
        }

        var mode = first.ToLowerInvariant();
        if (mode is "on" or "off")
        {
            config.Sanitize = mode == "on";
            ConfigStore.Save(config);
            await ReloadAsync(cancellationToken);
            Console.WriteLine($"Sanitization {mode}.");
            return 0;
        }

        Console.Error.WriteLine("usage: winTTSas sanitize [on|off|--test <text>]");
        return 1;
    }

    private static void PrintSanitize(AppConfig config)
    {
        Console.WriteLine($"sanitize: {(config.Sanitize ? "on" : "off")}");
        Console.WriteLine($"markdown: {config.Markdown}");
        Console.WriteLine($"url:      {config.UrlMode}");
        Console.WriteLine($"code:     {config.CodeMode}");
        Console.WriteLine($"inline:   {config.InlineCodeMode}");
        Console.WriteLine($"tables:   {config.TableMode}");
        Console.WriteLine($"replaced: {config.TextReplacements.Count} custom");
    }

    private static async Task<int> MarkdownAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var config = ConfigStore.Load();

        if (args.Length == 0)
        {
            Console.WriteLine(config.Markdown);
            return 0;
        }

        var mode = args[0].Trim().ToLowerInvariant();
        if (mode is not ("auto" or "on" or "off"))
        {
            Console.Error.WriteLine("markdown must be auto, on, or off.");
            return 1;
        }

        config.Markdown = mode;
        ConfigStore.Save(config);
        await ReloadAsync(cancellationToken);
        Console.WriteLine($"Markdown detection set to {mode}.");
        return 0;
    }

    private enum HotkeyTarget
    {
        SelectedText,
        Clipboard
    }

    private static async Task<int> HotkeyCommandAsync(
        HotkeyTarget target,
        string[] args,
        CancellationToken cancellationToken)
    {
        var config = ConfigStore.Load();
        var settings = HotkeySettingsFor(config, target);
        var label = target == HotkeyTarget.SelectedText ? "selected text" : "clipboard";
        var command = target == HotkeyTarget.SelectedText ? "selectedText" : "clipboard";

        if (args.Length == 0)
        {
            var current = await ControlPipeClient.GetStatusAsync(500, cancellationToken);
            PrintHotkeyState(target, config, current);
            return 0;
        }

        var action = args[0].Trim().ToLowerInvariant();

        if (action is "on" or "off")
        {
            settings.Enabled = action == "on";
            ConfigStore.Save(config);

            var status = await ReloadAsync(cancellationToken);
            PrintHotkeyState(target, config, status);

            if (status is null)
            {
                Console.WriteLine("Start the server so the shortcut works: winTTSas start");
            }

            return 0;
        }

        if (action is "change")
        {
            HotkeyChord? chord = null;

            if (args.Length > 1)
            {
                var text = string.Join(" ", args.Skip(1));
                if (!HotkeyChord.TryParse(text, out chord, out var error))
                {
                    Console.Error.WriteLine("error: " + error);
                    return 1;
                }
            }
            else
            {
                if (Console.IsInputRedirected)
                {
                    Console.Error.WriteLine($"usage: winTTSas {command} change <shortcut>");
                    return 1;
                }

                Console.WriteLine($"Current: {settings.ActiveChord ?? settings.Shortcut}");
                Console.WriteLine($"Press the new shortcut for {label} (Esc cancels)...");

                var captured = HotkeyCapture.Capture(TimeSpan.FromSeconds(20), cancellationToken);
                if (captured.Chord is null)
                {
                    if (captured.Error is not null)
                    {
                        Console.Error.WriteLine("error: " + captured.Error);
                        return 1;
                    }

                    Console.WriteLine("cancelled.");
                    return 0;
                }

                chord = captured.Chord;
            }

            settings.Enabled = true;
            settings.Shortcut = chord!.Display;
            ConfigStore.Save(config);

            var status = await ReloadAsync(cancellationToken);
            Console.WriteLine($"{label} shortcut set to {chord.Display}.");
            PrintHotkeyState(target, config, status);

            if (status is null)
            {
                Console.WriteLine("Start the server so the shortcut works: winTTSas start");
            }

            return 0;
        }

        Console.Error.WriteLine($"usage: winTTSas {command} [on|off|change [shortcut]]");
        return 1;
    }

    private static HotkeyBindingSettings HotkeySettingsFor(AppConfig config, HotkeyTarget target) =>
        target == HotkeyTarget.SelectedText
            ? config.Hotkeys.SelectedText
            : config.Hotkeys.Clipboard;

    private static void PrintHotkeyState(
        HotkeyTarget target,
        AppConfig config,
        StatusInfo? status)
    {
        var settings = HotkeySettingsFor(config, target);
        var chord = settings.ActiveChord ?? settings.Shortcut;
        var state = settings.Enabled ? "on" : "off";
        var suffix = "";

        if (settings.Enabled)
        {
            suffix = status is null
                ? ", server not running"
                : string.IsNullOrEmpty(status.HotkeyError)
                    ? ", active"
                    : ", unavailable";
        }

        var label = target == HotkeyTarget.SelectedText ? "selected text" : "clipboard";
        Console.WriteLine($"{label}: {chord} ({state}{suffix})");

        if (status is not null && !string.IsNullOrEmpty(status.HotkeyError))
        {
            Console.Error.WriteLine("warning: " + status.HotkeyError);
        }
    }

    private static string DescribeHotkey(HotkeyBindingSettings settings) =>
        !settings.Enabled ? "off" : settings.ActiveChord ?? settings.Shortcut;

    private static async Task<int> PortAsync(
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

    private static async Task<int> FormatAsync(
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
        var server = await ReloadAsync(cancellationToken);

        Console.WriteLine($"Default format set to {config.DefaultFormat}.");
        if (server is not null)
        {
            Console.WriteLine($"Server format: {server.Format}.");
        }

        return 0;
    }

    private static async Task<int> LogonAsync(
        string[] args,
        CancellationToken cancellationToken,
        string? serverExeOverride = null,
        bool startNow = true)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        if (sub == "on")
        {
            var serverExe = serverExeOverride ?? ResolveServerExe();
            if (!File.Exists(serverExe))
            {
                Console.Error.WriteLine($"server executable not found: {serverExe}");
                return 1;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key?.SetValue(RunValueName, $"\"{serverExe}\" --logon");
            Console.WriteLine("Logon startup enabled.");

            if (!startNow)
            {
                return 0;
            }

            if (await ControlPipeClient.GetStatusAsync(300, cancellationToken) is null)
            {
                return await StartAsync(["--quiet"], cancellationToken, serverExe);
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
        var keepFiles = HasFlag(args, "--keep-files");
        var removeAdapter = HasFlag(args, "--remove-adapter");

        var installDir = ShellIntegration.ReadInstallDir();
        if (string.IsNullOrWhiteSpace(installDir))
        {
            installDir = AppContext.BaseDirectory;
        }

        installDir = UserEnvironment.NormalizeDirectory(installDir);

        if (!yes)
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("non-interactive uninstall requires --yes.");
                return 1;
            }

            var parts = new List<string> { "stop the server", "remove the PATH entry, logon, and shortcuts" };
            if (!keepData)
            {
                parts.Add("delete config/cache/logs");
            }

            if (!keepFiles)
            {
                parts.Add($"delete {installDir}");
            }

            Console.Write($"This will {string.Join(", ", parts)}. Continue? [y/N] ");
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

        var pathUpdate = UserEnvironment.RemoveFromUserPath(installDir);
        Console.WriteLine(pathUpdate.Changed
            ? "Removed the PATH entry."
            : "No PATH entry pointed at this folder.");

        ShellIntegration.Uninstall();
        Console.WriteLine("Removed the Start Menu shortcut and Apps & Features entry.");

        if (!keepData)
        {
            DeleteDirectory(AppPaths.ConfigDir);
            DeleteDirectory(AppPaths.LocalDir);
        }

        if (removeAdapter)
        {
            UnregisterAdapter();
        }

        if (keepFiles)
        {
            Console.WriteLine("Kept the program files.");
        }
        else if (TryDeleteInstallDir(installDir, out var reason, out var inUse))
        {
            Console.WriteLine($"Deleted {installDir}.");
        }
        else if (inUse)
        {
            Console.WriteLine("Folder is in use; removing it after this process exits.");
            ScheduleDeferredDelete(installDir);
        }
        else
        {
            Console.WriteLine($"Could not delete {installDir}: {reason}");
            Console.WriteLine("Remove it manually when convenient.");
        }

        Console.WriteLine("winTTSas uninstalled.");
        return 0;
    }

    private static bool TryDeleteInstallDir(string directory, out string reason, out bool inUse)
    {
        reason = "";
        inUse = false;
        var full = UserEnvironment.NormalizeDirectory(directory);
        var root = Path.GetPathRoot(full);

        if (string.IsNullOrWhiteSpace(full) ||
            (!string.IsNullOrEmpty(root) &&
             string.Equals(
                 full.TrimEnd(Path.DirectorySeparatorChar),
                 root.TrimEnd(Path.DirectorySeparatorChar),
                 StringComparison.OrdinalIgnoreCase)))
        {
            reason = "refusing to delete a drive root";
            return false;
        }

        var protectedDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };

        if (protectedDirs.Any(path =>
                !string.IsNullOrWhiteSpace(path) &&
                string.Equals(
                    full,
                    UserEnvironment.NormalizeDirectory(path),
                    StringComparison.OrdinalIgnoreCase)))
        {
            reason = "refusing to delete a protected folder";
            return false;
        }

        if (!File.Exists(Path.Combine(full, "winTTSas.exe")))
        {
            reason = "winTTSas.exe was not found there";
            return false;
        }

        try
        {
            Directory.Delete(full, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            inUse = ex is UnauthorizedAccessException or IOException;
            return false;
        }
    }

    private static void ScheduleDeferredDelete(string directory)
    {
        try
        {
            var literal = directory.Replace("'", "''");
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-WindowStyle");
            startInfo.ArgumentList.Add("Hidden");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                $"Start-Sleep -Seconds 3; Remove-Item -LiteralPath '{literal}' -Recurse -Force -ErrorAction SilentlyContinue");

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not schedule cleanup: {ex.Message}");
            Console.Error.WriteLine($"delete the folder manually: {directory}");
        }
    }

    private static async Task<StatusInfo?> ReloadAsync(CancellationToken cancellationToken)
    {
        var response = await ControlPipeClient.SendAsync(
            new ControlRequest { Cmd = "reload" },
            3000,
            cancellationToken);

        if (response is null)
        {
            var runtime = RuntimeStore.Read();
            if (runtime is not null && IsProcessAlive(runtime.Pid))
            {
                Console.Error.WriteLine(
                    $"warning: server process {runtime.Pid} is running but did not answer the reload.");
                Console.Error.WriteLine(
                    "restart it with: winTTSas stop; winTTSas start");
            }

            return null;
        }

        if (response is { Ok: false })
        {
            Console.Error.WriteLine("reload failed: " + response.Error);
            return null;
        }

        return response.Status;
    }

    private static void PrintStatus(StatusInfo status)
    {
        Console.WriteLine($"winTTSas {status.Version}");
        Console.WriteLine($"state:    running (pid {status.Pid})");
        Console.WriteLine($"base URL: {status.BaseUrl}");
        Console.WriteLine($"voice:    {status.Voice}");
        Console.WriteLine($"format:   {status.Format}");
        Console.WriteLine($"gain:     {FormatGain(status.GainDb)} dB");
        Console.WriteLine($"pitch:    {FormatPitch(status.PitchSemitones)} semitones");
        Console.WriteLine($"normalize: {(status.Normalize ? "on" : "off")}");
        Console.WriteLine($"speed:    {status.Speed:0.00} (SAPI rate {status.Rate})");
        Console.WriteLine($"voices:   {status.Voices} installed ({status.NaturalVoices} natural)");
        Console.WriteLine($"queue:    {status.Queue}/{status.QueueLimit}");
        Console.WriteLine($"workers:  {status.Workers}");
        Console.WriteLine($"uptime:   {FormatUptime(status.UptimeSeconds)}");
        Console.WriteLine($"logon:    {(IsLogonEnabled() ? "on" : "off")}");
        Console.WriteLine($"adapter:  {status.Adapter}");
        Console.WriteLine($"ffmpeg:   {status.Ffmpeg}");
        if (!string.IsNullOrWhiteSpace(status.FfmpegPath))
        {
            Console.WriteLine($"binary:   {status.FfmpegPath}");
        }
        Console.WriteLine($"sanitize: {(status.Sanitize ? "on" : "off")} (markdown {status.Markdown})");
        Console.WriteLine($"hotkeys:  {(string.IsNullOrEmpty(status.SelectedTextHotkey) ? "off" : status.SelectedTextHotkey)} (selection), {(string.IsNullOrEmpty(status.ClipboardHotkey) ? "off" : status.ClipboardHotkey)} (clipboard)");
        if (!string.IsNullOrEmpty(status.HotkeyError))
        {
            Console.WriteLine($"hotkey:   {status.HotkeyError}");
        }

        Console.WriteLine($"speech:   {(status.Speaking ? "speaking" : "idle")}");
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
        Console.WriteLine($"gain:     {FormatGain(config.GainDb)} dB");
        Console.WriteLine($"pitch:    {FormatPitch(config.PitchSemitones)} semitones");
        Console.WriteLine($"normalize: {(config.NormalizeLoudness ? "on" : "off")}");
        Console.WriteLine($"speed:    {config.DefaultSpeed:0.00} (SAPI rate {SpeechRate.FromSpeed(config.DefaultSpeed)})");
        Console.WriteLine($"voices:   {catalog.Voices.Count} installed ({catalog.NaturalCount} natural)");
        Console.WriteLine($"logon:    {(IsLogonEnabled() ? "on" : "off")}");
        Console.WriteLine($"adapter:  {adapter.Detail}");
        Console.WriteLine($"sanitize: {(config.Sanitize ? "on" : "off")} (markdown {config.Markdown})");
        Console.WriteLine($"hotkeys:  {DescribeHotkey(config.Hotkeys.SelectedText)} (selection), {DescribeHotkey(config.Hotkeys.Clipboard)} (clipboard)");
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

    private static string FormatGain(double gain) =>
        gain.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);

    private static string FormatPitch(double semitones) =>
        semitones.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);

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
