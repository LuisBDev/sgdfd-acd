namespace ACWF.Configuration;

public sealed class UpdateOptions
{
    public int CheckIntervalHours { get; init; } = 6;
    public string RepoUrl { get; init; } = "";
    public bool IncludePrerelease { get; init; }
}
