namespace ACD.Tray;

public enum TrayState
{
    Ready,
    Connected,
    Error
}

public interface ITrayStateNotifier
{
    void SetState(TrayState state);
    void NotifyUpdateAvailable(string version);
    void NotifyUpdateProgress(int percent);

    /// <summary>
    ///     Shows a balloon informing the user that a downloaded update is about
    ///     to be applied automatically and the process will restart.
    /// </summary>
    void NotifyUpdateApplying(string version);
}