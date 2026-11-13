# solidworks-mcp

Lets an AI agent drive SOLIDWORKS through the [Model Context Protocol](https://modelcontextprotocol.io). It has three parts:

- **`SwAgent.AddIn`** runs inside SOLIDWORKS (.NET Framework 4.8). It serves JSON-RPC on `\\.\pipe\solidworks-mcp-<pid>` and runs every request on SOLIDWORKS' UI thread.
- **`SwAgent.Client`** is the one library that talks to that pipe.
- **`swmcp`** is the MCP server (`swmcp serve`, the default) and the command line (`swmcp call`, `swmcp addin ...`).

It offers these tools:

- `sw_status` says what SOLIDWORKS is doing: whether it runs, what the add-in is running and has queued, any open dialog, the open documents and, when SOLIDWORKS is idle, the active document. It always answers, even while SOLIDWORKS is busy, stopped in a dialog, starting or absent.
- `sw_dialog_respond` presses a button on the modal dialog SOLIDWORKS is showing.
- `sw_events` returns what happened since a cursor: documents opened, saved, closed and changed, the user's selection, dialogs, and the late outcomes of operations.
- `sw_reconnect` connects again, to a named SOLIDWORKS when several run.
- Documents: `sw_document_new` (a part from your default template, saved at once when given a path), `sw_document_open`, `sw_document_save`, `sw_document_save_as`, `sw_document_close` and `sw_document_rebuild`.
- Inspection of parts: `sw_model_tree`, `sw_model_mass`, `sw_model_bounding_box`, `sw_model_rebuild_status` and `sw_model_find`.
- `sw_selection_get` reads what is selected in a part.
- Sketches: `sw_sketch_create` draws a whole sketch in one call (lines, centerlines, arcs, circles, rectangles, polygons, slots, splines and points, then relations, then named driving dimensions), `sw_sketch_read` reads one back and `sw_sketch_modify` adds to or removes from one.
- Dimensions: `sw_model_dimensions` lists them and `sw_dimension_set` changes a driving one.
- Features: `sw_feature_extrude` (a boss or a cut, blind, through all, up to next, up to a vertex or surface, or mid-plane, with an optional draft, second direction and thin wall), `sw_feature_revolve`, `sw_feature_fillet` (constant radius, on edges or faces), `sw_feature_chamfer` (distance and angle, or two distances), `sw_feature_suppress` and `sw_feature_delete`.
- More features: `sw_feature_sweep` (a profile along a path, with guide curves), `sw_feature_loft` (through two or more profiles, with guide curves), `sw_feature_rib`, `sw_feature_shell`, `sw_feature_draft` (about a neutral plane), `sw_feature_hole` (Hole Wizard: simple, counterbore, countersink and tapped holes to ISO or ANSI metric, at points on a face), `sw_feature_linear_pattern` and `sw_feature_circular_pattern` (of features, or of faces as a geometry pattern), and `sw_feature_mirror` (features, or the whole body).
- Configurations: `sw_configuration_list`, `sw_configuration_add` and `sw_configuration_activate`. `sw_feature_suppress` and `sw_dimension_set` act in the active configuration.
- Reference geometry: `sw_refgeom_plane` (offset, angle, three points, parallel through a point, normal to a curve), `sw_refgeom_axis` and `sw_refgeom_point`.
- `sw_model_measure` gives the distance, closest points, centre distance and angle between two faces, edges, vertices, planes, axes or points.
- `sw_export_part` writes a copy of a part as STEP (AP214 or AP242), Parasolid (`.x_t`), binary STL or 3MF.
- Sheet metal: `sw_sheetmetal_base_flange` starts a sheet-metal part from a sketch, `sw_sheetmetal_set_parameters` changes its thickness, bend radius, bend allowance and relief, and `sw_sheetmetal_read` reads it as a sheet-metal designer does (every bend, and the flat size). `sw_sheetmetal_edge_flange`, `sw_sheetmetal_miter_flange`, `sw_sheetmetal_hem` and `sw_sheetmetal_sketched_bend` add flanges, hems and bends; `sw_sheetmetal_flatten` flattens and folds the part; `sw_sheetmetal_unfold` and `sw_sheetmetal_fold` unfold chosen bends to cut across them; `sw_sheetmetal_convert` turns an L or U solid into sheet metal. `sw_export_flat_pattern` writes the flat pattern as DXF or DWG for the shop. See **Sheet metal**.
- Mates: `sw_mate_add` (coincident, parallel, perpendicular, tangent, concentric, lock, distance and angle), `sw_mate_edit`, `sw_mate_delete` and `sw_mate_list`. Each new or edited mate is checked by measuring the geometry it constrains, and rolled back with every moved component put back when it does not hold. `sw_assembly_dof` gives each component's constrained state (fixed, fully, under or over defined). **Known gap:** it does not report free motion for a component with mates, because SOLIDWORKS 2026 SP4's `GetRemainingDOFs` answers `unavailable` for every mated component. Counts are given only for fixed, fully defined and unmated components, and `note` says so otherwise.
- `sw_api_call`, the escape hatch, calls any member of SOLIDWORKS' API the other tools do not cover yet. It can be turned off (see **The escape hatch**).

There is one tool per add-in operation, named `sw_` and the operation with underscores (`document.save_as` is `sw_document_save_as`).

Lengths are millimetres and angles degrees everywhere except in `sw_api_call`, which speaks SOLIDWORKS' own metres and radians.

### References

Faces, edges, vertices, features, planes, sketches, sketch entities and dimensions are named by references: strings such as `r1.face.AQAAAB…`, given in results (`sw_model_find`, `sw_model_tree`, `sw_selection_get`, `sw_sketch_create`). The part after `r1.` says what kind of thing it names; the rest is opaque. A reference survives rebuilds, saving under a new name, and closing and opening the part again. When the thing it named is gone (deleted, suppressed, or in another part), the operation answers `reference_unresolved` with what the thing was, and never picks something nearby instead. Feature names (`Boss-Extrude1`) work too, and `front`, `top`, `right` and `origin` name the template's own planes and origin in any language.

### Changing a part

Every tool that changes a part (the sketch, dimension, feature, reference geometry and sheet-metal tools) works the same way:

- It does not start while you are editing a sketch in the part or have a command's PropertyManager open: it answers `busy` with `user_editing` and changes nothing. It never closes your sketch or command. A command open anywhere in SOLIDWORKS counts, not only one in that part: SOLIDWORKS reports the open command for the whole application.
- It checks its own result against what was asked, by the part's geometry rather than SOLIDWORKS' return value (a sketch holds what was drawn, every dimension reads back at its value, nothing new fails to rebuild), and if anything is wrong it takes the change back, so the part is as it was. If taking it back fails too, it says so (`rollback_failed`) and leaves the part open with its changes for you to look at.
- It returns the evidence: what it made and edited, the part's rebuild errors, and its volume and body count before and after.

### Sketches

`sw_sketch_create` draws, relates and dimensions a whole sketch and closes it in one call, so SOLIDWORKS is never left editing a sketch between calls. Each entity has the agent's own short id; relations and dimensions name entities by id, with `.start`, `.end` or `.center` for their points (`l1.end`), a rectangle's sides as `.bottom`, `.right`, `.top` and `.left`, `origin` for the sketch's origin, or a reference to one of the part's edges or vertices. Coordinates are millimetres in the sketch's own plane; the result says where that plane is in the part. An under-defined sketch is allowed and reported; an over-defined one is taken back.

### Features

Each feature tool checks the geometry it made, as well as the checks every change makes:

- Extrudes and revolves: the part's volume grows (boss) or shrinks (cut). A blind or mid-plane boss without draft on a part with no body yet must add exactly the profile's area times its depth, and a boss revolved about a line in its own sketch exactly Pappus' volume (angle × area × the centroid's distance from the axis). Profiles of lines and circles are measured; arcs and splines get the sign check only.
- Fillets and chamfers: every edge named is gone afterwards (its reference no longer resolves), the feature made a face for each, and the volume changed.
- Reference planes, axes and points: they are where the request put them (a plane's normal and offset, the angle to its plane, the points on it; an axis on its planes' line or its cylinder's axis; a point at a circle's centre or on its edges), and the volume is unchanged.
- Sweeps and lofts: the part has a body and its volume grew (boss) or shrank (cut); a boss swept along one straight line on an empty part must be exactly the profile's area times the line's length along the profile's normal.
- Ribs grow the part; shells open the faces named (their references no longer resolve) and shrink the part (inward) or grow it (outward); a draft leaves every face it tilted at 90° less the draft angle to the neutral plane's normal.
- Hole Wizard holes: the add-in sketches a point at each position on the face first; afterwards there must be one hole (a cylinder, or a counterbore's or countersink's coaxial cylinders) on each position's axis, and the part shrank. Sizes are the Hole Wizard's own names (`M5`, `Ø5.0`).
- Patterns and mirrors: a pattern has the instances asked, and the volume changed by the seeds' own volume once for each new instance (a face pattern: only that it changed). The add-in measures the seeds by suppressing them for a moment and putting them back before it makes the pattern; it skips that, and checks only that the volume changed, when another feature is built on a seed. An instance that runs off the part or into another changes the volume by a different amount, and the pattern is taken back. A mirrored body must grow the part and at most double it.
- Configurations: the configuration added or activated is the active one afterwards; otherwise the one before is active again.
- `sw_feature_suppress` checks each feature reads as asked after the rebuild, and puts them back otherwise. `sw_feature_delete` refuses a feature that others are built on (at any depth) unless `children: delete`, and a deleted feature cannot be made again, so a check that fails afterwards is `rollback_failed`.

