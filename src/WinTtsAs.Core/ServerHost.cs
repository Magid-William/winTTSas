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
                    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
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

            pipe = new ControlPipeServer(
                ControlPipeNames.ForCurrentUser(),
                request => HandleControlAsync(request, speech),
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
        app.MapGet("/", () => Results.Content(TestPage.Html, "text/html; charset=utf-8"));

        app.MapGet("/health", () => Results.Json(new
        {
            ok = true,
            version = BuildInfo.Version,
            pid = Environment.ProcessId,
            port = speech.Config.Port,
            baseUrl = AppPaths.OpenAiBaseUrl(speech.Config.Port, speech.Config.Bind),
            voice = speech.DefaultVoice?.Name,
            format = speech.Config.DefaultFormat,
            speed = speech.Config.DefaultSpeed,
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
            cache = new { enabled = speech.Config.CacheEnabled, files = speech.CacheCount, bytes = speech.CacheSizeBytes },
            status = speech.GetStatus()
        }));

        app.MapPost("/v1/audio/speech", (HttpContext context) =>
            HandleSpeechAsync(context, speech, logger));

        app.MapPost("/audio/speech", (HttpContext context) =>
            HandleSpeechAsync(context, speech, logger));
    }

    private static Task<ControlResponse> HandleControlAsync(
        ControlRequest request,
        SpeechService speech)
    {
        switch ((request.Cmd ?? "").Trim().ToLowerInvariant())
        {
            case "status":
                return Task.FromResult(new ControlResponse
                {
                    Ok = true,
                    Status = speech.GetStatus()
                });

            case "reload":
                try
                {
                    speech.Reload(ConfigStore.Load());
                    return Task.FromResult(new ControlResponse
                    {
                        Ok = true,
                        Status = speech.GetStatus()
                    });
                }
                catch (Exception ex)
                {
                    return Task.FromResult(new ControlResponse
                    {
                        Ok = false,
                        Error = ex.Message
                    });
                }

            case "stop":
                return Task.FromResult(new ControlResponse
                {
                    Ok = true,
                    Stop = true,
                    Status = speech.GetStatus()
                });

            default:
                return Task.FromResult(new ControlResponse
                {
                    Ok = false,
                    Error = $"unknown command: {request.Cmd}"
                });
        }
    }

    private static async Task HandleSpeechAsync(
        HttpContext context,
        SpeechService speech,
        ILogger logger)
    {
        var cancellationToken = context.RequestAborted;
        OpenAiSpeechRequest? request;

        try
        {
            request = await JsonSerializer.DeserializeAsync<OpenAiSpeechRequest>(
                context.Request.Body,
                JsonOptions,
                cancellationToken);
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

        if (request is null || string.IsNullOrWhiteSpace(request.Input))
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

        try
        {
            var result = await speech.SynthesizeAsync(
                request.Input,
                request.Voice,
                request.ResponseFormat,
                request.Speed,
                cancellationToken);

            context.Response.ContentType = result.ContentType;
            context.Response.ContentLength = result.Audio.Length;
            context.Response.Headers["X-WinTTSas-Voice"] = result.Voice;
            context.Response.Headers["X-WinTTSas-Synth-Ms"] =
                result.ElapsedMs.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers["X-WinTTSas-Cache"] =
                result.CacheHit ? "hit" : "miss";
            context.Response.Headers["Cache-Control"] = "no-store";

            await context.Response.Body.WriteAsync(result.Audio, cancellationToken);

            logger.LogInformation(
                "speech voice={Voice} chars={Chars} format={Format} cache={Cache} ms={Ms} bytes={Bytes}",
                result.Voice,
                request.Input.Length,
                request.ResponseFormat ?? speech.Config.DefaultFormat,
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

        [JsonPropertyName("instructions")]
        public string? Instructions { get; set; }
    }
}
