namespace ACWF.WebSocket;

public interface ISessionGate
{
    bool IsActive { get; }
    Task<bool> TryAcquireAsync(CancellationToken ct);
    void Release();
}

/// <summary>
/// Thread-safe singleton gate that enforces at most one active WebSocket session.
/// Uses SemaphoreSlim(1,1) for atomic check-and-acquire.
/// </summary>
public sealed class SessionGate : ISessionGate
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _isActive;

    public bool IsActive => _isActive;

    /// <summary>
    /// Attempts to acquire the session gate. Returns false immediately if a session is already active.
    /// </summary>
    public async Task<bool> TryAcquireAsync(CancellationToken ct)
    {
        bool acquired = await _lock.WaitAsync(0, ct).ConfigureAwait(false);
        if (acquired)
        {
            _isActive = true;
        }
        return acquired;
    }

    /// <summary>
    /// Releases the gate, allowing new sessions to connect.
    /// </summary>
    public void Release()
    {
        _isActive = false;
        _lock.Release();
    }
}
