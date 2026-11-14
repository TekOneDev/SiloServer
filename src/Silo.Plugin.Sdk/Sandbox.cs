using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Silo.Plugins;

/// <summary>
/// Limits for the processes that open untrusted files: plugins and OCR workers (Windows; 0.1.5, ported from
/// Silo Desktop's reader process).
/// <list type="bullet">
/// <item>A job: memory cap, no child processes, killed when the service goes away.</item>
/// <item>No privileges, except "bypass traverse checking", which opening a file by its full path needs.</item>
/// <item>Low integrity: the process can read files but cannot write anywhere except its scratch folder, and cannot
/// read folders labelled with <see cref="ProtectFolder"/> (the server's settings, catalog and index).</item>
/// </list>
/// Each step is tried on its own and reported, so one that a machine refuses does not stop files being read.
/// The process still reads as the service account, so it sees the files that account can see.
/// </summary>
public static class Sandbox
{
    /// <summary>Set by the server on a plugin it wants sandboxed: "1" (job, no privileges, low integrity) or "job" (no low integrity).</summary>
    public const string EnvMode = "SILO_SANDBOX";
    /// <summary>Memory cap in MB for the job.</summary>
    public const string EnvMemory = "SILO_SANDBOX_MB";

    const uint JOB_LIMIT_ACTIVE_PROCESS = 0x8, JOB_LIMIT_PROCESS_MEMORY = 0x100, JOB_LIMIT_DIE_ON_UNHANDLED = 0x400, JOB_LIMIT_KILL_ON_CLOSE = 0x2000;
    const uint TOKEN_QUERY = 0x8, TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_ADJUST_DEFAULT = 0x80;
    const uint SE_PRIVILEGE_REMOVED = 0x4, SE_GROUP_INTEGRITY = 0x20;
    const int TokenPrivileges = 3, TokenIntegrityLevel = 25, JobObjectExtendedLimitInformation = 9;
    const uint SE_CHANGE_NOTIFY_LUID = 23;
    const uint LABEL_SECURITY_INFORMATION = 0x10, PROTECTED_SACL_SECURITY_INFORMATION = 0x40000000;
    const int SE_FILE_OBJECT = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct BASIC_LIMITS
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    struct EXTENDED_LIMITS
    {
        public BASIC_LIMITS Basic;
        public IO_COUNTERS Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_MANDATORY_LABEL { public IntPtr Sid; public uint Attributes; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObjectW(IntPtr sa, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int cls, ref EXTENDED_LIMITS info, int len);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf, int len, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr token, int cls, ref TOKEN_MANDATORY_LABEL info, int len);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, IntPtr newState, int len, IntPtr prev, IntPtr retLen);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSidToSidW(string sid, out IntPtr psid);
    [DllImport("advapi32.dll")] static extern int GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, int revision, out IntPtr sd, out int size);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetSecurityDescriptorSacl(IntPtr sd, out bool present, out IntPtr sacl, out bool defaulted);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int SetNamedSecurityInfoW(string name, int type, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    /// <summary>
    /// Called by a plugin or worker on itself before it opens its first file, when the server asked for it
    /// (<see cref="EnvMode"/>). Returns what was applied, e.g. "job, no privileges, low integrity", or null when not asked.
    /// </summary>
    public static string? ApplyIfAsked()
    {
        var mode = Environment.GetEnvironmentVariable(EnvMode);
        if (string.IsNullOrEmpty(mode) || mode == "0") return null;
        long mb = long.TryParse(Environment.GetEnvironmentVariable(EnvMemory), out var m) && m > 0 ? m : 4096;
        return Apply(mb * 1024 * 1024, lowIntegrity: mode != "job");
    }

    public static string Apply(long memoryLimitBytes, bool lowIntegrity)
    {
        if (!OperatingSystem.IsWindows()) return "none (not Windows)";
        var done = new List<string>();
        void Step(string name, Action a) { try { a(); done.Add(name); } catch (Exception e) { done.Add($"{name} FAILED ({e.Message})"); } }

        Step("job", () => NewJob(GetCurrentProcess(), memoryLimitBytes));

        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES | TOKEN_ADJUST_DEFAULT, out var token))
        {
            done.Add($"token FAILED ({new Win32Exception().Message})");
            return string.Join(", ", done);
        }
        try
        {
            Step("no privileges", () =>
            {
                GetTokenInformation(token, TokenPrivileges, IntPtr.Zero, 0, out var need);
                var buf = Marshal.AllocHGlobal(need);
                try
                {
                    if (!GetTokenInformation(token, TokenPrivileges, buf, need, out _)) throw new Win32Exception();
                    int count = Marshal.ReadInt32(buf);
                    for (int i = 0; i < count; i++)
                    {
                        int at = 4 + i * 12;   // LUID (8 bytes) + attributes (4)
                        uint low = (uint)Marshal.ReadInt32(buf, at); int high = Marshal.ReadInt32(buf, at + 4);
                        if (low == SE_CHANGE_NOTIFY_LUID && high == 0) continue;
                        Marshal.WriteInt32(buf, at + 8, (int)SE_PRIVILEGE_REMOVED);
                    }
                    if (!AdjustTokenPrivileges(token, false, buf, need, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
                    var err = Marshal.GetLastWin32Error();
                    if (err != 0) throw new Win32Exception(err);
                }
                finally { Marshal.FreeHGlobal(buf); }
            });

            if (lowIntegrity)
                Step("low integrity", () =>
                {
                    if (!ConvertStringSidToSidW("S-1-16-4096", out var sid)) throw new Win32Exception();
                    try
                    {
                        var label = new TOKEN_MANDATORY_LABEL { Sid = sid, Attributes = SE_GROUP_INTEGRITY };
                        if (!SetTokenInformation(token, TokenIntegrityLevel, ref label, Marshal.SizeOf<TOKEN_MANDATORY_LABEL>() + GetLengthSid(sid))) throw new Win32Exception();
                    }
                    finally { LocalFree(sid); }
                });
        }
        finally { CloseHandle(token); }
        return string.Join(", ", done);
    }

    /// <summary>
    /// Puts a process the server just started into a job of its own (memory cap, no child processes, killed with the
    /// server). Done from outside, so it also covers third-party plugins that do not sandbox themselves.
    /// Returns null on success, or why it failed.
    /// </summary>
    public static string? Contain(Process p, long memoryLimitBytes)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { NewJob(p.Handle, memoryLimitBytes); return null; }
        catch (Exception e) { return e.Message; }
    }

    static void NewJob(IntPtr process, long memoryLimitBytes)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception();
        var info = new EXTENDED_LIMITS();
        info.Basic.LimitFlags = JOB_LIMIT_ACTIVE_PROCESS | JOB_LIMIT_PROCESS_MEMORY | JOB_LIMIT_DIE_ON_UNHANDLED | JOB_LIMIT_KILL_ON_CLOSE;
        info.Basic.ActiveProcessLimit = 1;
        info.ProcessMemoryLimit = (UIntPtr)(ulong)memoryLimitBytes;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<EXTENDED_LIMITS>())) { var e = new Win32Exception(); CloseHandle(job); throw e; }
        if (!AssignProcessToJobObject(job, process)) { var e = new Win32Exception(); CloseHandle(job); throw e; }
        // The handle stays open for the life of this process on purpose: closing the last handle kills the job.
    }

    static void SetLabel(string path, string sddl, bool blockInherited)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var sd, out _)) throw new Win32Exception();
        try
        {
            if (!GetSecurityDescriptorSacl(sd, out var present, out var sacl, out _) || !present) throw new Win32Exception();
            uint what = LABEL_SECURITY_INFORMATION | (blockInherited ? PROTECTED_SACL_SECURITY_INFORMATION : 0);
            var rc = SetNamedSecurityInfoW(path, SE_FILE_OBJECT, what, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
            if (rc != 0) throw new Win32Exception(rc);
        }
        finally { LocalFree(sd); }
    }

    /// <summary>A scratch folder: the one place a low-integrity process may write. Emptied first; does not take the protected label of its parent.</summary>
    public static void PrepareScratch(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { }
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsWindows()) SetLabel(dir, "S:(ML;OICI;NW;;;LW)", blockInherited: true);
    }

    /// <summary>Keeps low-integrity processes from reading a folder and everything in it (the server's own data).</summary>
    public static void ProtectFolder(string dir)
    {
        if (OperatingSystem.IsWindows()) SetLabel(dir, "S:(ML;OICI;NWNR;;;ME)", blockInherited: false);
    }

    /// <summary>
    /// Opens a folder inside a protected one back up for reading by low-integrity processes (plugin programs that an
    /// administrator copied into the data folder must be able to load their own files). Writing stays closed.
    /// </summary>
    public static void AllowRead(string dir)
    {
        if (OperatingSystem.IsWindows()) SetLabel(dir, "S:(ML;OICI;NW;;;ME)", blockInherited: true);
    }
}
