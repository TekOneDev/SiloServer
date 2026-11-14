# Silo Server 0.1.7 (beta)

A search service for mining data: exploration reports, drilling data, and technical files from packages such as Datamine and Rocscience. It runs as a Windows service. You use it through a Google-style web page at **http://localhost:47820/**.

## Install

1. Unzip `Silo-Server-0.1.7-win-x64.zip`.
2. Double-click **Install Silo Server.cmd**. Windows asks for administrator permission once.
3. When it finishes, the search page opens. Indexing starts straight away. Watch progress at **http://localhost:47820/admin**.

By default it indexes the public mining library at `D:\PublicMiningData`. To use another folder:

- Add it on the admin page with **Add a folder**, or
- Install with `-Library "E:\Library"`. Add this after `install.ps1` in the .cmd file, or run `install.ps1 -Library ...` from PowerShell.

To let other PCs on the network use it, install with `-Lan`. This listens on port 47820 and opens the firewall for domain and private networks.

To encrypt searches on the network, add `-Https`. The server then also listens on port 47821 with HTTPS, and other PCs that come in on plain HTTP are sent there (this computer keeps plain HTTP). The installer makes a "Silo Server" certificate for this computer and trusts it here. Other PCs must trust it too: deploy `C:\Program Files\Silo Server\silo-server.cer` by Group Policy. Better, use a certificate from your company's certificate authority: `-Https -Certificate <thumbprint>`.

### The service account (0.1.5)

The service no longer runs as LocalSystem. By default it runs as its own virtual account, `NT SERVICE\SiloServer`, which has almost no rights on the computer. The installer gives it:

- full control of its data folder, `C:\ProgramData\SiloServer`;
- read access to the program folder and to every local folder in its sources.

Shares on other servers are read as the computer's own domain account (`SERVER$`), so give that account Read on them. Or run the service as a group managed service account that can read them: `-Account CORP\svc-silo$`. An ordinary domain account also works (`-Account CORP\svc-silo`, the installer asks for its password). `-Account LocalSystem` puts back the old behaviour, which is not recommended.

When you add a folder on the admin page that the service cannot read, the page says which account needs access.

To uninstall, use **Settings > Apps > Silo Server (beta)**, or run `uninstall.ps1`. The index in `C:\ProgramData\SiloServer` is kept unless you add `-RemoveData`.

## What you get

- **Search page**
  - Results are grouped by report, with snippets that show the page number.
  - "Did you mean" suggestions, and typing suggestions for tenements, commodities and companies.
  - Facets on the left: source, commodity, report type, year histogram, state, tenement, company, rock type, method, file type and location.
- **Preview pane**
  - Highlighted page text with jump-to-match buttons.
  - The PDF opened at the matching page, report metadata, and a mini map.
  - The other files in the report, and the licence and attribution for each item.
- **Map view**
  - Reports plotted and clustered.
  - **Search this area**, or **Draw an area** to filter by location.
  - Streets or satellite basemaps when online. A built-in Australia outline is used when offline.
- **Admin page**: progress, sources, plugins (each can be turned on or off), recent problems, live log and settings.
- **Light and dark themes.** Keyboard: `/` focuses search, `j`/`k` move through results, `Esc` closes the preview.

### Search syntax

| Type | Finds |
|---|---|
| `gold RC drilling` | All the words. Mining terms expand (RC, BIF, Au, sulphide/sulfide…) |
| `"banded iron formation"` · `-laterite` · `a OR b` | Phrase, exclude, either |
| `E28/589`, `E 8/1622`, `EPM 17533` | Tenements in any common form, matched exactly |
| `A59033`, `CR089328` | Report numbers |
| `commodity:cu` `type:annual` `year:1990..2005` `after:2015` | Commodity, report type, year |
| `company:"Western Mining"` `asx:IGO` `rock:komatiite` `method:aircore` | Company, ASX listing, rock type, method |
| `source:wamex` `state:qld` `filetype:spreadsheet` `ext:dm` | Source, state, file kind |
| `has:location` `in:115,-32,122,-26` | Located reports, or reports inside an area |

## How it works

```
 sources (plugins) ──► catalog (SQLite) ──► extract (filter plugins) ──► OCR (Windows) ──► enrich (plugins) ──► index (Lucene)
                                                                                                                 │
                         web page / MCP / other clients ◄──────────── HTTP API /api/v1 ◄────────────────────────┘
```

- **Catalog (`catalog.db`)** is the record of truth. It holds every file's state, extracted page text (compressed), fields, entities and location. The index can always be rebuilt from it without reading the files again.
- **Index**
  - Lucene.NET with BM25.
  - One document per file, plus one per page for snippets and page jumps.
  - Facets with drill-sideways counts. Identical copies are folded together.
  - Permissions are trimmed in the query and fail closed: an item with no allow token is invisible.
