# Silo Server 0.1.3 (beta)

A search service for mining data: exploration reports, drilling data, and technical files from packages such as Datamine and Rocscience. It runs as a Windows service. You use it through a Google-style web page at **http://localhost:47820/**.

## Install

1. Unzip `Silo-Server-0.1.3-win-x64.zip`.
2. Double-click **Install Silo Server.cmd**. Windows asks for administrator permission once.
3. When it finishes, the search page opens. Indexing starts straight away. Watch progress at **http://localhost:47820/admin**.

By default it indexes the public mining library at `D:\PublicMiningData`. To use another folder:

- Add it on the admin page with **Add a folder**, or
- Install with `-Library "E:\Library"`. Add this after `install.ps1` in the .cmd file, or run `install.ps1 -Library ...` from PowerShell.

To let other PCs on the network use it, install with `-Lan`. This listens on port 47820 and opens the firewall for domain and private networks.

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
| `silo.filter.pdf` | PDF text by page. Scanned pages go to OCR |
| `silo.filter.office` | Word, Excel, PowerPoint, HTML, RTF, CSV and text files (LAS, XML, JSON…). Recognises drillhole collars, assays, lithology logs and surveys in tables |
| `silo.filter.image` | Photos and scans: EXIF/IPTC, GPS location. OCR |
| `silo.filter.datamine` | Datamine `.dm`/`.dmx`: file type, fields, record count, block size, model extents |
| `silo.filter.rocscience` | RS3 compute, model database, results and engine logs: materials, stages, strength models |
| `silo.enrich.au-geo` | Report footprints from WA (DEMIRS SLIP) and QLD (GSQ open data) |

To add one:

1. Reference `Silo.Plugin.Sdk` and subclass `FilterPlugin`, `SourcePlugin` or `EnricherPlugin`.
2. Make `Main` the single line `return PluginRunner.Run(new MyPlugin());`.
3. Drop the folder into `C:\ProgramData\SiloServer\plugins\<id>\` with a `plugin.json`.

A plugin can be written in any language that reads and writes JSON lines. A filter declares `fields` in its manifest, and those fields become facets and `x.<name>:` search operators automatically. A filter whose version changes causes the files it handles to be re-read.

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

Logs are in `C:\ProgramData\SiloServer\logs`.

## Build

Run `./build.sh` with the .NET 8 SDK. It runs the tests, publishes the server and plugin host for win-x64 (self-contained), writes the plugin manifests, and zips the package into `dist/`.

## Known limits in this beta

- OCR and the online footprint lookups only run on Windows with internet access. The build machine could reach neither, so they still need checking on a real PC.
- A share on another server needs the service to run as a domain account that can read it. Change the account in `services.msc`, under SiloServer > Log On.
- Semantic (meaning-based) search is designed in as a hook, but it is not built yet.
