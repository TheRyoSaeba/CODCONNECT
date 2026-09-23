using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace CODConnect.SoftEther;

public static class SoftEtherServerSetup
{
    public static Uri DefaultServerUrl { get; } = new("https://127.0.0.1:5555/");

    public static string DefaultCredentialPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CODCONNECT", "softether-admin.bin");

    public static string? FindServerDirectory()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var candidate = Path.Combine(root, "SoftEther VPN Server");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool IsServerInstalled => FindServerDirectory() is not null;

    public static async Task<string?> EnsureAdminPasswordAsync(
        Uri? serverUrl = null, string? credentialPath = null, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        serverUrl ??= DefaultServerUrl;
        credentialPath ??= DefaultCredentialPath;

        var stored = TryLoad(credentialPath);
        if (stored is not null)
        {
            var client = SoftEtherJsonRpcClient.Create(serverUrl, stored);
            if (await SafeTestAsync(client, stored, cancellationToken).ConfigureAwait(false))
            {
                return stored;
            }

            log?.Invoke("stored SoftEther admin password no longer accepted; re-checking the empty password");
        }

        var open = SoftEtherJsonRpcClient.Create(serverUrl, string.Empty);
        if (!await SafeTestAsync(open, string.Empty, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await open.SetServerPasswordAsync(generated, string.Empty, cancellationToken).ConfigureAwait(false);
        Save(credentialPath, generated);
        log?.Invoke("SoftEther admin password set (was empty) and stored under ProgramData");
        return generated;
    }

    public static string? TryLoad(string? credentialPath = null)
    {
        credentialPath ??= DefaultCredentialPath;
        try
        {
            if (!File.Exists(credentialPath))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(credentialPath);
            if (OperatingSystem.IsWindows())
            {
                bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.LocalMachine);
            }

            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static void Save(string credentialPath, string password)
    {
        var directory = Directory.CreateDirectory(Path.GetDirectoryName(credentialPath)!);
        RestrictToAdministrators(directory);
        var bytes = Encoding.UTF8.GetBytes(password);
        if (OperatingSystem.IsWindows())
        {
            bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
        }

        File.WriteAllBytes(credentialPath, bytes);
    }

    private static void RestrictToAdministrators(DirectoryInfo directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var security = directory.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var principals = new List<SecurityIdentifier>
            {
                new(WellKnownSidType.LocalSystemSid, null),
                new(WellKnownSidType.BuiltinAdministratorsSid, null),
            };

            if (WindowsIdentity.GetCurrent().User is { } creator)
            {
                principals.Add(creator);
            }

            foreach (var sid in principals)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            directory.SetAccessControl(security);
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task<bool> SafeTestAsync(ISoftEtherAdmin client, string password, CancellationToken cancellationToken)
    {
        try
        {
            return await client.TestAsync(password, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }
}
