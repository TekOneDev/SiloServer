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
            try { if (!IsLockedWindows(dir, out _)) LockWindows(dir); }
            catch (Exception e)
            {
                if (IsService()) throw new UnauthorizedAccessException($"Silo Server could not lock its folder {dir} to administrators: {e.Message}", e);
                log.Warn($"Folder {dir} not locked (not running as the service): {e.Message}");
            }
        }
    }

    /// <summary>
    /// Closes the data folders to the sandboxed plugins and OCR workers (0.1.5): low-integrity processes cannot read
    /// the catalog, index, settings or logs, which hold the text of every file. The plugins folder and the scratch
    /// folder are opened back up (read, and write for scratch) because plugins must load from and write there.
    /// </summary>
    public static void ProtectFromPlugins(SettingsStore settings, Log log)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var dir in new[] { settings.Root, settings.DataDir }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetPathRoot(Path.GetFullPath(dir)) == Path.GetFullPath(dir)) continue;
            try { Silo.Plugins.Sandbox.ProtectFolder(dir); }
            catch (Exception e) { log.Warn($"The data folder {dir} could not be closed to plugins: {e.Message}"); }
        }
        var extra = Path.Combine(settings.Root, "plugins");
        try { Directory.CreateDirectory(extra); Silo.Plugins.Sandbox.AllowRead(extra); }
        catch (Exception e) { log.Warn($"The plugins folder {extra} could not be opened to plugins, so plugins installed there may not start: {e.Message}"); }
    }

    /// <summary>The account the service runs as, for the log and admin page, e.g. "NT SERVICE\\SiloServer".</summary>
    public static string RunningAs()
    {
        if (!OperatingSystem.IsWindows()) return Environment.UserName;
        try { using var id = WindowsIdentity.GetCurrent(); return id.IsSystem ? "LocalSystem" : id.Name; } catch { return "unknown"; }
    }

    /// <summary>True when the service runs as LocalSystem (the 0.1.4 default, before the installer moved it to its own account).</summary>
    public static bool IsLocalSystem()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { using var id = WindowsIdentity.GetCurrent(); return id.IsSystem; } catch { return false; }
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

    /// <summary>
    /// The accounts that may write to the data folder: SYSTEM, Administrators and, since 0.1.5, the account the
    /// service runs as (its own virtual account "NT SERVICE\\SiloServer", or a domain or managed service account).
    /// </summary>
    [SupportedOSPlatform("windows")]
    static List<SecurityIdentifier> Writers()
    {
        var list = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            if (id.User != null && !list.Contains(id.User) && IsService()) list.Add(id.User);
        }
        catch { }
        return list;
    }

    const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;

    /// <summary>
    /// True when the folder already is as it should be: not inheriting, and nobody but the writers can write. The
    /// service's own account usually cannot change permissions (the installer set them), so this is checked first.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static bool IsLockedWindows(string path, out string why)
    {
        why = "";
        var back = new DirectoryInfo(path).GetAccessControl();
        if (!back.AreAccessRulesProtected) { why = "the folder still inherits permissions"; return false; }
        var ok = Writers();
        foreach (FileSystemAccessRule r in back.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)r.IdentityReference;
            if (r.AccessControlType != AccessControlType.Allow || ok.Contains(sid)) continue;
            if ((r.FileSystemRights & WriteRights) != 0) { why = $"{sid.Value} can write there"; return false; }
        }
        return true;
    }

    [SupportedOSPlatform("windows")]
    static void LockWindows(string path)
    {
        var dir = new DirectoryInfo(path);
        var ds = new DirectorySecurity();
        ds.SetAccessRuleProtection(true, false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in Writers())
            ds.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(ds);
        if (!IsLockedWindows(path, out var why)) throw new UnauthorizedAccessException(why);
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
