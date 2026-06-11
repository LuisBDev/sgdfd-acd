namespace ACWF.System;

/// <summary>
/// Enforces single-instance behavior per environment using a named global Mutex.
/// If the mutex is already held by another process, exits immediately with code 0.
/// </summary>
public static class InstanceGuard
{
    /// <summary>
    /// Attempts to acquire the global mutex for the given environment variant.
    /// Calls <see cref="Environment.Exit(int)"/> silently if another instance is already running.
    /// </summary>
    /// <param name="environment">Environment suffix: "Dev" or "Prod".</param>
    /// <returns>An <see cref="IDisposable"/> that releases the mutex on disposal.</returns>
    public static IDisposable Acquire(string environment)
    {
        string mutexName = $"Global\\ACWF-{environment}";
        var mutex = new Mutex(initiallyOwned: true, name: mutexName, out bool isNewInstance);

        if (!isNewInstance)
        {
            // Another instance of the same variant is already running — exit silently.
            mutex.Dispose();
            Environment.Exit(0);
        }

        return new MutexHandle(mutex);
    }

    private sealed class MutexHandle : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _disposed;

        internal MutexHandle(Mutex mutex)
        {
            _mutex = mutex;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* already released */ }

            _mutex.Dispose();
        }
    }
}
