using System.Security.AccessControl;
using System.Security.Principal;

#pragma warning disable CA1416

namespace WeChatVoice.KeyBroker;

/// <summary>
/// Applies explicit ACLs to Broker-owned materialization directories. Elevated
/// staging is private to SYSTEM and local Administrators. Once the output is
/// committed, the verified caller SID is added to every directory and file so
/// the normal-privilege Desktop can read the resulting SQLite workspace.
/// </summary>
internal static class BrokerDirectorySecurity
{
    internal static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static void RestrictToSystemAndAdministrators(string directory)
    {
        ApplyDirectorySecurity(directory, userSid: null);
    }

    /// <summary>
    /// Replaces the inherited staging ACL after the atomic directory move. The
    /// caller SID comes from the authenticated named-pipe client; the fallback
    /// is used only by direct Broker-host tests and local diagnostics.
    /// </summary>
    internal static void GrantFinalWorkspaceAccess(string directory, string? userSid = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Broker directory security is Windows-only.");
        }

        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The final materialization workspace was not a regular directory.");
        }

        var directories = new List<string> { root };
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The final materialization workspace contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(entry);
                    pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        foreach (var path in directories.OrderBy(static path => path.Length))
        {
            ApplyDirectorySecurity(path, userSid);
        }

        foreach (var path in files.Order(StringComparer.Ordinal))
        {
            ApplyFileSecurity(path, userSid);
        }
    }

    private static void ApplyDirectorySecurity(string directory, string? userSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Broker directory security is Windows-only.");
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), directory: true);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), directory: true);
        if (!string.IsNullOrWhiteSpace(userSid))
        {
            AddFullControl(security, new SecurityIdentifier(userSid), directory: true);
        }

        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void ApplyFileSecurity(string file, string? userSid)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), directory: false);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), directory: false);
        if (!string.IsNullOrWhiteSpace(userSid))
        {
            AddFullControl(security, new SecurityIdentifier(userSid), directory: false);
        }

        new FileInfo(file).SetAccessControl(security);
    }

    private static void AddFullControl(FileSystemSecurity security, IdentityReference identity, bool directory)
    {
        var inheritance = directory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
    }
}

#pragma warning restore CA1416
