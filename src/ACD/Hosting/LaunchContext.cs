namespace ACD.Hosting;

/// <summary>
///     Captures launch-time context so services can adapt their behavior
///     depending on how the process was started.
/// </summary>
public sealed class LaunchContext
{
    /// <summary>
    ///     True when the process was triggered by a URI-scheme activation
    ///     (e.g. <c>acd://…</c>) rather than by the user opening the application
    ///     directly from the desktop, Start menu, or system tray.
    /// </summary>
    public bool IsUriSchemeInvocation { get; init; }
}
