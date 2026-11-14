using Silo.Isolation;
using System.Diagnostics;
using System.Text;
using Silo.Plugins;

/// <summary>
/// "Silo.PluginHost.exe --sandbox-selftest" (0.1.5): proves on a real Windows machine (the CI runner, or a customer's
/// server) that a sandboxed plugin can read ordinary files and write its scratch folder, but cannot read the server's
/// protected data, write anywhere else, or start other programs. Exit code 0 when every check holds.
/// </summary>
static class SandboxTest
{
    public static int Parent()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("The sandbox only exists on Windows; nothing to test."); return 0; }
        var root = Path.Combine(Path.GetTempPath(), "silo-sandbox-test-" + Guid.NewGuid().ToString("N")[..8]);
        var data = Path.Combine(root, "data");
        var secret = Path.Combine(data, "catalog.db");
        var plugins = Path.Combine(data, "plugins");
        var pluginFile = Path.Combine(plugins, "plugin.json");
        var scratch = Path.Combine(data, "plugin-scratch");
        var open = Path.Combine(root, "library");
        var openFile = Path.Combine(open, "report.txt");
        try
        {
            Directory.CreateDirectory(data); Directory.CreateDirectory(open); Directory.CreateDirectory(plugins);
            File.WriteAllText(secret, "secret");
            File.WriteAllText(pluginFile, "{}");
            File.WriteAllText(openFile, "readable");
            // The same steps the server takes at start.
            Sandbox.ProtectFolder(data);
            Sandbox.AllowRead(plugins);
            Sandbox.PrepareScratch(scratch);

            var exe = Environment.ProcessPath!;
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false) };
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(typeof(SandboxTest).Assembly.Location);
            foreach (var a in new[] { "--sandbox-child", secret, pluginFile, scratch, openFile, data }) psi.ArgumentList.Add(a);
            psi.Environment[Sandbox.EnvMode] = "1";
            psi.Environment[Sandbox.EnvMemory] = "1024";
            psi.Environment["TEMP"] = psi.Environment["TMP"] = scratch;
            using var p = Process.Start(psi)!;
            var job = Sandbox.Contain(p, 1024L * 1024 * 1024);
            var output = p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            Console.Write(output);
            if (err.Length > 0) Console.Error.Write(err);
            Console.WriteLine(job == null ? "PASS  the server put the plugin in a job of its own" : $"FAIL  outside job: {job}");
            bool ok = p.ExitCode == 0 && job == null;
            Console.WriteLine(ok ? "Sandbox self-test passed." : "Sandbox self-test FAILED. (SILO-SRV-PLUG-0013)");
            return ok ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    public static int Child(string[] a)
    {
        string secret = a[0], pluginFile = a[1], scratch = a[2], openFile = a[3], data = a[4];
        var applied = Sandbox.ApplyIfAsked() ?? "nothing (not asked)";
        int failures = 0;
        void Check(string what, bool expectOk, Action act)
        {
            string got;
            bool worked;
            try { act(); worked = true; got = "allowed"; }
            catch (Exception e) { worked = false; got = "refused (" + e.GetType().Name + ")"; }
            bool pass = worked == expectOk;
            if (!pass) failures++;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {what}: {got}");
        }
        Console.WriteLine("Applied: " + applied);
        if (applied.Contains("FAILED") || !applied.Contains("low integrity")) { Console.WriteLine("FAIL  not every sandbox step was applied"); failures++; }
        Check("read an ordinary file", true, () => File.ReadAllText(openFile));
        Check("read a plugin's own files in the data folder", true, () => File.ReadAllText(pluginFile));
        Check("write to the scratch folder", true, () => File.WriteAllText(Path.Combine(scratch, "x.tmp"), "x"));
        Check("read the server's catalog", false, () => File.ReadAllText(secret));
        Check("write into the data folder", false, () => File.WriteAllText(Path.Combine(data, "planted.txt"), "x"));
        Check("write next to an ordinary file", false, () => File.WriteAllText(openFile + ".planted", "x"));
        Check("start another program", false, () =>
        {
            using var c = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
            c.WaitForExit(5000);
        });
        return failures == 0 ? 0 : 1;
    }
}
