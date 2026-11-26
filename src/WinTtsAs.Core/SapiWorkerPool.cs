using System.Collections.Concurrent;
using System.Speech.Synthesis;
using Microsoft.Extensions.Logging;

namespace WinTtsAs.Core;

public sealed record SynthesisRequest(string Text, string VoiceName, int Rate, int Volume);

public sealed class QueueFullException : Exception
{
    public QueueFullException() : base("The synthesis queue is full.")
    {
    }
}

public static class SpeechRate
{
    public static int FromSpeed(double speed)
    {
        speed = Math.Clamp(speed, 0.25, 4.0);
        var rate = (int)Math.Round(10.0 * Math.Log(speed) / Math.Log(3.0));
        return Math.Clamp(rate, -10, 10);
    }
}

public sealed class SapiWorkerPool : IAsyncDisposable
{
    private readonly BlockingCollection<Job> _queue;
    private readonly Thread[] _threads;
    private readonly ILogger? _log;

    public int WorkerCount => _threads.Length;
    public int QueueCount => _queue.Count;

    public SapiWorkerPool(int workers, int queueLimit, ILogger? log = null)
    {
        _log = log;
        _queue = new BlockingCollection<Job>(
            new ConcurrentQueue<Job>(),
            Math.Clamp(queueLimit, 1, 100));

        _threads = new Thread[Math.Clamp(workers, 1, 4)];
        for (var i = 0; i < _threads.Length; i++)
        {
            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"winTTSas-sapi-{i + 1}"
            };
            thread.SetApartmentState(ApartmentState.STA);
            _threads[i] = thread;
            thread.Start();
        }
    }

    public async Task<byte[]> SynthesizeAsync(
        SynthesisRequest request,
        CancellationToken cancellationToken)
    {
        if (_queue.IsAddingCompleted)
        {
            throw new ObjectDisposedException(nameof(SapiWorkerPool));
        }

        var job = new Job(request);
        if (!_queue.TryAdd(job))
        {
            throw new QueueFullException();
        }

        return await job.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void WorkerLoop()
    {
        SpeechSynthesizer? synth;
        try
        {
            synth = new SpeechSynthesizer();
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "could not create SpeechSynthesizer worker");
            return;
        }

        using (synth)
        {
            string? currentVoice = null;

            foreach (var job in _queue.GetConsumingEnumerable())
            {
                if (job.Completion.Task.IsCompleted)
                {
                    continue;
                }

                try
                {
                    if (!string.Equals(currentVoice, job.Request.VoiceName, StringComparison.Ordinal))
                    {
                        synth.SelectVoice(job.Request.VoiceName);
                        currentVoice = job.Request.VoiceName;
                    }

                    synth.Rate = job.Request.Rate;
                    synth.Volume = job.Request.Volume;

                    using var wave = new MemoryStream();
                    synth.SetOutputToWaveStream(wave);
                    synth.Speak(job.Request.Text);
                    synth.SetOutputToNull();

                    job.Completion.TrySetResult(wave.ToArray());
                }
                catch (Exception ex)
                {
                    job.Completion.TrySetException(ex);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.CompleteAdding();

        foreach (var thread in _threads)
        {
            if (thread.IsAlive)
            {
                thread.Join(TimeSpan.FromSeconds(3));
            }
        }

        _queue.Dispose();
        await Task.CompletedTask;
    }

    private sealed class Job
    {
        public Job(SynthesisRequest request)
        {
            Request = request;
        }

        public SynthesisRequest Request { get; }

        public TaskCompletionSource<byte[]> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
