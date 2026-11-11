# Changelog

Each release of solidworks-mcp. The version numbers follow semantic versioning; until 1.0.0 a minor version may change
tools and their parameters.

## 0.1.0

The first release: an agent can model parts in a running SOLIDWORKS 2026 SP4 on Windows, through MCP.

### Parts

- Documents: new part from your templates, open, save, save as, close (refusing to discard unsaved changes unless
  asked), rebuild, list open documents.
- Sketches in one call: lines, centre lines, arcs, circles, rectangles, polygons, slots, splines and points, with
  relations and named driving dimensions, read back with their solve state; a change that over-defines a sketch is
  taken back.
- Features: extrude and cut (blind, through all, up to next, mid-plane, both directions, drafted, thin), revolve,
  sweep, loft, fillet, chamfer, Hole Wizard holes, shell, draft, rib, linear and circular patterns, mirror, reference
  planes, axes and points, suppress, delete, dimension edits and configurations. Each is checked against the geometry
  it made, and taken back when the check fails.
- Inspection: feature tree, dimensions, mass properties, bounding box, measure, rebuild errors and warnings, selection.
- References to faces, edges and features that survive rebuilds, save as and reopening, and say
  `reference_unresolved` rather than guess.
- Export: STEP, Parasolid, STL and 3MF, leaving the part and your export preferences as they were.
- `sw_api_call`, a logged escape hatch to the rest of SOLIDWORKS' API, with the members that close documents, end
  SOLIDWORKS or run code refused. `--no-api-call` or `SWMCP_API_CALL=0` turns it off.

### Reliability

- Every model change runs inside SOLIDWORKS, on its UI thread, one at a time.
- Typed errors with stable codes: `sw_not_running`, `sw_starting`, `sw_crashed`, `sw_multiple_instances`,
  `addin_not_loaded`, `addin_incompatible`, `timeout`, `busy` (including `user_editing` while you edit a sketch or a
  PropertyManager is open), `dialog_open`, `invalid_argument`, `reference_unresolved`, `wrong_document_type`,
  `operation_failed`, `verification_failed`, `api_error`.
- A deadline on every call; a dead or closed SOLIDWORKS is reported within a second, with what was in flight.
- Modal dialogs are reported with their title, text and buttons, and pressed only when the agent asks
  (`sw_dialog_respond`). 3DEXPERIENCE sign-in and licence windows are never answered.
- The preferences the add-in changes are put back when a client disconnects.
- `sw_status`, `sw_events` and `sw_reconnect`.

### Install

- A self-contained zip for win-x64 with its SHA-256. `swmcp install` installs it per user (no administrator rights)
  under `%LOCALAPPDATA%\solidworks-mcp\app\<version>\` and prints the `claude mcp add` line; `swmcp uninstall` removes
  it.

### Known limits

- Parts only: assemblies, drawings and sheet metal come in later releases.
- SOLIDWORKS 2026 SP4 or later, started from the 3DEXPERIENCE dashboard. swmcp never starts SOLIDWORKS.
- The binaries are not signed.
- The dialog watcher can match a 3DEXPERIENCE sign-in window that opens while it is looking for another dialog,
  and close it (#10). Sign in before you start an agent.

### Fixed before release

- The `dialog_open` error for a 3DEXPERIENCE sign-in or licence window no longer tells the agent to answer it with
  `sw_dialog_respond`, which refuses those windows; it says only a person can answer it (#16).