- **Rules (`rules.json`)** are applied by the core at index time to every source. They cover commodity aliases, report types, tenement formats per state, rock types, methods and synonyms. To customise them, copy the file to `C:\ProgramData\SiloServer\rules.json`, edit it, and click **Reload rules**.
- **Locations** come from several places:
  1. Coordinates written in the text: decimal or DMS, map-frame graticules, MGA grids with a zone, and 1:250 000 map sheet codes (approximate).
  2. Coordinates in data files.
  3. The `silo.enrich.au-geo` plugin, which looks up WAMEX and GSQ report footprints online. These win over the others.
- **OCR** uses the Windows engine (no install needed). It runs in worker processes, and sideways scans are retried after turning them.
- **Sandbox (0.1.5).** Plugins and OCR workers open untrusted files, so each runs in a job (memory cap, no other programs, killed with the service) with all privileges removed. Plugins also run at low integrity: they can read files but cannot write anywhere except a scratch folder, and cannot read the server's catalog, index, settings or logs. A plugin that cannot work like this says `"sandbox": false` in its manifest; the admin page shows each plugin's sandbox. OCR workers run at low integrity only with `"ocrLowIntegrity": true`, until that has been checked with Windows OCR on a real server. `Silo.PluginHost.exe --sandbox-selftest` checks the sandbox on any machine.
- **Permissions**
  - Folder sources can use NTFS permissions (`access: ntfs`) or be public.
  - With `auth: windows`, people sign in with their domain account and see only what they can read.
  - With `auth: anonymous`, people see public items only.
  - The admin page works on the server itself, or for Windows administrators.

## Plugins

Every data source and file type is a plugin that runs in its own process. Contract `silo.plugin/1`:

- A plugin is a folder with a `plugin.json` manifest and an executable.
- Plugins talk JSON lines over stdin/stdout.
- A plugin that crashes or hangs is restarted, and the file is marked failed. The server keeps going.

Built in:

| Plugin | Handles |
|---|---|
| `silo.source.folder` | Folders and shares. Reads sidecars (`item.json`), NTFS ACLs, and live change watching |
| `silo.source.vault` | Autodesk Vault (0.1.7): files with Vault paths, properties, revisions and lifecycle states, with Vault permissions. See Autodesk Vault below |
| `silo.filter.pdf` | PDF text by page. Scanned pages go to OCR |
| `silo.filter.office` | Word, Excel, PowerPoint, HTML, RTF, CSV and text files (LAS, XML, JSON…). Recognises drillhole collars, assays, lithology logs and surveys in tables |
| `silo.filter.image` | Photos and scans: EXIF/IPTC, GPS location. OCR |
| `silo.filter.datamine` | Datamine `.dm`/`.dmx`: file type, fields, record count, block size, model extents |
| `silo.filter.rocscience` | RS3 compute, model database, results and engine logs: materials, stages, strength models |
| `silo.filter.readers` | The types Silo Desktop reads, from the shared SiloCommon readers (0.1.6): Word/Excel/PowerPoint 97-2003, OpenDocument, `.eml`/`.msg`, zip listings, DWG/DXF/DGN/STEP drawings, Inventor `.ipt`/`.iam`/`.idw`/`.ipn` iProperties and files used (0.1.7), 3D print and mesh files, Datamine projects and `.dmx`, Leapfrog, Slide/RS2, OMF and Surfer grids. Facets: software, model type, has results |
| `silo.enrich.au-geo` | Report footprints from WA (DEMIRS SLIP) and QLD (GSQ open data) |

To add one:

1. Reference `Silo.Plugin.Sdk` and subclass `FilterPlugin`, `SourcePlugin` or `EnricherPlugin`.
2. Make `Main` the single line `return PluginRunner.Run(new MyPlugin());`.
3. Drop the folder into `C:\ProgramData\SiloServer\plugins\<id>\` with a `plugin.json`.

A plugin can be written in any language that reads and writes JSON lines. A filter declares `fields` in its manifest, and those fields become facets and `x.<name>:` search operators automatically. A filter whose version changes causes the files it handles to be re-read.

## Autodesk Vault (0.1.7)

Vault keeps files under numbered names in its filestore, with the real names, folders, properties and permissions in its database, so Silo reads Vault through Vault's own web services rather than the share. It works with Vault Basic, Workgroup and Professional, ships no Autodesk code, and only reads: it signs in read-only and never checks out, changes or deletes anything.

1. Make a Vault user for Silo, such as `silo-indexer`, that can read every folder (the Administrator role is simplest; Silo only reads).
2. Check the Vault first: `Silo.PluginHost.exe --vault-probe http://vaultserver Vault silo-indexer` (add `--windows` to sign in as the current Windows account). It asks for the password, looks at a sample of folders, files, properties and permissions, downloads one small file, and writes `vault-probe.txt`. It changes nothing.
3. On the admin page, **Add Autodesk Vault** with the server address, vault name and account. A full pass runs every 30 minutes; only files with a new version are read again.

