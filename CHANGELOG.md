# Changelog

Each release of solidworks-mcp. The version numbers follow semantic versioning; until 1.0.0 a minor version may change
tools and their parameters.

## 0.2.0 (2026-11-13)

The assemblies release: an agent can now mate, pattern, check, explode and export assemblies, and sheet metal goes
from the base flange to flanges, hems, bends, flattening and flat-pattern export. The add-in and swmcp speak protocol
12 (0.1.0 spoke 5). After upgrading, close SOLIDWORKS and start it again from the 3DEXPERIENCE dashboard so it loads the
new add-in; until then every call answers `addin_incompatible`.

### Assemblies

- References into components: a component's faces, edges, planes and axes are named in the assembly by an assembly
  reference (`sw_model_find` with `component`, or `sw_selection_get`), by `{component, entity}` with a reference
  from the part, or by `name@component` (`front@bracket-1`, `Axis1@sub-1/pin-1`), all resolving to the same entity.
  A suppressed, lightweight or missing component's entity answers `reference_unresolved` with
  `component_suppressed`, `component_lightweight` or `component_missing`, never a nearby entity.
- Mates: `sw_mate_add`, `sw_mate_edit`, `sw_mate_delete` and `sw_mate_list` for the standard mates, each checked by
  measuring what it constrains (0.001 mm, 0.01°). An over-defining mate is taken back with every component where it
  was. `sw_assembly_dof` reports how far each component is defined.
- Component patterns: `sw_component_linear_pattern`, `sw_component_circular_pattern` and
  `sw_component_feature_pattern`, checked instance by instance.
- `sw_assembly_interference`: each pair of components that overlap, with the volume and box of the overlap.
- Exploded views: `sw_explode_create` makes a view whole in one call and checks that each step moved its components
  by exactly the distance asked and nothing else; `sw_explode_show`, `sw_explode_list` and `sw_explode_delete`.
- `sw_export_assembly`: STEP (one occurrence per component, checked), Parasolid, STL and 3MF, leaving your export
  preferences as they were.
- Every change to an assembly records where every component was, and taking a change back puts back every component
  that moved.

### Sheet metal

