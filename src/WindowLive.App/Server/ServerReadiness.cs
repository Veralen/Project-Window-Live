using System.Threading;
using System.Threading.Tasks;

namespace WindowLive.App.Server;

/// <summary>
/// Tracks whether llama-server has finished starting, so code that doesn't own
/// the server lifecycle (the snip pipeline in <see cref="Overlay.SnipController"/>)
/// can wait for readiness without depending on <see cref="LlamaServerManager"/>
/// directly. Starts in the "not yet settled" state; App.xaml.cs calls
/// <see cref="MarkReady"/> or <see cref="MarkFailed"/> exactly once per startup
/// attempt, and <see cref="Reset"/> before each Retry.
/// </summary>
internal sealed class ServerReadiness
{
    private readonly object _gate = new();
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady { get; private set; }
    public bool IsFailed { get; private set; }
    public string? FailureMessage { get; private set; }

    /// <summary>Starts a new attempt: clears Ready/Failed and re-arms the wait gate.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            IsReady = false;
            IsFailed = false;
            FailureMessage = null;
            _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void MarkReady()
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            IsReady = true;
            IsFailed = false;
            tcs = _tcs;
        }
        tcs.TrySetResult();
    }

    public void MarkFailed(string message)
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            IsFailed = true;
            FailureMessage = message;
            tcs = _tcs;
        }
        tcs.TrySetResult();
    }

    /// <summary>
    /// Completes once the current attempt has settled (ready or failed). Never
    /// faults on its own (unless <paramref name="ct"/> is cancelled) — check
    /// <see cref="IsFailed"/> after it completes to distinguish the two outcomes.
    /// </summary>
    public Task WaitUntilSettledAsync(CancellationToken ct = default)
    {
        Task t;
        lock (_gate) t = _tcs.Task;
        return ct.CanBeCanceled ? t.WaitAsync(ct) : t;
    }
}
