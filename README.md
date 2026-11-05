# solidworks-mcp

Lets an AI agent drive SOLIDWORKS through the [Model Context Protocol](https://modelcontextprotocol.io). It has three parts:

- **`SwAgent.AddIn`** runs inside SOLIDWORKS (.NET Framework 4.8). It serves JSON-RPC on `\\.\pipe\solidworks-mcp-<pid>` and runs every request on SOLIDWORKS' UI thread.
- **`SwAgent.Client`** is the one library that talks to that pipe.
- **`swmcp`** is the MCP server (`swmcp serve`, the default) and the command line (`swmcp call`, `swmcp addin ...`).

Phase 1 offers one tool, `sw_status`: the SOLIDWORKS revision, the active document and whether it has unsaved changes.

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

## Install

```powershell
$app = "$env:LOCALAPPDATA\solidworks-mcp\app"
dotnet publish src/SwAgent.Mcp -c Release -o $app
& "$app\swmcp.exe" addin register
```

`addin register` writes the add-in's COM registration and SOLIDWORKS' add-in keys under `HKCU`. `swmcp addin status` shows them, and `swmcp addin unregister` removes them.

## Load the add-in

SOLIDWORKS does not load add-ins registered only for the current user at startup. So after each start of SOLIDWORKS, load it:

```powershell
& "$app\swmcp.exe" addin load
```

This attaches to the running SOLIDWORKS (it never starts one) and asks it to load the registered add-in. Name an instance with `--pid` when more than one is running. Until it is loaded, `sw_status` answers `addin_not_loaded` with this same instruction.

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

The add-in serves one client at a time. While an MCP client is connected, `swmcp call` from a terminal answers `busy`.

## From a terminal

```powershell
swmcp call session.status
```

`call` prints the result, or the typed error, as JSON. It exits 0 on success and with a code per error otherwise (`swmcp --help`).

`addin.state` answers even while SOLIDWORKS is busy with another request, and shows what is running, what waits, the open documents and whether the automation preferences are applied:

```powershell
& "$app\swmcp.exe" call addin.state
```

A call that names no `--deadline-ms` gets its operation family's default (10 s for `session.*`, 5 s for `addin.*`). Any deadline must be between 1 ms and one hour.

A request whose deadline passes before SOLIDWORKS can start it is answered `busy` (it never ran; retrying is safe). One that started but ran past its deadline is answered `timeout`; it keeps running, and its outcome is raised as an `operation.completed` event.

While a client is connected, the add-in turns off three SOLIDWORKS preferences that stall automation: the dimension value box on creation, the save reminder, and the prompt to close an open sketch. Your values come back when the client disconnects or SOLIDWORKS closes. If SOLIDWORKS crashes first, they come back the next time the add-in loads, from `%LOCALAPPDATA%\solidworks-mcp\preferences-journal.json`.

When SOLIDWORKS shows a modal dialog, a request that raised it is answered `dialog_open` with the dialog's id, title, text and buttons, instead of hanging; the request goes on waiting on the dialog, and its outcome follows as an `operation.completed` event. While a dialog is open nothing else starts on SOLIDWORKS' UI thread: new requests are answered `dialog_open` with `started: false`. `addin.state` shows the open dialog, and `dialog.respond` presses one of its buttons, by text or control id, or closes it:

```powershell
& "$app\swmcp.exe" call addin.state
& "$app\swmcp.exe" call dialog.respond '{"dialog":"d3-1a2b","button":"Cancel"}'
```

Each `swmcp call` opens a new connection, and a new connection's hello waits for SOLIDWORKS' UI thread. So from a terminal this answers a dialog that no request is waiting on (one opened by hand). A dialog that a request raised is answered on the connection that sent the request; `swmcp serve` keeps one open from phase 2.C.

The add-in presses a button only when asked, or for a prompt on its built-in allow-list of measured, always-safe answers (empty in this release).

## Logs

Logs are in `%LOCALAPPDATA%\solidworks-mcp\logs\` and are kept 14 days:

- `addin-<yyyyMMdd>.log` is the add-in's log: load, unload, and every request with its thread.
- `server-<yyyyMMdd>.log` is the MCP server's log: every tool call with its outcome code and duration. The server also logs to stderr.

`SWMCP_LOG_LEVEL` (`trace`, `debug`, `information`, `warning`, `error`) sets the server's minimum level; the default is `information`. At `debug` the server also logs each call's parameters.