- `sw_sheetmetal_edge_flange`, `sw_sheetmetal_miter_flange`, `sw_sheetmetal_hem` (closed and open) and
  `sw_sheetmetal_sketched_bend`, each checked against the bend arithmetic (volume, box and bend count; a hem's volume within a band).
- `sw_sheetmetal_convert`: a sheet-metal part from a solid of constant thickness.
- `sw_sheetmetal_flatten` flattens and folds a part, checked by the flatten volume identity; `sw_sheetmetal_unfold`
  and `sw_sheetmetal_fold` unfold named bends for a cut across them and fold them back.
- `sw_export_flat_pattern`: the flat pattern as DXF or DWG, with or without bend lines; a DXF is checked against the
  flat size from the cut list and its bend-line count. The part must have been saved; on SOLIDWORKS 2026 SP4 the
  export leaves it marked as changed (`marked_dirty`).

### Changed

- `sw_model_find`, `sw_model_mass`, `sw_model_bounding_box`, `sw_model_measure`, `sw_selection_get`,
  `sw_document_rebuild`, `sw_model_rebuild_status` and the configuration tools work on assemblies too.
- `sw_model_find` takes `component`, its entities carry their `component`, and it answers `skipped` with the
  components it could not search (suppressed, lightweight or missing).
- `sw_model_measure` takes `a_component` and `b_component` for a reference issued in a component's own part.
- `sw_model_mass` on an assembly answers each top-level component's share in `components`, and `skipped`.
- An assembly's rebuild errors include its mates' errors, each named by its mate.
- `sw_selection_get` reports a selected component as a `component` item, and a face, edge or vertex with its
  component.
- `sw_document_open` answers `substituted` with each component file SOLIDWORKS loaded from another folder; a reference
  issued earlier into that component answers `reference_unresolved` with `component_file_changed`.
- `sw_component_delete` also deletes component patterns with their instances, refuses a pattern's seed unless the
  pattern goes too, refuses a single instance, and answers `patterns_deleted` and `instances_deleted`.
- Sheet-metal results, `sw_sheetmetal_base_flange`'s included, carry `features` when one call made more than one
  feature (an edge flange makes one per edge).

### Known limits

- No drawings yet.
- Assemblies: no editing a component in the assembly's context; no advanced or mechanical mates, mirrored components, subassembly forming or dissolving, component
  replacement or Pack and Go (`sw_api_call` reaches them); `sw_assembly_dof` counts free motion only for fixed, fully
  defined and unmated components; an exploded view cannot be renamed or deleted through SOLIDWORKS' API, so
  `sw_explode_delete` leaves it empty, and a step cannot be turned.
- Sheet metal: no teardrop or rolled hems, corners, jogs, lofted bends, cross breaks, rips, gussets, tabs and slots,
  vents, forming tools, bend or gauge tables, or more than one sheet-metal body; a cut that reaches past a bend into
  the side that turns cannot be folded back on SOLIDWORKS 2026 SP4.
- SOLIDWORKS 2026 SP4 or later, started from the 3DEXPERIENCE dashboard. swmcp never starts SOLIDWORKS.
- The binaries are not signed.
- The dialog watcher can match a 3DEXPERIENCE sign-in window that opens while it is looking for another dialog,
  and close it (#10). Sign in before you start an agent.

### Fixed before release

- `sw_assembly_interference` now says when SOLIDWORKS' interference session marks a saved assembly as having unsaved
  changes (`marked_dirty: true`), and checks that no component moved and no mate came or went; the assembly is not
  saved behind you (#29).

## 0.1.0 (2026-11-11)

The first release: an agent can model parts, sheet-metal parts and assemblies in a running SOLIDWORKS 2026 SP4 on
Windows, through MCP. The add-in and swmcp speak protocol 5, and swmcp refuses an add-in at any other version
(`addin_incompatible`).

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

### Sheet metal

- `sw_sheetmetal_base_flange`: a sheet-metal part from a sketch, a closed profile as a flat tab or an open chain
  bent at each corner, with its thickness, bend radius, allowance (K-factor 0.5 by default) and relief.
- `sw_sheetmetal_read`: whether a part is sheet metal, folded or flattened, its thickness, defaults, bodies, every
  bend with its angle, radius, allowance and width, its base face and the flat pattern's size.
- `sw_sheetmetal_set_parameters`: change the thickness, default bend radius, allowance or relief of a folded part.
- Each is checked against the part, and rolled back when the check fails.

### Assemblies

- Documents: `sw_document_new` makes assemblies; `sw_document_open` opens them with every component resolved and
  lists missing component files; `sw_document_save` saves the changed documents an assembly uses first;
  `sw_document_close` refuses to lose a component's unsaved changes, and a part an open assembly uses keeps them.
- `sw_assembly_insert`: a part or assembly as a component, at a point and orientation; the first one is fixed.
- `sw_assembly_components`: the components in tree order, top level or the whole tree, with their state, position,
  box and how far each is defined.
- `sw_component_place`, `sw_component_set` (fixed, suppressed, configuration, rigid or flexible, hidden) and
  `sw_component_delete` (with the mates that use the component).

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

- No drawings yet. Assemblies have no mates yet, and sheet metal has only the base flange; flanges, hems and the
  rest come in later releases.
- SOLIDWORKS 2026 SP4 or later, started from the 3DEXPERIENCE dashboard. swmcp never starts SOLIDWORKS.
- The binaries are not signed.
- The dialog watcher can match a 3DEXPERIENCE sign-in window that opens while it is looking for another dialog,
  and close it (#10). Sign in before you start an agent.

### Fixed before release

- The `dialog_open` error and the `sw_status` message for a 3DEXPERIENCE sign-in or licence window no longer tell
  the agent to answer it with `sw_dialog_respond`, which refuses those windows; they say only a person can answer it
  (#16).
