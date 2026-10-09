namespace ACD.DocumentWorkspace;

public sealed record WordConversionSettings(TimeSpan Timeout, TimeSpan ExitGracePeriod, string TempRoot);
