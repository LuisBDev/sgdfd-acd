using Microsoft.Extensions.Logging;

namespace ACWF.System;

/// <summary>
/// Manages a lock file that advertises the active Kestrel port so other processes
/// (e.g. a URI-scheme-activated second instance) can discover the running agent.
/// The file is advisory — failures are logged as warnings, not errors.
/// </summary>
public static class PortRegistry
{
    private static readonly ILogger? _logger =
        LoggerFactory.Create(b => b.AddConsole()).CreateLogger(nameof(PortRegistry));

    private static string GetLockFilePath(string packId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            packId,
            "port.lock");

    /// <summary>
    /// Writes the port number to the lock file.
    /// Creates the directory if it does not exist.
    /// </summary>
    public static void Write(string packId, int port)
    {
        string lockFile = GetLockFilePath(packId);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
            File.WriteAllText(lockFile, port.ToString());
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Failed to write port lock file at {LockFile}", lockFile);
        }
    }

    /// <summary>
    /// Deletes the lock file on graceful shutdown.
    /// </summary>
    public static void Delete(string packId)
    {
        string lockFile = GetLockFilePath(packId);
        try
        {
            if (File.Exists(lockFile))
            {
                File.Delete(lockFile);
            }
        }
        catch (IOException)
        {
            // Advisory file — swallow deletion errors.
        }
    }

    /// <summary>
    /// Reads and parses the port from the lock file. Returns null on any failure.
    /// </summary>
    public static int? TryRead(string packId)
    {
        string lockFile = GetLockFilePath(packId);
        try
        {
            if (!File.Exists(lockFile)) return null;
            string content = File.ReadAllText(lockFile);
            return int.TryParse(content.Trim(), out int port) ? port : null;
        }
        catch
        {
            return null;
        }
    }
}
