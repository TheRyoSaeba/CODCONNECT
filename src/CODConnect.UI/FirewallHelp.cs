using Microsoft.Win32;
using System.IO;

namespace CODConnect.UI;

internal static class FirewallHelp
{
    public static string? FindExecutable(string backendMode)
    {
        string? serviceCommand = null;
        if (backendMode == "service")
        {
            try
            {
                using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\CODConnect");
                serviceCommand = service?.GetValue("ImagePath") as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }

        return ResolveExecutable(backendMode, serviceCommand, Environment.ProcessPath, File.Exists);
    }

    internal static string? ResolveExecutable(string backendMode, string? serviceCommand, string? processPath, Func<string, bool> exists)
    {
        var path = backendMode switch
        {
            "service" => ParseExecutablePath(serviceCommand),
            "embedded" => processPath,
            _ => null
        };
        if (path is null || !Path.IsPathFullyQualified(path) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return null;
        return exists(path) ? path : null;
    }

    internal static string? ParseExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        if (expanded.StartsWith('"'))
        {
            var end = expanded.IndexOf('"', 1);
            return end > 1 ? expanded[1..end] : null;
        }
        var exe = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? expanded[..(exe + 4)] : null;
    }
}
