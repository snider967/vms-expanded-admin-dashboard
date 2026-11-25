using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;

internal static class PrivateMediaFiles
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static string Root(string dataPath) => Path.Combine(dataPath, "remote-admin-desk", "media-management");

    internal static void EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User ?? throw new UnauthorizedAccessException();
            var access = new DirectorySecurity();
            access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            access.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(access);
        }
        else
        {
            Directory.CreateDirectory(path, DirectoryMode);
        }
        VerifyDirectory(path);
    }

    internal static FileStream CreateFile(string path)
    {
        var options = new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.CreateNew, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileModeBits;
        return new FileStream(path, options);
    }

    internal static void VerifyDirectory(string path) => Verify(path, directory: true, DirectoryMode);
    internal static void VerifyFile(string path) => Verify(path, directory: false, FileModeBits);

    private static void Verify(string path, bool directory, UnixFileMode maximum)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new UnauthorizedAccessException();
        if (OperatingSystem.IsWindows())
        {
            VerifyWindowsAccess(path, directory);
        }
        else if ((File.GetUnixFileMode(path) & ~maximum) != 0)
        {
            throw new UnauthorizedAccessException();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsAccess(string path, bool directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new UnauthorizedAccessException();
        FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        if (acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(sid)))
            throw new UnauthorizedAccessException();
    }
}
