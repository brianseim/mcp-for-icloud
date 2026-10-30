using System.Net;
using System.Runtime.InteropServices;
using ModelContextProtocol;

namespace McpForICloud;

/// <summary>
/// Reads the iCloud app-specific password from Windows Credential Manager,
/// falling back to the <c>MCP_FOR_ICLOUD_USER</c> / <c>MCP_FOR_ICLOUD_APP_PASSWORD</c> environment variables.
/// </summary>
public sealed class CredentialProvider(McpForICloudOptions options)
{
    private NetworkCredential? _cached;

    public NetworkCredential Get()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        NetworkCredential? fromStore = null;
        if (OperatingSystem.IsWindows())
        {
            fromStore = ReadWindowsCredential(options.CredentialTarget);
            if (fromStore is null && options.CredentialTarget == McpForICloudOptions.DefaultCredentialTarget)
            {
                fromStore = ReadWindowsCredential(McpForICloudOptions.LegacyCredentialTarget);
            }
        }

        var user = fromStore?.UserName;
        var password = fromStore?.Password;

        if (string.IsNullOrEmpty(password))
        {
            user = McpForICloudOptions.GetEnvironmentVariable("MCP_FOR_ICLOUD_USER", "AIVA_ICLOUD_USER");
            password = McpForICloudOptions.GetEnvironmentVariable("MCP_FOR_ICLOUD_APP_PASSWORD", "AIVA_ICLOUD_APP_PASSWORD");
        }

        if (string.IsNullOrEmpty(user))
        {
            user = options.AppleId;
        }

        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(user))
        {
            throw new McpException(
                $"No iCloud credentials found. Create an app-specific password at account.apple.com, then run " +
                $"`cmdkey /generic:{options.CredentialTarget} /user:<your Apple ID> /pass` and restart your MCP client. " +
                $"Config file: {options.ConfigPath} (found: {options.ConfigFound}).");
        }

        // Spaces in an app-specific password are presentation only; Apple accepts it either way.
        _cached = new NetworkCredential(user, password);
        return _cached;
    }

    private static NetworkCredential? ReadWindowsCredential(string target)
    {
        const int CredTypeGeneric = 1;
        if (!CredRead(target, CredTypeGeneric, 0, out var handle))
        {
            return null;
        }

        try
        {
            var cred = Marshal.PtrToStructure<NativeCredential>(handle);
            var password = cred.CredentialBlobSize > 0
                ? Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2)
                : null;
            return new NetworkCredential(Marshal.PtrToStringUni(cred.UserName), password);
        }
        finally
        {
            CredFree(handle);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
