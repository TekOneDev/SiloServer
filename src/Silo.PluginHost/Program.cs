using Silo.Plugins;

// Runs one of the built-in plugins out of process: Silo.PluginHost.exe <plugin id>
// Each built-in is a separate library speaking the same contract as third-party plugins.
var id = args.Length > 0 ? args[0] : "";
return id switch
{
    "silo.source.folder" => PluginRunner.Run(new Silo.Plugins.Files.FolderSource()),
    "silo.filter.pdf" => PluginRunner.Run(new Silo.Plugins.Pdf.PdfFilter()),
    "silo.filter.office" => PluginRunner.Run(new Silo.Plugins.Office.OfficeFilter()),
    "silo.filter.image" => PluginRunner.Run(new Silo.Plugins.Image.ImageFilter()),
    "silo.filter.rocscience" => PluginRunner.Run(new Silo.Plugins.Rocscience.RocscienceFilter()),
    "silo.filter.datamine" => PluginRunner.Run(new Silo.Plugins.Datamine.DatamineFilter()),
    "silo.enrich.au-geo" => PluginRunner.Run(new Silo.Plugins.AuGeo.AuGeoEnricher()),
    "silo.filter.readers" => PluginRunner.Run(new Silo.Plugins.Readers.ReadersFilter()),
    "silo.source.vault" => PluginRunner.Run(new Silo.Plugins.Vault.VaultSource { Log = PluginRunner.Info }),
    "--vault-probe" => Silo.Plugins.Vault.VaultProbe.Run(args.Skip(1).ToArray()),
    "--manifests" => Manifests(args.Length > 1 ? args[1] : "plugins"),
    "--sandbox-selftest" => SandboxTest.Parent(),
    "--sandbox-child" => SandboxTest.Child(args.Skip(1).ToArray()),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Silo.PluginHost <plugin id> | --manifests <folder> | --sandbox-selftest | --vault-probe <server> <vault> <user>");
    return 2;
}

// Writes plugins\<id>\plugin.json for every built-in (used by the build).
static int Manifests(string dir)
{
    var all = new PluginManifest[]
    {
        new Silo.Plugins.Files.FolderSource().Manifest, new Silo.Plugins.Pdf.PdfFilter().Manifest, new Silo.Plugins.Office.OfficeFilter().Manifest,
        new Silo.Plugins.Image.ImageFilter().Manifest, new Silo.Plugins.Rocscience.RocscienceFilter().Manifest,
        new Silo.Plugins.Datamine.DatamineFilter().Manifest, new Silo.Plugins.AuGeo.AuGeoEnricher().Manifest,
        new Silo.Plugins.Readers.ReadersFilter().Manifest, new Silo.Plugins.Vault.VaultSource().Manifest,
    };
    foreach (var m in all)
    {
        m.Exec = "{bin}/Silo.PluginHost.exe";
        m.Args = [m.Id];
        var d = Path.Combine(dir, m.Id);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "plugin.json"), System.Text.Json.JsonSerializer.Serialize(m, new System.Text.Json.JsonSerializerOptions(Contract.Json) { WriteIndented = true }));
    }
    return 0;
}
