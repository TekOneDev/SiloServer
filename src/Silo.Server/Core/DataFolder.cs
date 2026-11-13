using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Silo.Server.Core;

/// <summary>
/// Keeps the service's own folders (settings, rules, extra plugins, catalog, index) open to SYSTEM and
/// Administrators only (0.1.4): the index holds the text of every file, whatever its permissions. Windows lets every user create files under ProgramData by default, and the service
/// starts plugin programs it finds there, so an unlocked folder let any user of the server run code as the service.
/// </summary>
public static class DataFolder
{
    public static void Lock(SettingsStore settings, Log log)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var dir in new[] { settings.Root, settings.DataDir }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Never a whole drive: a data folder set to "D:\\" is left as it is, with a warning.
            if (Path.GetPathRoot(Path.GetFullPath(dir)) == Path.GetFullPath(dir)) { log.Warn($"The data folder {dir} is a whole drive, so Silo cannot lock it. Move it into a folder of its own."); continue; }
            try { LockWindows(dir); }
            catch (Exception e)
            {
                if (IsService()) throw new UnauthorizedAccessException($"Silo Server could not lock its folder {dir} to administrators: {e.Message}", e);
                log.Warn($"Folder {dir} not locked (not running as the service): {e.Message}");
            }
        }
    }

    /// <summary>
    /// True when a plugin folder outside the install folder can be trusted: on Windows, every file in it must belong to
    /// SYSTEM, Administrators or TrustedInstaller. A file planted by an ordinary user before the folder was locked fails this.
    /// </summary>
    public static bool TrustedPlugin(string pluginDir, out string why)
    {
        why = "";
        if (!OperatingSystem.IsWindows()) return true;
        try { return TrustedWindows(pluginDir, out why); }
        catch (Exception e) { why = e.Message; return false; }
    }

    [SupportedOSPlatform("windows")]
    static bool IsService() { try { using var id = WindowsIdentity.GetCurrent(); return id.IsSystem || !Environment.UserInteractive; } catch { return true; } }

    [SupportedOSPlatform("windows")]
    static void LockWindows(string path)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var dir = new DirectoryInfo(path);
        var ds = new DirectorySecurity();
        ds.SetAccessRuleProtection(true, false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        ds.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        ds.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(ds);
        var back = dir.GetAccessControl();
        if (!back.AreAccessRulesProtected) throw new UnauthorizedAccessException("the folder still inherits permissions");
        const FileSystemRights writes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;
        foreach (FileSystemAccessRule r in back.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)r.IdentityReference;
            if (r.AccessControlType != AccessControlType.Allow || sid == system || sid == admins) continue;
            if ((r.FileSystemRights & writes) != 0) throw new UnauthorizedAccessException($"{sid.Value} can still write there");
        }
    }

    [SupportedOSPlatform("windows")]
    static bool TrustedWindows(string pluginDir, out string why)
    {
        why = "";
        var ok = new HashSet<string>
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",   // TrustedInstaller
        };
        foreach (var f in Directory.EnumerateFiles(pluginDir, "*", SearchOption.AllDirectories).Take(5000))
        {
            var owner = new FileInfo(f).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))?.Value;
            if (owner == null || !ok.Contains(owner)) { why = $"{Path.GetFileName(f)} belongs to {owner ?? "an unknown account"}, not to an administrator"; return false; }
        }
        return true;
    }
}
