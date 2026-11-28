using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WinTtsAs.Core;

public static class ServerHost
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> RunAsync(
        string[] args,
        bool consoleLogging = false,
        CancellationToken cancellationToken = default)
    {
        var logon = args.Any(a => a.Equals("--logon", StringComparison.OrdinalIgnoreCase));
        var logFile = Path.Combine(
            AppPaths.LogDir,
            $"server-{DateTime.Now:yyyyMMdd}.log");

        AppConfig config;
        try
        {
            config = ConfigStore.Load();
        }
        catch (Exception ex)
        {
            AppendFatal(logFile, ex);
            return 1;
        }

        var existing = await ControlPipeClient.GetStatusAsync(400, cancellationToken);
        if (existing is not null)
        {
            return 0;
        }

        if (logon)
        {
            await Task.Delay(2000, cancellationToken);
        }

        WebApplication? app = null;
        SpeechService? speech = null;
        SpeechPlayer? player = null;
        SpeechActions? actions = null;
        HotkeyManager? hotkeys = null;
        ControlPipeServer? pipe = null;

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = AppContext.BaseDirectory
            });

            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Information);
            builder.Logging.AddProvider(new FileLoggerProvider(logFile));
            if (consoleLogging)
            {
                builder.Logging.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "HH:mm:ss ";
                });
            }

            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = 128 * 1024;
                options.Listen(ResolveBind(config.Bind), config.Port);
            });

            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                    policy
                        .AllowAnyOrigin()
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .WithExposedHeaders(
                            "X-WinTTSas-Voice",
                            "X-WinTTSas-Speed",
                            "X-WinTTSas-Rate",
                            "X-WinTTSas-Gain-Db",
                            "X-WinTTSas-Pitch",
                            "X-WinTTSas-Normalize",
                            "X-WinTTSas-Synth-Ms",
                            "X-WinTTSas-Cache"));
            });

            app = builder.Build();
            var logger = app.Logger;

            speech = new SpeechService(
                config,
                app.Services.GetRequiredService<ILogger<SpeechService>>());

            logger.LogInformation(
                "winTTSas {Version} starting: {Voices} voices ({Natural} natural), default={Voice}, port={Port}",
                BuildInfo.Version,
                speech.Voices.Count,
                speech.NaturalVoices,
                speech.DefaultVoice?.Name ?? "none",
                config.Port);

            if (speech.Voices.Count == 0)
            {
                logger.LogError("No SAPI voices are installed; cannot start.");
                return 4;
            }

            player = new SpeechPlayer(app.Services.GetRequiredService<ILogger<SpeechPlayer>>());
            actions = new SpeechActions(
                speech,
                player,
                app.Services.GetRequiredService<ILogger<SpeechActions>>());
            hotkeys = new HotkeyManager(
                actions.HandleAsync,
                app.Services.GetRequiredService<ILogger<HotkeyManager>>());

            speech.ConfigReloaded += reloaded => _ = ReapplyHotkeysAsync(hotkeys, reloaded, logger);
            await hotkeys.ApplyAsync(config);

            pipe = new ControlPipeServer(
                ControlPipeNames.ForCurrentUser(),
                request => HandleControlAsync(request, speech, hotkeys, actions),
                app.Lifetime,
                app.Services.GetRequiredService<ILogger<ControlPipeServer>>());

            await pipe.StartAsync(cancellationToken);

            app.UseCors();
            app.Use(async (context, next) =>
            {
                if (context.Request.Headers["Access-Control-Request-Private-Network"] == "true")
                {
                    context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
                }

                await next();
            });
            app.Use(async (context, next) =>
            {
                if (!IsProtectedPath(context.Request.Path) ||
                    IsAuthorized(context, speech.Config.Token))
                {
                    await next();
                    return;
                }

                await WriteError(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "Invalid or missing API key.",
                    "authentication_error");
            });

            MapEndpoints(app, speech, logger);

            await app.StartAsync(cancellationToken);

            RuntimeStore.Write(new RuntimeInfo
            {
                Pid = Environment.ProcessId,
                Port = config.Port,
                BaseUrl = AppPaths.OpenAiBaseUrl(config.Port, config.Bind),
                StartedAt = DateTimeOffset.Now,
                Version = BuildInfo.Version,
                LogFile = logFile
            });

            logger.LogInformation(
                "listening on {Url} (OpenAI base {OpenAiUrl})",
                AppPaths.BaseUrl(config.Port, config.Bind),
                AppPaths.OpenAiBaseUrl(config.Port, config.Bind));

            _ = Task.Run(async () =>
            {
                try
                {
                    await speech.WarmupAsync(CancellationToken.None);
                    logger.LogInformation("warm-up complete");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "warm-up failed; first request may be slower");
                }
            }, CancellationToken.None);

            await app.WaitForShutdownAsync(cancellationToken);
            return 0;
        }
        catch (ServerAlreadyRunningException ex)
        {
            app?.Logger.LogWarning(ex.Message);
            return 2;
        }
        catch (IOException ex)
        {
            app?.Logger.LogError(ex, "could not bind the HTTP port {Port}", config.Port);
            AppendFatal(logFile, ex);
            return 3;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            app?.Logger.LogError(ex, "fatal server error");
            AppendFatal(logFile, ex);
            return 1;
        }
        finally
        {
            RuntimeStore.Delete();

            if (pipe is not null)
            {
                await pipe.DisposeAsync();
            }

            if (hotkeys is not null)
            {
                await hotkeys.DisposeAsync();
            }

            if (actions is not null)
            {
                actions.Dispose();
            }

            if (player is not null)
            {
                player.Dispose();
            }

            if (speech is not null)
            {
                await speech.DisposeAsync();
            }

            app?.Logger.LogInformation("winTTSas stopped");

            if (app is not null)
            {
                await app.DisposeAsync();
            }
        }
    }

    private static void MapEndpoints(
        WebApplication app,
        SpeechService speech,
        ILogger logger)
    {
        app.MapGet("/", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Content(TestPage.Html, "text/html; charset=utf-8");
        });

        app.MapGet("/health", () => Results.Json(new
        {
            ok = true,
            version = BuildInfo.Version,
            pid = Environment.ProcessId,
            port = speech.Config.Port,
            baseUrl = AppPaths.OpenAiBaseUrl(speech.Config.Port, speech.Config.Bind),
            voice = speech.DefaultVoice?.Name,
            format = speech.Config.DefaultFormat,
            gainDb = speech.Config.GainDb,
            pitch = speech.Config.PitchSemitones,
            normalize = speech.Config.NormalizeLoudness,
            speed = speech.Config.DefaultSpeed,
            rate = SpeechRate.FromSpeed(speech.Config.DefaultSpeed),
            voices = speech.Voices.Count,
            naturalVoices = speech.NaturalVoices,
            queue = speech.QueueCount,
            workers = speech.Workers,
            ffmpeg = speech.FfmpegAvailable
        }));

        app.MapGet("/healthz", () => Results.Json(new { ok = true }));

        app.MapGet("/v1/models", () => Results.Json(new
        {
            @object = "list",
            data = new object[]
            {
                new { id = "tts-1", @object = "model", owned_by = "winTTSas" },
                new { id = "tts-1-hd", @object = "model", owned_by = "winTTSas" },
                new { id = "gpt-4o-mini-tts", @object = "model", owned_by = "winTTSas" }
            }
        }));

        app.MapGet("/v1/audio/voices", () => Results.Json(new
        {
            @object = "list",
            data = speech.Voices.Select(v => new
            {
                id = v.Slug,
                name = v.Name,
                language = v.Language,
                gender = v.Gender,
                natural = v.IsNatural,
                description = v.Description,
                sample_rate = v.SampleRate
            }).ToArray()
        }));

        app.MapGet("/doctor", () => Results.Json(new
        {
            version = BuildInfo.Version,
            configFile = AppPaths.ConfigFile,
            logDir = AppPaths.LogDir,
            adapter = AdapterProbe.Get(),
            voices = speech.Voices,
            ffmpeg = speech.FfmpegVersion,
            ffmpegPath = speech.FfmpegPath,
            cache = new { enabled = speech.Config.CacheEnabled, files = speech.CacheCount, bytes = speech.CacheSizeBytes },
            status = speech.GetStatus()
        }));

        app.MapPost("/v1/audio/speech", (HttpContext context) =>
            HandleSpeechAsync(context, speech, logger));

        app.MapPost("/audio/speech", (HttpContext context) =>
            HandleSpeechAsync(context, speech, logger));

        app.MapPost("/v1/audio/speech/{**segments}", (HttpContext context) =>
            HandleSpeechPostSegmentsAsync(context, speech, logger));

        app.MapPost("/audio/speech/{**segments}", (HttpContext context) =>
            HandleSpeechPostSegmentsAsync(context, speech, logger));

        app.MapPost("/v1/{**segments}", (HttpContext context) =>
            HandleSpeechPostSegmentsAsync(context, speech, logger));

        app.MapPost("/audio/{**segments}", (HttpContext context) =>
            HandleSpeechPostSegmentsAsync(context, speech, logger));

        app.MapGet("/v1/audio/speech", (HttpContext context) =>
            HandleSpeechQueryAsync(context, speech, logger));

        app.MapGet("/audio/speech", (HttpContext context) =>
            HandleSpeechQueryAsync(context, speech, logger));

        app.MapGet("/v1/audio/speech/{**segments}", (HttpContext context) =>
            HandleSpeechSegmentsAsync(context, speech, logger));

        app.MapGet("/audio/speech/{**segments}", (HttpContext context) =>
            HandleSpeechSegmentsAsync(context, speech, logger));

        app.MapGet("/v1/{**segments}", (HttpContext context) =>
            HandleSpeechSegmentsAsync(context, speech, logger));

        app.MapGet("/audio/{**segments}", (HttpContext context) =>
            HandleSpeechSegmentsAsync(context, speech, logger));
    }

    private static async Task<ControlResponse> HandleControlAsync(
        ControlRequest request,
        SpeechService speech,
        HotkeyManager hotkeys,
        SpeechActions actions)
    {
        switch ((request.Cmd ?? "").Trim().ToLowerInvariant())
        {
            case "status":
                return new ControlResponse
                {
                    Ok = true,
                    Status = BuildStatus(speech, hotkeys, actions)
                };

            case "reload":
                try
                {
                    var config = ConfigStore.Load();
                    speech.Reload(config);
                    await hotkeys.ApplyAsync(config, force: true);

                    return new ControlResponse
                    {
                        Ok = true,
                        Status = BuildStatus(speech, hotkeys, actions)
                    };
                }
                catch (Exception ex)
                {
                    return new ControlResponse
                    {
                        Ok = false,
                        Error = ex.Message
                    };
                }

            case "stop":
                return new ControlResponse
                {
                    Ok = true,
                    Stop = true,
                    Status = BuildStatus(speech, hotkeys, actions)
                };

            default:
                return new ControlResponse
                {
                    Ok = false,
                    Error = $"unknown command: {request.Cmd}"
                };
        }
    }

    private static StatusInfo BuildStatus(
        SpeechService speech,
        HotkeyManager hotkeys,
        SpeechActions actions) =>
        speech.GetStatus() with
        {
            SelectedTextHotkey = speech.Config.Hotkeys.SelectedText.ActiveChord ?? "",
            ClipboardHotkey = speech.Config.Hotkeys.Clipboard.ActiveChord ?? "",
            HotkeyError = hotkeys.Error ?? "",
            Speaking = actions.IsSpeaking
        };

    private static async Task ReapplyHotkeysAsync(
        HotkeyManager hotkeys,
        AppConfig config,
        ILogger logger)
    {
        try
        {
            await hotkeys.ApplyAsync(config);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "could not re-apply the global hotkeys");
        }
    }

    private static async Task HandleSpeechAsync(
        HttpContext context,
        SpeechService speech,
        ILogger logger)
    {
        OpenAiSpeechRequest? request;

        try
        {
            request = await JsonSerializer.DeserializeAsync<OpenAiSpeechRequest>(
                context.Request.Body,
                JsonOptions,
                context.RequestAborted);
        }
        catch (JsonException)
        {
            await WriteError(
                context,
                StatusCodes.Status400BadRequest,
                "Invalid JSON body.",
                "invalid_request_error");
            return;
        }

        await ExecuteSpeechAsync(context, speech, logger, request ?? new OpenAiSpeechRequest());
    }

    private static async Task HandleSpeechQueryAsync(
        HttpContext context,
        SpeechService speech,
        ILogger logger)
    {
        if (!SpeechUrl.TryParseQuery(context.Request.Query, out var values, out var error))
        {
            await WriteSpeechUrlErrorAsync(context, error!);
            return;
        }

        await ExecuteSpeechAsync(context, speech, logger, ToSpeechRequest(values));
    }

    private static async Task HandleSpeechSegmentsAsync(
        HttpContext context,
        SpeechService speech,
        ILogger logger)
    {
        var segments = context.Request.RouteValues.TryGetValue("segments", out var value)
            ? value as string
            : null;

        if (!SpeechUrl.TryParsePath(segments, out var values, out var error))
        {
            await WriteSpeechUrlErrorAsync(context, error!);
            return;
        }

        if (!SpeechUrl.TryApplyQueryFallback(values, context.Request.Query, out error))
        {
            await WriteSpeechUrlErrorAsync(context, error!);
            return;
        }

        if (string.IsNullOrWhiteSpace(values.Input))
        {
            await WriteSpeechUrlErrorAsync(context, SpeechUrl.MissingTextError());
            return;
        }

        await ExecuteSpeechAsync(context, speech, logger, ToSpeechRequest(values));
    }

    private static async Task HandleSpeechPostSegmentsAsync(
        HttpContext context,
        SpeechService speech,
        ILogger logger)
    {
        var segments = context.Request.RouteValues.TryGetValue("segments", out var value)
            ? value as string
            : null;

        if (!SpeechUrl.TryParsePath(segments, out var values, out var error))
        {
            await WriteSpeechUrlErrorAsync(context, error!);
            return;
        }

        if (!SpeechUrl.TryApplyQueryFallback(values, context.Request.Query, out error))
        {
            await WriteSpeechUrlErrorAsync(context, error!);
            return;
        }

        OpenAiSpeechRequest? body = null;
        if (context.Request.ContentLength.GetValueOrDefault() > 0 ||
            context.Request.Headers.TransferEncoding.Count > 0)
        {
            try
            {
                body = await JsonSerializer.DeserializeAsync<OpenAiSpeechRequest>(
                    context.Request.Body,
                    JsonOptions,
                    context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteError(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Invalid JSON body.",
                    "invalid_request_error");
                return;
            }
        }

        // URL values win over body defaults; the body fills whatever the URL
        // did not set.
        var request = body ?? new OpenAiSpeechRequest();
        request.Input = values.Input ?? request.Input;
        request.Voice = values.Voice ?? request.Voice;
        request.ResponseFormat = values.Format ?? request.ResponseFormat;
        request.Speed = values.Speed ?? request.Speed;
        request.GainDb = values.GainDb ?? request.GainDb;
        request.Pitch = values.Pitch ?? request.Pitch;
        request.Normalize = values.Normalize ?? request.Normalize;

        if (string.IsNullOrWhiteSpace(request.Input))
        {
            await WriteSpeechUrlErrorAsync(context, SpeechUrl.MissingTextError());
            return;
        }

        await ExecuteSpeechAsync(context, speech, logger, request);
    }

    private static OpenAiSpeechRequest ToSpeechRequest(SpeechUrlValues values) => new()
    {
        Input = values.Input,
        Voice = values.Voice,
        ResponseFormat = values.Format,
        Speed = values.Speed,
        GainDb = values.GainDb,
        Pitch = values.Pitch,
        Normalize = values.Normalize,
        Sanitize = values.Sanitize
    };

    private static Task WriteSpeechUrlErrorAsync(HttpContext context, SpeechUrlError error) =>
        WriteError(
            context,
            StatusCodes.Status400BadRequest,
            error.Message,
            "invalid_request_error",
            error.Param,
            error.Code);

    private static async Task ExecuteSpeechAsync(
        HttpContext context,
        SpeechService speech,
        ILogger logger,
        OpenAiSpeechRequest request)
    {
        var cancellationToken = context.RequestAborted;

        if (string.IsNullOrWhiteSpace(request.Input))
        {
            await WriteError(
                context,
                StatusCodes.Status400BadRequest,
                "input is required.",
                "invalid_request_error",
                "input");
            return;
        }

        if (request.Input.Length > speech.Config.MaxTextLength)
        {
            await WriteError(
                context,
                StatusCodes.Status400BadRequest,
                $"input must be {speech.Config.MaxTextLength} characters or fewer.",
                "invalid_request_error",
                "input",
                "input_too_long");
            return;
        }

        if (request.Speed is < 0.25 or > 4.0)
        {
            await WriteError(
                context,
                StatusCodes.Status400BadRequest,
                "speed must be between 0.25 and 4.0.",
                "invalid_request_error",
                "speed");
            return;
        }

        if (request.ResponseFormat is not null &&
            !AudioFormats.IsSupported(request.ResponseFormat))
        {
            await WriteError(
                context,
                StatusCodes.Status400BadRequest,
                $"response_format must be one of: {string.Join(", ", AudioFormats.All)}.",
                "invalid_request_error",
                "response_format");
            return;
        }

        var requestGain = request.GainDb ?? request.GainDbCamel;
        if (requestGain is < -24.0 or > 24.0)
        {
            await WriteError(
                context,
                StatusCodes.Status400BadRequest,
                "gain_db must be between -24 and +24.",
                "invalid_request_error",
                "gain_db");
            return;
        }

        if (request.Pitch is < -12.0 or > 12.0)
        {
            await WriteError(
                context,
                StatusCodes.Status400BadRequest,
                "pitch must be between -12 and +12 semitones.",
                "invalid_request_error",
                "pitch");
            return;
        }

        try
        {
            var result = await speech.SynthesizeAsync(
                request.Input,
                request.Voice,
                request.ResponseFormat,
                request.Speed,
                requestGain,
                request.Pitch,
                request.Normalize,
                request.Sanitize,
                cancellationToken);

            context.Response.ContentType = result.ContentType;
            context.Response.ContentLength = result.Audio.Length;
            context.Response.Headers["X-WinTTSas-Voice"] = result.Voice;
            context.Response.Headers["X-WinTTSas-Speed"] =
                result.Speed.ToString("0.###", CultureInfo.InvariantCulture);
            context.Response.Headers["X-WinTTSas-Rate"] =
                result.Rate.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers["X-WinTTSas-Gain-Db"] =
                result.GainDb.ToString("0.###", CultureInfo.InvariantCulture);
            context.Response.Headers["X-WinTTSas-Pitch"] =
                result.PitchSemitones.ToString("0.###", CultureInfo.InvariantCulture);
            context.Response.Headers["X-WinTTSas-Normalize"] =
                result.Normalize ? "on" : "off";
            context.Response.Headers["X-WinTTSas-Synth-Ms"] =
                result.ElapsedMs.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers["X-WinTTSas-Cache"] =
                result.CacheHit ? "hit" : "miss";
            context.Response.Headers["Cache-Control"] = "no-store";

            await context.Response.Body.WriteAsync(result.Audio, cancellationToken);

            logger.LogInformation(
                "speech voice={Voice} chars={Chars} format={Format} speed={Speed} rate={Rate} gain_db={GainDb} pitch={Pitch} normalize={Normalize} cache={Cache} ms={Ms} bytes={Bytes}",
                result.Voice,
                request.Input.Length,
                request.ResponseFormat ?? speech.Config.DefaultFormat,
                result.Speed,
                result.Rate,
                result.GainDb,
                result.PitchSemitones,
                result.Normalize,
                result.CacheHit,
                result.ElapsedMs,
                result.Audio.Length);
        }
        catch (QueueFullException)
        {
            context.Response.Headers["Retry-After"] = "2";
            await WriteError(
                context,
                StatusCodes.Status429TooManyRequests,
                "The synthesis queue is full; retry shortly.",
                "rate_limit_error");
        }
        catch (FfmpegUnavailableException ex)
        {
            await WriteError(
                context,
                StatusCodes.Status503ServiceUnavailable,
                ex.Message,
                "server_error");
        }
        catch (ArgumentException ex)
        {
            await WriteError(
                context,
                StatusCodes.Status400BadRequest,
                ex.Message,
                "invalid_request_error",
                "input");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // client disconnected
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "speech synthesis failed");
            await WriteError(
                context,
                StatusCodes.Status500InternalServerError,
                "Speech synthesis failed.",
                "server_error");
        }
    }

    private static async Task WriteError(
        HttpContext context,
        int status,
        string message,
        string type,
        string? param = null,
        string? code = null)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message,
                type,
                param,
                code
            }
        });
    }

    private static bool IsProtectedPath(PathString path) =>
        path.StartsWithSegments("/v1") || path.StartsWithSegments("/audio");

    private static bool IsAuthorized(HttpContext context, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return true;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        var supplied = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : "";

        if (supplied.Length == 0)
        {
            supplied = context.Request.Headers["X-Api-Key"].ToString().Trim();
        }

        if (supplied.Length == 0)
        {
            supplied = context.Request.Query["token"].ToString().Trim();
        }

        if (supplied.Length == 0)
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(supplied);
        var b = Encoding.UTF8.GetBytes(token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static IPAddress ResolveBind(string bind)
    {
        if (string.IsNullOrWhiteSpace(bind) ||
            bind.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(bind, out var address)
            ? address
            : throw new InvalidOperationException($"Invalid bind address: {bind}");
    }

    private static void AppendFatal(string logFile, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            File.AppendAllText(
                logFile,
                $"{DateTimeOffset.Now:O} [Fatal] {exception}{Environment.NewLine}");
        }
        catch
        {
            // best effort
        }
    }

    private sealed class OpenAiSpeechRequest
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("input")]
        public string? Input { get; set; }

        [JsonPropertyName("voice")]
        public string? Voice { get; set; }

        [JsonPropertyName("response_format")]
        public string? ResponseFormat { get; set; }

        [JsonPropertyName("speed")]
        public double? Speed { get; set; }

        [JsonPropertyName("gain_db")]
        public double? GainDb { get; set; }

        [JsonPropertyName("gainDb")]
        public double? GainDbCamel { get; set; }

        [JsonPropertyName("pitch")]
        public double? Pitch { get; set; }

        [JsonPropertyName("normalize")]
        public bool? Normalize { get; set; }

        [JsonPropertyName("sanitize")]
        public bool? Sanitize { get; set; }

        [JsonPropertyName("instructions")]
        public string? Instructions { get; set; }
    }
}
