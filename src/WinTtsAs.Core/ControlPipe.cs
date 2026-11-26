using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WinTtsAs.Core;

public sealed class ServerAlreadyRunningException : Exception
{
    public ServerAlreadyRunningException(string message) : base(message)
    {
    }
}

public static class ControlPipeClient
{
    public static async Task<ControlResponse?> SendAsync(
        ControlRequest request,
        int timeoutMs = 500,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                ControlPipeNames.ForCurrentUser(),
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(timeoutMs, cancellationToken);

            using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                1024,
                leaveOpen: true)
            {
                AutoFlush = true
            };
            using var reader = new StreamReader(
                pipe,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                1024,
                leaveOpen: true);

            await writer.WriteLineAsync(
                JsonSerializer.Serialize(request, ControlJson.Options));
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return null;
            }

            return JsonSerializer.Deserialize<ControlResponse>(line, ControlJson.Options);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<StatusInfo?> GetStatusAsync(
        int timeoutMs = 500,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new ControlRequest { Cmd = "status" },
            timeoutMs,
            cancellationToken);

        return response is { Ok: true } ? response.Status : null;
    }
}

public sealed class ControlPipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<ControlRequest, Task<ControlResponse>> _handler;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ControlPipeServer> _log;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ControlPipeServer(
        string pipeName,
        Func<ControlRequest, Task<ControlResponse>> handler,
        IHostApplicationLifetime lifetime,
        ILogger<ControlPipeServer> log)
    {
        _pipeName = pipeName;
        _handler = handler;
        _lifetime = lifetime;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        NamedPipeServerStream first;
        try
        {
            first = CreatePipe();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ServerAlreadyRunningException(
                "Another winTTSas server is already using the control pipe.");
        }

        _loop = Task.Run(() => LoopAsync(first, _cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private NamedPipeServerStream CreatePipe() =>
        new(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task LoopAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(cancellationToken);

                var shouldStop = false;
                try
                {
                    using var reader = new StreamReader(
                        pipe,
                        Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: false,
                        1024,
                        leaveOpen: true);
                    using var writer = new StreamWriter(
                        pipe,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        1024,
                        leaveOpen: true)
                    {
                        AutoFlush = true
                    };

                    var line = await reader.ReadLineAsync(cancellationToken);
                    ControlResponse response;

                    if (line is null)
                    {
                        response = new ControlResponse { Ok = false, Error = "empty request" };
                    }
                    else
                    {
                        try
                        {
                            var request = JsonSerializer.Deserialize<ControlRequest>(
                                line,
                                ControlJson.Options);

                            response = request is null
                                ? new ControlResponse { Ok = false, Error = "invalid request" }
                                : await _handler(request);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "control command failed");
                            response = new ControlResponse { Ok = false, Error = ex.Message };
                        }
                    }

                    shouldStop = response.Stop;
                    await writer.WriteLineAsync(
                        JsonSerializer.Serialize(response, ControlJson.Options));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogDebug(ex, "control connection failed");
                }
                finally
                {
                    pipe.Dispose();
                }

                if (shouldStop)
                {
                    await Task.Delay(100, CancellationToken.None);
                    _lifetime.StopApplication();
                    return;
                }

                pipe = CreatePipe();
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "control pipe stopped");
        }
        finally
        {
            pipe.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // best effort
            }
        }

        _cts?.Dispose();
    }
}