What you get:

- Each file once, at its latest released version (or its latest version when it was never released, or always with `"version": "latest"`; `"unreleased": "skip"` leaves never-released files out). Files with no lifecycle at all (Vault Basic) are always listed.
- The Vault path on the card (**Copy Vault path**), and **Open in Vault**, which opens it in Vault Explorer (`"openUrl"` takes a template with `{server}`, `{vault}`, `{path}`, `{pathEncoded}`, `{masterId}`, `{fileId}` for the web client or anything else).
- Facets and `x.` operators: `x.vault_part:`, `x.vault_rev:`, `x.vault_state:`, `x.vault_category:`, `x.vault_folder:`, `x.vault_by:`, `x.vault_project:`. Every other Vault property is searchable text.
- The text inside each file, read by the normal filters. Silo fetches a copy into the plugins' scratch folder, reads it and deletes it. Downloads from the preview pane work the same way. Scanned pages in Vault PDFs are not OCR'd yet.

Permissions follow Vault and fail closed:

- Read = Allow on a file's or folder's ACL becomes that user's or group's Windows SID; Read = Deny goes to the deny list, which always wins.
- Vault users and groups that came from Active Directory map to their AD SIDs. Vault-only groups are expanded to their members. Vault-only users get no access unless `"userMap": { "vaultname": "DOMAIN\\winname" }` names their Windows account (`"matchByName": true` tries the same name in `"domain"`).
- A file with no ACL anywhere (Vault's default) is visible to every active Vault user with a Windows account, or to the group named in `"everyone"`.
- When a lifecycle state has its own security, a user needs Read from both the folder and the state. If Silo cannot read the state's security the file is hidden (`"stateSecurity": "ignore"` uses folder security alone).
- Accounts that could not be mapped are listed in the log after each scan.

Other settings: `"root"` (a Vault folder such as `$/Designs`), `"exclude"` (patterns on Vault paths and names), `"windowsAuth"`, `"serviceVersion"` (found automatically), `"aclEntity"` (`master`, or `file`), `"readPermissionId"` (1). The password is kept in `settings.json` on the server, which only the service and administrators can read, and the admin page never shows it.

## API

`GET /api/v1/` describes the endpoints. The main ones are:

- `GET|POST /api/v1/search`
- `GET /api/v1/suggest`
- `POST /api/v1/map`
- `GET /api/v1/items/{id}`, `.../pages/{n}` and `.../file`

The web page uses only these endpoints, so an MCP server or other tools can sit on the same API.

## Settings

Settings live in `C:\ProgramData\SiloServer\settings.json`. You can also change them on the admin page.

- `listen`: the addresses the server listens on.
- `auth`: `anonymous` or `windows`.
- `extractWorkers` and `ocrWorkers`: how many workers read files and run OCR.
- `ocr` and `enrich`: turn OCR and online location lookups on or off.
- `sources`, `lookups` (CSV metadata joins, such as the ASX company list) and `pluginDirs`.
- `certificate`: for `https://` listen addresses, a thumbprint or subject in the computer's certificate store, or a `.pfx` path. Empty means the newest valid certificate named "Silo Server". Without a usable certificate the server listens on this computer only and logs why.
- `httpsRedirect`: send other PCs from HTTP to HTTPS (default on).
- `sandboxPlugins`, `pluginMemoryMB` (default 4096) and `ocrLowIntegrity`: see Sandbox above.

Logs are in `C:\ProgramData\SiloServer\logs`.

## Build

The plugin sandbox (and next, the file readers) is shared with Silo Desktop and lives in [SiloCommon](https://github.com/TekOneDev/SiloCommon), a git submodule in `common/`. Clone with `git clone --recursive`, or run `git submodule update --init` once.

Run `./build.sh` with the .NET 10 SDK. It runs the tests, publishes the server and plugin host for win-x64 (self-contained), writes the plugin manifests, and zips the package into `dist/`.

## Known limits in this beta

- OCR and the online footprint lookups only run on Windows with internet access. The build machine could reach neither, so they still need checking on a real PC.
- A share on another server needs the service's account to be able to read it (see The service account). Change the account by running the installer again with `-Account`.
- The service account, HTTPS and the sandbox (0.1.5) have passed the Windows CI checks but not yet a real server install.
- Semantic (meaning-based) search is designed in as a hook, but it is not built yet.
- The Vault source (0.1.7) is built from Autodesk's published API names and tested against a pretend Vault, not yet a real one. Run `--vault-probe` on a new Vault and send the report if anything looks wrong.
