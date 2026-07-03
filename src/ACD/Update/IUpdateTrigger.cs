namespace ACD.Update;

public enum UpdateOutcome
{
    Applied,
    NoUpdatesAvailable,
    SessionActive,
    Failed
}

public interface IUpdateTrigger
{
    bool HasPendingUpdate { get; }
    string? PendingVersion { get; }
    bool IsBusy { get; }
    int LastProgress { get; }

    Task<UpdateOutcome> UpdateNowAsync();
}
