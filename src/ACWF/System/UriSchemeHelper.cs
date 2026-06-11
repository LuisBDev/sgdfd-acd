using Microsoft.Win32;

namespace ACWF.System;

/// <summary>
/// Manages Windows registry entries for the ACWF URI scheme (acwf:// and acwf-dev://).
/// Operations are idempotent — safe to call on every launch and update.
/// </summary>
public static class UriSchemeHelper
{
    /// <summary>
    /// Registers the URI scheme under HKCU\Software\Classes so the browser can invoke the agent.
    /// Creates or overwrites existing registration.
    /// </summary>
    /// <param name="scheme">The URI scheme name, e.g. "acwf" or "acwf-dev".</param>
    /// <param name="exePath">Absolute path to the executable to invoke.</param>
    public static void EnsureRegistered(string scheme, string exePath)
    {
        using RegistryKey classesRoot = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true)
            ?? Registry.CurrentUser.CreateSubKey(@"Software\Classes");

        using RegistryKey schemeKey = classesRoot.CreateSubKey(scheme);
        schemeKey.SetValue(null, $"URL:{scheme} Protocol");
        schemeKey.SetValue("URL Protocol", string.Empty);

        using RegistryKey shellKey = schemeKey.CreateSubKey("shell");
        using RegistryKey openKey = shellKey.CreateSubKey("open");
        using RegistryKey commandKey = openKey.CreateSubKey("command");
        commandKey.SetValue(null, $"\"{exePath}\" --uri-invoke \"%1\"");
    }

    /// <summary>
    /// Removes the URI scheme registration from the registry.
    /// Called on Velopack uninstall event.
    /// </summary>
    /// <param name="scheme">The URI scheme name to unregister.</param>
    public static void Unregister(string scheme)
    {
        using RegistryKey? classesRoot = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
        classesRoot?.DeleteSubKeyTree(scheme, throwOnMissingSubKey: false);
    }
}
