using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AiTranslate;

/// <summary>
/// Runs at most one job per key in the background; a caller asking for a key that is running joins that job.
/// The key is reserved before the work starts, so two requests at the same moment never pay twice, and it is released
/// before the result is published, so a caller that sees a failure can start again at once. Each job gets its own
/// cancellation token (<see cref="Cancel"/>), independent of the callers: a client that stops waiting does not stop it.
/// </summary>
public sealed class SingleFlight<TKey, TResult>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Flight> _running;

    public SingleFlight(IEqualityComparer<TKey>? comparer = null)
    {
        _running = new ConcurrentDictionary<TKey, Flight>(comparer);
    }

    public bool IsRunning(TKey key) => _running.ContainsKey(key);

    /// <summary>Starts <paramref name="work"/> unless the key is running; <paramref name="started"/> tells which happened.</summary>
    public Task<TResult> Run(TKey key, Func<CancellationToken, Task<TResult>> work, out bool started)
    {
        var flight = new Flight();
        var current = _running.GetOrAdd(key, flight);
        started = current == flight;
        if (!started)
        {
            flight.Stop.Dispose();
            return current.Result.Task;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work(flight.Stop.Token).ConfigureAwait(false);
                Finish(key, flight);
                flight.Result.SetResult(result);
            }
            catch (OperationCanceledException) when (flight.Stop.IsCancellationRequested)
            {
                Finish(key, flight);
                flight.Result.SetCanceled();
            }
            catch (Exception ex)
            {
                Finish(key, flight);
                flight.Result.SetException(ex);
            }
        });

        return flight.Result.Task;
    }

    /// <summary>Asks the job running under the key to stop; false when none is running.</summary>
    public bool Cancel(TKey key)
    {
        if (!_running.TryGetValue(key, out var flight))
        {
            return false;
        }

        try
        {
            flight.Stop.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void Finish(TKey key, Flight flight)
    {
        _running.TryRemove(new KeyValuePair<TKey, Flight>(key, flight));
        flight.Stop.Dispose();
    }

    private sealed class Flight
    {
        public TaskCompletionSource<TResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource Stop { get; } = new();
    }
}
