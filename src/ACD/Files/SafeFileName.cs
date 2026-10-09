namespace ACD.Files;

public static class SafeFileName
{
    public const int MaxLength = 180;

    public static bool IsValid(string? name, Func<string, bool> isAllowedExtension) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxLength
        && string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal)
        && isAllowedExtension(Path.GetExtension(name))
        && Path.GetFileNameWithoutExtension(name).Trim().Length > 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.Contains("..", StringComparison.Ordinal);
}
