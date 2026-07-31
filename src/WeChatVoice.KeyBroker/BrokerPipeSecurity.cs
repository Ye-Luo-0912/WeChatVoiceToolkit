using System.IO.Pipes;
using System.Security.Principal;

namespace WeChatVoice.KeyBroker;

/// <summary>
/// The Broker runs elevated while the CLI remains medium-integrity. The
/// framework's CurrentUserOnly flag can apply an integrity boundary that blocks
/// that legitimate pair, so the server uses an explicit SID ACL instead.
/// The random 256-bit pipe name and client-side server-PID check remain part of
/// the protocol boundary.
/// </summary>
internal static class BrokerPipeSecurity
{
    internal static PipeSecurity CreateForCurrentUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Key Broker pipe security policy is Windows-only.");
        }

        var userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new UnauthorizedAccessException("The Broker user SID was unavailable.");
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        var security = new PipeSecurity();
        // The DACL limits access to this user, local administrators, and the
        // operating system. Do not add a mandatory-label SACL here: setting a
        // SACL requires SeSecurityPrivilege and would make the Broker fail
        // before it can create the one-shot pipe.
        security.SetSecurityDescriptorSddlForm($"D:(A;;GA;;;SY)(A;;GA;;;{administratorsSid})(A;;GA;;;{userSid})");
        return security;
    }
}
