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

There is one tool per add-in operation, named `sw_` and the operation with underscores (`document.save_as` is `sw_document_save_as`).

Lengths are millimetres and angles degrees everywhere.

### References

Faces, edges, vertices, features, planes, sketches, sketch entities and dimensions are named by references: strings such as `r1.face.AQAAAB…`, given in results (`sw_model_find`, `sw_model_tree`, `sw_selection_get`, `sw_sketch_create`). The part after `r1.` says what kind of thing it names; the rest is opaque. A reference survives rebuilds, saving under a new name, and closing and opening the part again. When the thing it named is gone (deleted, suppressed, or in another part), the operation answers `reference_unresolved` with what the thing was, and never picks something nearby instead. Feature names (`Boss-Extrude1`) work too, and `front`, `top`, `right` and `origin` name the template's own planes and origin in any language.

### Changing a part

Every tool that changes a part (the sketch, dimension, feature and reference geometry tools) works the same way:

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

### Closing documents

`sw_document_close` closes only a document the agent opened or created (with `sw_document_new` or `sw_document_open`) since it connected. Any other document is yours: closing it needs `users_document: true`, which an agent should pass only when you asked. A document with unsaved changes needs `discard: true` as well.

Every tool takes an optional `deadline_ms`.

## Requirements

- Windows with SOLIDWORKS 2026 SP4, started from the 3DEXPERIENCE launcher.
- The .NET 10 SDK, to build. A per-user install works: `dotnet-install.ps1 -Channel 10.0` puts it in `%LOCALAPPDATA%\Microsoft\dotnet`. Then set `DOTNET_ROOT` to that folder, because `swmcp.exe` needs it to find the runtime.
- No administrator rights. Everything installs for the current user only.

## Build and test

```powershell
dotnet build SolidWorksMcp.slnx -c Release
dotnet test SolidWorksMcp.slnx -c Release --no-build
```

The build needs no SOLIDWORKS: the interop assemblies it compiles against are in `lib/solidworks/`.

## Live fault suite

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

### Parts suite

`PartsSuite` checks documents, references, inspection, selection, sketches, dimensions and features against exact geometry: a 40 x 20 x 10 mm block must measure 8000 mm³, its top face must be the one face whose normal is +Z, a reference to it must survive a rebuild, a save as and a close and reopen, a fully dimensioned plate sketch must read back fully defined, setting the block's depth to 15 mm must make it 12000 mm³, a change that over-defines a sketch must leave it as it was, a 5 mm radius hole through the block must take 785.398 mm³, a 2 mm fillet on a 10 mm edge 8.584 mm³ and a 2 mm chamfer 20 mm³, a tube revolved from a 10 x 30 profile must be 28274.334 mm³, extrudes up to next, mid-plane, reversed, both ways, drafted and thin-walled must make exactly the box and volume asked for, a fillet too big for its edge or a cut that misses the part must leave it as it was, a circle swept 30 mm along a line must be 2356.194 mm³, a loft from a 20 mm square to a 10 mm one 10 mm above it 2333.333 mm³, the block shelled open at the top with 2 mm walls 3392 mm³, its side drafted 5° must change by 87.489 mm³, two Ø5.0 holes through it must take 392.699 mm³, patterns and a mirror of holes must take each hole's volume once per instance, the block mirrored about its side must be 16000 mm³, and a hole suppressed in one configuration must be there in the other. Two cases ask you to edit a sketch and to open a PropertyManager by hand, and are skipped after 3 minutes if nobody does. It never ends SOLIDWORKS. It needs the add-in loaded with diagnostics on, as the fault suite does (it looks after that the same way), and stops if any document it did not create is open:

```powershell
$env:SWMCP_LIVE = "1"
dotnet test tests/SwAgent.Live.Tests -c Release --filter "FullyQualifiedName~SwAgent.Live.Tests.PartsSuite"
Remove-Item env:SWMCP_LIVE
```

Its report is in `%LOCALAPPDATA%\solidworks-mcp\live-runs\<run>-parts-suite\report.md`.