A feature SOLIDWORKS does not make (a fillet too big for its edge, a cut that misses the part) answers `operation_failed` with SOLIDWORKS' rebuild errors, and nothing is left of it. The sketch an extrude or revolve used is kept.

### Sheet metal

A sheet-metal part is a part with a `Sheet-Metal` feature: `sw_sheetmetal_base_flange` makes one from a sketch, and `sw_sheetmetal_convert` from a solid of constant thickness. The sheet-metal tools answer `wrong_document_type` (with `needs: sheet_metal`) on any other part. A part has one sheet-metal body: its thickness belongs to the part, so a second base flange or a convert on a sheet-metal part is refused.

- **Folded to change.** Flanges, hems, bends, convert and parameter changes refuse a flattened part (`invalid_argument`); fold it with `sw_sheetmetal_flatten {flat: false}` first. Nothing is folded or flattened on the agent's behalf. `sw_feature_extrude` cuts work in either state.
- **Checked by the bend arithmetic.** A bend of angle θ, inner radius R and thickness T holds `w · θ/2 · ((R+T)² − R²)` of material folded and develops to the bend allowance `θ · (R + K·T)` flat (or the allowance or deduction given). Every flange, hem and bend is checked against the volume, box and bend count that gives, and flattening must change the volume by exactly `Σ w · T · (BA − θ·(R + T/2))`, which is nothing at K = 0.5. A hem's volume is checked within a band, not exactly.
- **Bend allowance** is a K-factor, an allowance or a deduction, exactly one of them. It is written into each flange where SOLIDWORKS does not carry the part's default to it. Bend tables and gauge tables are not read.
- **Flattening** is the `Flat-Pattern` feature's suppression, and `sw_sheetmetal_read` reports the state from it. The flat size is the cut list's `Bounding Box Length` and `Width`, which SOLIDWORKS rounds to the part's display precision (136.57 for 136.5664).
- **Unfold, cut and fold.** `sw_sheetmetal_unfold` unfolds the bends named with a fixed face kept put, a cut is made with `sw_feature_extrude`, and `sw_sheetmetal_fold` folds them back. On SOLIDWORKS 2026 SP4 a cut must stay inside the bend or on the side that stays put: once it reaches into the side that turns, SOLIDWORKS makes no Fold, and `sw_sheetmetal_fold` answers `operation_failed` and changes nothing.
- **Hems** are closed or open. Teardrop and rolled hems are refused (`invalid_argument` at `kind`): SOLIDWORKS 2026 SP4 does not make them through its API, though it does by hand.

