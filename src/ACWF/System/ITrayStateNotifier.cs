namespace ACWF.System;

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
}
