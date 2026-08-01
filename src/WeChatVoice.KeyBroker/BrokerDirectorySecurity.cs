using System.Security.AccessControl;
using System.Security.Principal;

namespace WeChatVoice.KeyBroker;

/// <summary>
/// Restricts an elevated Broker-created directory to SYSTEM and local
/// Administrators only, without inherited rules, so an ordinary same-user
/// process cannot replace or modify the private temporary copies. The Broker
/// runs elevated and always applies this DACL in production; failure fails
/// closed.
/// </summary>
internal static class BrokerDirectorySecurity
{
    /// <summary>
    /// True only when the current token is actually elevated (Administrators
    /// enabled). The one-shot Broker is always elevated in production; unit
    /// tests and development invocations run filtered and skip the DACL so
    /// they can still exercise the surrounding flow.
    /// </summary>
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
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Broker directory security is Windows-only.");
        }

        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