`sw_export_flat_pattern` writes the flat pattern as DXF or DWG, with the bend lines unless `bend_lines: false`, and the part's visible sketches only with `sketches: true`. The part must be saved to disk: a part never saved is refused before SOLIDWORKS is asked (`invalid_argument`; save it with `sw_document_save_as` first), because SOLIDWORKS then opens a Save As box that waits for you. A part with unsaved changes is exported as it is now. The part need not be flattened, and keeps its state, path and unsaved-changes flag. With bend lines or sketches the add-in asks SOLIDWORKS with `ExportToDWG2` first: on SOLIDWORKS 2026 SP4, `ExportFlatPatternView` draws no bend lines even when asked, so it is tried only after `ExportToDWG2` writes nothing, and its file then fails the bend-line check. Without either, `ExportFlatPatternView` is asked first and `ExportToDWG2` after it. The result says which wrote it. The file is then checked on disk: a DXF must parse, the extents of its entities must be the flat size from the cut list within 0.01 mm beyond the cut list's rounding (the header's ``/`` is reported, not compared: `ExportFlatPatternView` writes the drawing sheet there), and it must hold a line across the outline for each bend when bend lines were asked for, and none when they were not. Bend lines are found by their geometry, not their layer: SOLIDWORKS 2026 SP4 draws them on layer 0, like the outline. A DWG must start with its `AC10xx` version and be at least 1 KB; its extents are not read.

### Closing documents

`sw_document_close` closes only a document the agent opened or created (with `sw_document_new` or `sw_document_open`) since it connected. Any other document is yours: closing it needs `users_document: true`, which an agent should pass only when you asked. A document with unsaved changes needs `discard: true` as well.

### Export

`sw_export_part` writes a copy: the part keeps its own path and its unsaved changes. A part that is not the active document is made active for the export, and the document that was active is made active again afterwards. The path's extension must be the format's (`.step` or `.stp`, `.x_t`, `.stl`, `.3mf`), in a folder that exists, and an existing file is replaced only with `overwrite: true`.

- STEP is AP214 unless `options.step_schema` is `ap242`.
- STL and 3MF are written in mm. Without `options.resolution` they use your own mesh settings (Tools > Options > Export, STL/3MF/AMF), untouched; `coarse` or `fine` is set for the call. STL is binary, in the part's own coordinates (not moved to positive space).
- Parasolid takes no options.

The options are SOLIDWORKS preferences (Tools > Options > Export). The add-in sets the ones the export needs for the call and puts them back afterwards, reading each one back to be sure SOLIDWORKS took it; one it did not take keeps the journal, is logged as an error, and blocks the next export until it is back. It writes them to `%LOCALAPPDATA%\solidworks-mcp\export-preferences-journal.json` first, so if SOLIDWORKS stops mid-export, the next load restores them.

**Known gaps and workarounds**, measured on SOLIDWORKS 2026 SP4:

- **No custom mesh resolution.** SOLIDWORKS ignores `swSTLQuality = 3` (custom) when it is set through its API: the call returns and the value stays where it was. A custom resolution changed for an export could never be put back, so `export.part` offers only `coarse` and `fine`, and refuses either (`invalid_argument`, `options.resolution`) when your own resolution is custom; leave `resolution` out to export with your own mesh settings. Custom can be chosen only by hand, in Tools > Options > Export with a document open.
- **AP242 depends on SOLIDWORKS MBD.** `SaveAs3` with `swStepAP = 242`, which SOLIDWORKS keeps, writes STEP AP203 (`FILE_DESCRIPTION 'STEP AP203'`, `FILE_SCHEMA CONFIG_CONTROL_DESIGN`), so `ap242` is published instead, by a chain that stops at the first attempt that writes: `PublishSTEP242File` with the path, the same with its extension in upper case, then `GetMBDSTEP242Data` with `PublishSTEP242File2`. On a seat without the SOLIDWORKS MBD product (the Save As dialog offers only STEP AP203 and AP214, and there is no Publish STEP 242 command) every attempt answers `swPublishStep242_InvalidPath`, and the call answers `operation_failed` listing each attempt and SOLIDWORKS' answer in `details.attempts`. `ap214` works everywhere.

The file is then checked on disk. It must have been written by this call and not be empty, and it must start with its format's header: `ISO-10303-21;` with the schema asked for (STEP), the Parasolid transmit header, a binary STL whose size matches its triangle count, or a 3MF zip holding `3D/3dmodel.model`. The part's path and unsaved-changes flag must also be unchanged. Otherwise the call answers `verification_failed`, naming the check.

### The escape hatch

`sw_api_call` calls a member of SOLIDWORKS' API by late binding, for what the other tools do not cover yet.

- **Its units are SOLIDWORKS' own: metres and radians.**
- `target` is `app` (SOLIDWORKS' `ISldWorks`), `document` (the document's `IModelDoc2`), or an entity of the document: a reference such as a face from `sw_model_find`, or the name of a feature, plane, axis or point.
- `member` is a dotted path (`Extension.GetMassProperties2`). Every part but the last is a property or a method without arguments; the last is called with `args`.
- `args` are JSON. `{"ref": "r1..."}` passes the entity a reference (or a feature's name) names. Give a placeholder (`0`, `null`) for each out argument; its value after the call comes back in `out`.
- A returned object comes back as its reference when SOLIDWORKS can persist it, otherwise only as its interface's name. There are no handles, so a chain of calls has to be one dotted `member`.
- For `document` and reference targets the document is rebuilt afterwards (unless `rebuild: false`) and its rebuild errors returned. A call that rebuilds waits, like any change, while you are editing a sketch or have a command open (`busy`). A call with `rebuild: false`, or on `app`, does not wait.
- Nothing is checked and nothing is taken back: it does what it is told.

It refuses every member that ends SOLIDWORKS, closes a document or runs code: `ExitApp`, `CloseAllDocuments`, `CloseDoc`, `QuitDoc`, `CloseAndReopen`, every other `Close` or `Quit` member of `ISldWorks` and `IModelDoc2`, `RunMacro`, `RunMacro2`, `RunCommand`, `LoadAddIn` and `UnloadAddIn`. It also refuses the members that close a document under another name: every member whose name contains `Reload` (`ReloadOrReplace`, `ReloadWithReferences`, `FileReload`, ...), `ForceReleaseLocks`, `SetReadOnlyState`, the members that replace a document (`ReplaceReferencedDocument`, `ReplaceComponents`, `ReplaceComponents2`, `ReplacePLMComponents`, `ReplaceReference`, `ReplaceViewModel`), and an add-in's own `ConnectToSW` and `DisconnectFromSW`. It also refuses every other member that runs code: `RunAttachedMacro`, `RunJournalCmd`, `RunBatchSaveProcess`, `CallBack`, `AddCallback`, `SetAddinCallbackInfo` and `SetAddinCallbackInfo2`, `InsertMacroFeature` (and `2`, `3` and the `IInsertMacroFeature` forms), `ToolsMacro`, `RunXMLCommand`, `WriteJournalToMacro`, `EmbedMacroFile` and `GetAddInObject`. These are refused whatever their case and wherever they appear in the path. Documents close only through `sw_document_close`.

Every call is logged in full to `api-calls.log` (see **Logs**), so its use shows which tools to add.

The escape hatch is on by default. Turn it off with `swmcp serve --no-api-call` or the environment variable `SWMCP_API_CALL=0`. The server then does not offer `sw_api_call` at all, and `swmcp call api.call` is refused.

Every tool takes an optional `deadline_ms`.

## Install

You need Windows and SOLIDWORKS 2026 SP4, started from the 3DEXPERIENCE dashboard, and nothing else. The release carries its own .NET, and installing needs no administrator rights.

1. Download `solidworks-mcp-<version>-win-x64.zip` from the [releases](https://github.com/Seiraiyu/solidworks-mcp/releases), and check it against the `.sha256` file beside it:

   ```powershell
   (Get-FileHash .\solidworks-mcp-0.1.0-win-x64.zip -Algorithm SHA256).Hash
   ```

2. Unzip it anywhere, such as your Downloads folder.
3. In PowerShell, in the unzipped folder:

   ```powershell
   .\swmcp.exe install
   ```

`install` copies the release to `%LOCALAPPDATA%\solidworks-mcp\app\<version>\`, one folder per version, so a SOLIDWORKS still holding an earlier add-in does not block it. It then:

- removes the Mark of the Web that Windows puts on downloaded files (SOLIDWORKS cannot load an add-in from a blocked DLL);
- registers the add-in for you alone, under `HKCU`;
- writes the version to `app\current.txt`.

It prints a JSON document. `path` is where it installed, `swmcp` is the `swmcp.exe` to use from now on, and `claude_code` is the line that adds the server to Claude Code. If SOLIDWORKS is running with an add-in loaded, `next` says the new one loads after you close SOLIDWORKS and start it again from the dashboard.

The binaries are not signed. Run `swmcp.exe` from PowerShell rather than by double-clicking it, which makes Windows SmartScreen ask about it.

Below, `$app` is the installed folder:

```powershell
$app = "$env:LOCALAPPDATA\solidworks-mcp\app\$(Get-Content "$env:LOCALAPPDATA\solidworks-mcp\app\current.txt")"
```

## Load the add-in

SOLIDWORKS does not load add-ins registered only for the current user at startup, so `swmcp serve`, `swmcp call` and `swmcp status` load it themselves. On the first call after SOLIDWORKS starts, they wait until SOLIDWORKS has finished starting, then attach to it (they never start one) and ask it to load the registered add-in. While SOLIDWORKS is still starting, including while it waits at a 3DEXPERIENCE sign-in or update prompt, calls answer `sw_starting` and `sw_status` says `starting`. swmcp never touches that prompt.

If the add-in fails to load, the error names the add-in's log. swmcp does not retry for that SOLIDWORKS until `sw_reconnect` (or a new `swmcp`), so a broken add-in is reported rather than reloaded in a loop. To load it explicitly:

```powershell
& "$app\swmcp.exe" addin load
```

Name an instance with `--pid` when more than one is running.

## Use it from Claude Code

Run the `claude_code` line `install` printed, which adds the server for every project:

```powershell
claude mcp add --scope user solidworks -- "$app\swmcp.exe" serve
```

Or in a project's `.mcp.json`:

```json
{
  "mcpServers": {
    "solidworks": {
      "command": "C:\\Users\\<you>\\AppData\\Local\\solidworks-mcp\\app\\0.1.0\\swmcp.exe",
      "args": ["serve"]
    }
  }
}
```

Then ask Claude for the SOLIDWORKS status, and it calls `sw_status`. A failure comes back as an error result whose JSON body has a stable `code` (`sw_not_running`, `addin_not_loaded`, `busy`, `timeout`, ...), a `message` saying what to do next, and `details`. Only a bug in the server itself comes back as an MCP protocol error (internal error); its detail is in the server log.

`sw_status` answers while another call waits on SOLIDWORKS. When SOLIDWORKS is not running, starting, or ambiguous, it says so as its `state` (`not_running`, `starting`, `multiple`, ...), with a `message` saying what to do.

If SOLIDWORKS crashes, the calls in flight get `sw_crashed` within about a second. The error names those calls and the documents that had unsaved changes. When no call was in flight, the next call gets it. Start SOLIDWORKS again from the 3DEXPERIENCE dashboard in the browser, and the next call reattaches by itself: it loads the add-in again. A SOLIDWORKS closed normally gives `sw_not_running`, not `sw_crashed`. swmcp never starts, restarts or ends SOLIDWORKS.

The add-in serves one client at a time. While an MCP client is connected, `swmcp call` from a terminal answers `busy`.

To run without the escape hatch, add `--no-api-call` after `serve` (or set `SWMCP_API_CALL=0` in the server's environment).

## From a terminal

```powershell
swmcp call session.status
```

`call` prints the result, or the typed error, as JSON. It exits 0 on success and with a code per error otherwise (`swmcp --help`).

`swmcp status` prints what `sw_status` answers, and always exits 0. `swmcp events` prints the events the add-in still holds. Add `--after-seq N` to see only those after event N, or `--follow` to print each new event as a line of JSON until Ctrl+C:

```powershell
& "$app\swmcp.exe" status
& "$app\swmcp.exe" events --follow
```

`addin.state` answers even while SOLIDWORKS is busy with another request, and shows what is running, what waits, the open documents and whether the automation preferences are applied:

```powershell
& "$app\swmcp.exe" call addin.state
```

A call that names no `--deadline-ms` gets its operation family's default (10 s for `session.*`, 5 s for `addin.*`, 60 s for `document.*`, 30 s for `model.*`, `selection.*`, `sketch.*` and `dimension.*`). Any deadline must be between 1 ms and one hour.

A request whose deadline passes before SOLIDWORKS can start it is answered `busy` (it never ran; retrying is safe). One that started but ran past its deadline is answered `timeout`; it keeps running, and its outcome is raised as an `operation.completed` event.

While a client is connected, the add-in turns off three SOLIDWORKS preferences that stall automation: the dimension value box on creation, the save reminder, and the prompt to close an open sketch. Your values come back when the client disconnects or SOLIDWORKS closes. If SOLIDWORKS crashes first, they come back the next time the add-in loads, from `%LOCALAPPDATA%\solidworks-mcp\preferences-journal.json`.

When SOLIDWORKS shows a modal dialog, a request that raised it is answered `dialog_open` with the dialog's id, title, text and buttons, instead of hanging; the request goes on waiting on the dialog, and its outcome follows as an `operation.completed` event. While a dialog is open nothing else starts on SOLIDWORKS' UI thread: new requests are answered `dialog_open` with `started: false`. `addin.state` shows the open dialog, and `dialog.respond` presses one of its buttons, by text or control id, or closes it:

```powershell
& "$app\swmcp.exe" call addin.state
& "$app\swmcp.exe" call dialog.respond '{"dialog":"d3-1a2b","button":"Cancel"}'
```

Each `swmcp call` opens a new connection, and a new connection's hello waits for SOLIDWORKS' UI thread. So from a terminal this answers a dialog that no request is waiting on (one opened by hand). A dialog that a request raised is answered on the connection that sent the request: an agent does it with `sw_dialog_respond`, over the server's one connection.

The add-in presses a button only when asked, or for a prompt on its built-in allow-list of measured, always-safe answers (empty in this release).

## Logs

Logs are in `%LOCALAPPDATA%\solidworks-mcp\logs\` and are kept 14 days:

- `addin-<yyyyMMdd>.log` is the add-in's log: load, unload, and every request with its thread.
- `api-calls.log` holds every `sw_api_call`, one JSON line each: the target, member, arguments, result or error, and duration. Past 10 MB it becomes `api-calls.1.log` and a new one starts.
- `server-<yyyyMMdd>.log` is the MCP server's log. It records every tool call with its outcome code and duration, every change in what the server sees (`not_running`, `starting`, `loading`, `ready`, `crashed`, ...), every `LoadAddIn` with its result and how long the add-in's pipe took, and how each connection ended (crash, close, add-in turned off), with its evidence. The server also logs to stderr.

`SWMCP_LOG_LEVEL` (`trace`, `debug`, `information`, `warning`, `error`) sets the server's minimum level; the default is `information`. At `debug` the server also logs each call's parameters.

## Upgrading and uninstalling

To upgrade, install the new release the same way. Its `swmcp.exe` is in a new version folder, so run `claude mcp remove --scope user solidworks` and then the `claude_code` line it prints. The add-in speaks protocol version 10, and a SOLIDWORKS that still has an older add-in loaded answers `addin_incompatible`. Close SOLIDWORKS and start it again from the 3DEXPERIENCE dashboard, and the next call loads the new add-in. The earlier version's folder stays until you uninstall.

```powershell
& "$app\swmcp.exe" uninstall
claude mcp remove --scope user solidworks
```

`uninstall` removes the add-in's registration and every version folder except the one it runs from, which it lists under `kept` for you to delete once it has exited. A folder that SOLIDWORKS still holds files in is kept too, with the reason: close SOLIDWORKS and run `uninstall` again. If SOLIDWORKS was running with the add-in loaded, run `uninstall` once more after closing SOLIDWORKS, which writes the add-in's `AddInsStartup` entry back as it exits. The preferences the add-in changes are put back whenever a client disconnects. Upgrading in place and a fuller uninstall are planned.

## What it can and cannot do

In your running SOLIDWORKS, an agent can:

- create, open, save and close parts;
- draw fully specified sketches in one call;
- add and edit the features listed above, each checked against the geometry it made and taken back when it is wrong;
- name faces, edges and features by references that survive rebuilds and reopening;
- read the feature tree, dimensions, mass properties, bounding box and rebuild errors, and measure;
- export STEP, Parasolid, STL and 3MF;
- make sheet-metal parts with flanges, hems and bends, change their thickness, radius and allowance, flatten them, cut across bends, and write their flat patterns as DXF or DWG checked against the flat size;
- see what you have selected;
- reach the rest of SOLIDWORKS' API through `sw_api_call`.

In this release it cannot:

- work on assemblies or drawings, or check interference;
- make teardrop or rolled hems: `sw_sheetmetal_hem` makes closed and open hems and refuses the other two kinds, which SOLIDWORKS does not make through its API as it does by hand;
- fold a part whose cut reaches past a bend into the side that turns (see **Sheet metal**);
- make sheet-metal corners, jogs, lofted bends, cross breaks, rips, gussets, tabs and slots, vents or forming tools, use bend or gauge tables, or put more than one sheet-metal body in a part;
- export drawings (PDF, or a drawing sheet as DXF or DWG); a part's flat pattern is written as DXF or DWG by `sw_export_flat_pattern`, and a DWG's extents are not checked;
- set materials, appearances, equations, design tables or custom properties, except through `sw_api_call`;
- make variable-radius fillets or chamfers other than distance-angle and distance-distance, or do surface, multibody, weldment or direct-editing work;
- set a custom STL or 3MF mesh resolution, or write STEP AP242 on a seat without SOLIDWORKS MBD (see **Export**);
- start, restart or end SOLIDWORKS, answer a 3DEXPERIENCE sign-in or licence prompt, or work on 3DEXPERIENCE documents: only local files;
- serve more than one client at a time;
- load a new add-in into a running SOLIDWORKS: an upgrade needs SOLIDWORKS started again.

It runs on Windows only and is tested on SOLIDWORKS 2026 SP4.

## Development

### Requirements

- The .NET 10 SDK, to build. A per-user install works: `dotnet-install.ps1 -Channel 10.0` puts it in `%LOCALAPPDATA%\Microsoft\dotnet`. Then set `DOTNET_ROOT` to that folder, because `swmcp.exe` needs it to find the runtime.

### Build and test

```powershell
dotnet build SolidWorksMcp.slnx -c Release
dotnet test SolidWorksMcp.slnx -c Release --no-build
```

The build needs no SOLIDWORKS: the interop assemblies it compiles against are in `lib/solidworks/`.

### Run from a build

```powershell
$dev = "$env:LOCALAPPDATA\solidworks-mcp\dev"
dotnet publish src/SwAgent.Mcp -c Release -o $dev
& "$dev\swmcp.exe" addin register
claude mcp add solidworks -e "DOTNET_ROOT=$env:LOCALAPPDATA\Microsoft\dotnet" -- "$dev\swmcp.exe" serve
```

A build is not self-contained, so `swmcp.exe` needs `DOTNET_ROOT`. `addin register` writes the add-in's COM registration and SOLIDWORKS' add-in keys under `HKCU`. `swmcp addin status` shows them, and `swmcp addin unregister` removes them.

### Releases

`.github/workflows/release.yml` builds a release from a `v*` tag. It builds, tests, packs the zip with `build/release/pack.ps1`, and checks it with `build/release/smoke.ps1`, which installs and uninstalls it on the runner. It then publishes the zip and its SHA-256 as a GitHub Release, whose notes carry the version's `CHANGELOG.md` section and link the pull request that carries the commit's live-suite report. A pull request that changes the workflow or `build/`, and a run started by hand (`dry_run` is on by default), do everything but the release and upload the zip as an artifact. To build the same zip here:

```powershell
./build/release/pack.ps1 -Version 0.1.0-local.1 -Output dist
```

`smoke.ps1` installs into your own `%LOCALAPPDATA%` and `HKCU`, so it runs only on a GitHub-hosted runner.

### Live fault suite

`tests/SwAgent.Live.Tests` drives a real SOLIDWORKS through the client library, exactly as `swmcp serve` does. It covers:

- the add-in loaded by the first call;
- preferences applied per connection and restored;
- `sw_status` answering while SOLIDWORKS is busy;
- `busy`, `timeout` and the late `operation.completed`;
- dialogs during an operation and with nothing running;
- a second client;
- the client dropping the pipe;
- SOLIDWORKS killed mid-operation (`sw_crashed`);
- SOLIDWORKS closed normally (`sw_not_running`);
- reattaching after each restart;
- a reference to a face that a dimension change removed (a pocket deepened through the block: `reference_unresolved`, nothing changed);
- a change while a sketch is open for editing (`busy` with `user_editing`; the suite opens the sketch itself);
- a dialog in the middle of a feature (`dialog_open`, answered with `sw_dialog_respond`: OK completes the fillet, Cancel takes it back). SOLIDWORKS' feature calls raise no prompt through the API, so the suite has the add-in show one (`diag.arm_dialog`).

A 3DEXPERIENCE sign-in or licence window is never answered: `sw_status` and `addin.state` mark it `needs_person`, `sw_dialog_respond` refuses it, and the suite stops for you.

Every case is skipped unless `SWMCP_LIVE=1`, so a plain `dotnet test` and CI never touch SOLIDWORKS.

Run it with SOLIDWORKS closed, or started from the 3DEXPERIENCE tab with no documents open:

```powershell
$env:SWMCP_LIVE = "1"
dotnet test tests/SwAgent.Live.Tests -c Release --filter "FullyQualifiedName~SwAgent.Live.Tests.FaultSuite"
Remove-Item env:SWMCP_LIVE
```

A run looks after the two switches the add-in's diagnostic operations depend on, so it does not matter how SOLIDWORKS was last started:

- It sets `HKCU\Software\solidworks-mcp\Diagnostics` to 1 when it starts and deletes it at the end. The add-in reads it when it loads.
- `swmcp addin register` makes SOLIDWORKS load the add-in as it starts (`HKCU\Software\SolidWorks\AddInsStartup`), before a run can turn diagnostics on. If SOLIDWORKS is not running, the run turns that off before it asks for SOLIDWORKS to be started, so the add-in loads on the run's first call with diagnostics on, and puts it back as it was at the end. If SOLIDWORKS is running with the add-in loaded without diagnostics, the run asks you (`ACTION`) to close it, waits for it to exit, and then asks for it to be started again.

It writes to `%LOCALAPPDATA%\solidworks-mcp\live-runs\<run>\`:

- `progress.log`, which you follow while it runs. A line starting `ACTION` asks for something only you can do: close SOLIDWORKS, start it from the 3DEXPERIENCE tab, open Tools > Options, edit a sketch or open a PropertyManager. A line starting `STOP` ends the run.
- `client.log`.
- `report.md`, with the SOLIDWORKS revision, the versions, and a result per case.

The suite ends SOLIDWORKS twice. Each time it first reads the open documents again through its own connection, and it refuses if a document it did not create is open, or a dialog is open. It ends SOLIDWORKS only by process id. It creates documents only under `%LOCALAPPDATA%\solidworks-mcp\live-scratch\`.

#### Parts suite

`PartsSuite` checks documents, references, inspection, selection, sketches, dimensions and features against exact geometry: a 40 x 20 x 10 mm block must measure 8000 mm³, its top face must be the one face whose normal is +Z, a reference to it must survive a rebuild, a save as and a close and reopen, a fully dimensioned plate sketch must read back fully defined, setting the block's depth to 15 mm must make it 12000 mm³, a change that over-defines a sketch must leave it as it was, a 5 mm radius hole through the block must take 785.398 mm³, a 2 mm fillet on a 10 mm edge 8.584 mm³ and a 2 mm chamfer 20 mm³, a tube revolved from a 10 x 30 profile must be 28274.334 mm³, extrudes up to next, mid-plane, reversed, both ways, drafted and thin-walled must make exactly the box and volume asked for, a fillet too big for its edge or a cut that misses the part must leave it as it was, a circle swept 30 mm along a line must be 2356.194 mm³, a loft from a 20 mm square to a 10 mm one 10 mm above it 2333.333 mm³, the block shelled open at the top with 2 mm walls 3392 mm³, its side drafted 5° must change by 87.489 mm³, two Ø5.0 holes through it must take 392.699 mm³, patterns and a mirror of holes must take each hole's volume once per instance, the block mirrored about its side must be 16000 mm³, and a hole suppressed in one configuration must be there in the other, each export format (STEP AP214 and AP242, Parasolid, STL coarse, fine and custom, 3MF) must be written with its header while the part keeps its path and unsaved changes and the export preferences read back as they were, and `sw_api_call`'s `GetMassProperties2` must agree with `sw_model_mass` in SI units, suppress a hole with its rebuild status, and refuse `CloseDoc`. Two cases ask you to edit a sketch and to open a PropertyManager by hand, and are skipped after 3 minutes if nobody does. It never ends SOLIDWORKS. It needs the add-in loaded with diagnostics on, as the fault suite does (it looks after that the same way), and stops if any document it did not create is open:

```powershell
$env:SWMCP_LIVE = "1"
dotnet test tests/SwAgent.Live.Tests -c Release --filter "FullyQualifiedName~SwAgent.Live.Tests.PartsSuite"
Remove-Item env:SWMCP_LIVE
```

Its report is in `%LOCALAPPDATA%\solidworks-mcp\live-runs\<run>-parts-suite\report.md`.

Its case 33 is the build-a-part scenario. It plays `tests/SwAgent.Live.Tests/Scenarios/bracket.transcript.json` through the MCP server's own tools, as an agent would: a 60 x 50 x 40 mm L-bracket of 6 mm stock with a 5 mm inside fillet and two 5 mm holes. The bracket must come out at 24938.982 mm³ with its centre of mass at (18.276, 13.621, 0) mm, and its STEP copy must be written. The calls and their answers are kept in the run's `bracket-transcript.jsonl`.

#### Sheet-metal suite

`SheetMetalSuite` checks the sheet-metal tools against the bend arithmetic and against the sizes CADapter measured on SOLIDWORKS 2026: a 100 x 60 x 2 mm tab must be 12000 mm³ and flat 100 x 60; a 25 mm flange at R 3 must flatten to 83.2832 mm across at K 0.5, 82.0265 at K 0.1 and 84.5398 at K 0.9; four 20 mm flanges must make a 100 x 60 x 22 mm box that flattens to 136.5664 x 96.5664 mm at the same volume; an L of 60 x 40 x 50 mm with a 2 mm wall must convert and flatten to 96.2832 x 50 mm; a hole cut across an unfolded bend must fold with it, and one reaching into the side that turns must be refused; the flat pattern's DXF must have the flat size, with and without bend lines, whether the part is folded, flattened or has unsaved changes; and a part never saved must be refused with no dialog opened. One case asks you to open a PropertyManager by hand, and is skipped after 3 minutes if nobody does. It needs the add-in loaded with diagnostics on, as the parts suite does, and stops if any document it did not create is open:

```powershell
$env:SWMCP_LIVE = "1"
dotnet test tests/SwAgent.Live.Tests -c Release --filter "FullyQualifiedName~SwAgent.Live.Tests.SheetMetalSuite"
Remove-Item env:SWMCP_LIVE
```

Its report is in `%LOCALAPPDATA%\solidworks-mcp\live-runs\<run>-sheet-metal-suite\report.md`.

Its case 23 is the build-an-enclosure scenario. It plays `tests/SwAgent.Live.Tests/Scenarios/enclosure.transcript.json` through the MCP server's own tools: a 100 x 60 mm blank of 2 mm sheet, a 25 mm flange, a 10 mm closed hem, and a 4 mm relief hole cut across the flange's unfolded bend where it meets the base, folded back. Its flat must be 100 mm by 60 + 23.2832 mm plus the hem's share (measured on a tab with that hem alone), and its DXF must be that size. The calls and their answers are kept in the run's `enclosure-transcript.jsonl`.

The SP4 canary, `Canary`, checks that a sketch call made through COM from outside SOLIDWORKS still crashes it. It is expected to crash SOLIDWORKS. It never runs by default: it needs `SWMCP_CANARY=1` as well, and is run on its own with `--filter "FullyQualifiedName~Canary"`, after a SOLIDWORKS update.
