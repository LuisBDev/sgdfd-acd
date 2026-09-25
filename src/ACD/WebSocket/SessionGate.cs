namespace ACD.WebSocket;

/// <summary>
///     Coordinador thread-safe que mantiene exclusividad por tipo de operación.
///     Permite una firma, una apertura PDF y una edición de documento en paralelo, pero nunca dos operaciones
///     simultáneas del mismo tipo.
/// </summary>
public sealed class SessionGate : ISessionGate
{
    private readonly SemaphoreSlim _connectionSlots = new(3, 3);
    private readonly SemaphoreSlim _documentEditLock = new(1, 1);
    private readonly SemaphoreSlim _pdfOpenLock = new(1, 1);
    private readonly SemaphoreSlim _signingLock = new(1, 1);
    private volatile bool _isDocumentEditActive;
    private volatile bool _isPdfOpenActive;
    private volatile bool _isSigningActive;

    public bool IsActive => _isSigningActive || _isPdfOpenActive || _isDocumentEditActive;
    public bool IsSigningActive => _isSigningActive;
    public bool IsPdfOpenActive => _isPdfOpenActive;
    public bool IsDocumentEditActive => _isDocumentEditActive;

    public Task<bool> TryAcquireConnectionAsync(CancellationToken ct) =>
        _connectionSlots.WaitAsync(0, ct);

    public void ReleaseConnection() => _connectionSlots.Release();

    /// <summary>
    ///     Intenta adquirir la compuerta de la operación solicitada. Retorna false
    ///     inmediatamente si ya existe otra operación del mismo tipo.
    /// </summary>
    public async Task<bool> TryAcquireAsync(SessionOperation operation, CancellationToken ct)
    {
        var gate = GateFor(operation);
        var acquired = await gate.WaitAsync(0, ct).ConfigureAwait(false);
        if (acquired) SetActive(operation, true);
        return acquired;
    }

    /// <summary>
    ///     Libera exclusivamente la compuerta de la operación indicada.
    /// </summary>
    public void Release(SessionOperation operation)
    {
        SetActive(operation, false);
        GateFor(operation).Release();
    }

    private SemaphoreSlim GateFor(SessionOperation operation) => operation switch
    {
        SessionOperation.Signing => _signingLock,
        SessionOperation.PdfOpen => _pdfOpenLock,
        SessionOperation.DocumentEdit => _documentEditLock,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };

    private void SetActive(SessionOperation operation, bool active)
    {
        switch (operation)
        {
            case SessionOperation.Signing:
                _isSigningActive = active;
                break;
            case SessionOperation.PdfOpen:
                _isPdfOpenActive = active;
                break;
            case SessionOperation.DocumentEdit:
                _isDocumentEditActive = active;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }
}