The SP4 canary, `Canary`, checks that a sketch call made through COM from outside SOLIDWORKS still crashes it. It is expected to crash SOLIDWORKS. It never runs by default: it needs `SWMCP_CANARY=1` as well, and is run on its own with `--filter "FullyQualifiedName~Canary"`, after a SOLIDWORKS update.

## Upgrading

The add-in speaks protocol version 3. A SOLIDWORKS that still has an older add-in loaded answers `addin_incompatible`: start SOLIDWORKS again from the 3DEXPERIENCE dashboard, and the next call loads the new add-in.

## Install

```powershell
$app = "$env:LOCALAPPDATA\solidworks-mcp\app"
dotnet publish src/SwAgent.Mcp -c Release -o $app
& "$app\swmcp.exe" addin register
```

`addin register` writes the add-in's COM registration and SOLIDWORKS' add-in keys under `HKCU`. `swmcp addin status` shows them, and `swmcp addin unregister` removes them.

## Load the add-in

SOLIDWORKS does not load add-ins registered only for the current user at startup, so `swmcp serve`, `swmcp call` and `swmcp status` load it themselves. On the first call after SOLIDWORKS starts, they wait until SOLIDWORKS has finished starting, then attach to it (they never start one) and ask it to load the registered add-in. While SOLIDWORKS is still starting, including while it waits at a 3DEXPERIENCE sign-in or update prompt, calls answer `sw_starting` and `sw_status` says `starting`. swmcp never touches that prompt.

If the add-in fails to load, the error names the add-in's log. swmcp does not retry for that SOLIDWORKS until `sw_reconnect` (or a new `swmcp`), so a broken add-in is reported rather than reloaded in a loop. To load it explicitly:

```powershell
& "$app\swmcp.exe" addin load
```

Name an instance with `--pid` when more than one is running.

## Use it from Claude Code

```powershell
claude mcp add solidworks -e "DOTNET_ROOT=$env:LOCALAPPDATA\Microsoft\dotnet" -- "$env:LOCALAPPDATA\solidworks-mcp\app\swmcp.exe" serve
```

Or in a project's `.mcp.json`:

```json
{
  "mcpServers": {
    "solidworks": {
      "command": "C:\\Users\\<you>\\AppData\\Local\\solidworks-mcp\\app\\swmcp.exe",
      "args": ["serve"],
      "env": { "DOTNET_ROOT": "C:\\Users\\<you>\\AppData\\Local\\Microsoft\\dotnet" }
    }
  }
}
```

Then ask Claude for the SOLIDWORKS status, and it calls `sw_status`. A failure comes back as an error result whose JSON body has a stable `code` (`sw_not_running`, `addin_not_loaded`, `busy`, `timeout`, ...), a `message` saying what to do next, and `details`. Only a bug in the server itself comes back as an MCP protocol error (internal error); its detail is in the server log.

`sw_status` answers while another call waits on SOLIDWORKS. When SOLIDWORKS is not running, starting, or ambiguous, it says so as its `state` (`not_running`, `starting`, `multiple`, ...), with a `message` saying what to do.

If SOLIDWORKS crashes, the calls in flight get `sw_crashed` within about a second. The error names those calls and the documents that had unsaved changes. When no call was in flight, the next call gets it. Start SOLIDWORKS again from the 3DEXPERIENCE dashboard in the browser, and the next call reattaches by itself: it loads the add-in again. A SOLIDWORKS closed normally gives `sw_not_running`, not `sw_crashed`. swmcp never starts, restarts or ends SOLIDWORKS.

The add-in serves one client at a time. While an MCP client is connected, `swmcp call` from a terminal answers `busy`.

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
- `server-<yyyyMMdd>.log` is the MCP server's log. It records every tool call with its outcome code and duration, every change in what the server sees (`not_running`, `starting`, `loading`, `ready`, `crashed`, ...), every `LoadAddIn` with its result and how long the add-in's pipe took, and how each connection ended (crash, close, add-in turned off), with its evidence. The server also logs to stderr.

`SWMCP_LOG_LEVEL` (`trace`, `debug`, `information`, `warning`, `error`) sets the server's minimum level; the default is `information`. At `debug` the server also logs each call's parameters.
