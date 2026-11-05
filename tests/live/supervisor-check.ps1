<#
.SYNOPSIS
  swmcp 2.C live check: the add-in loaded automatically, and swmcp serve reattaching after the user restarts
  SOLIDWORKS. It drives swmcp.exe exactly as an agent does (swmcp call, and swmcp serve over stdio as an MCP client).
  It never starts, closes or kills SOLIDWORKS, and it makes no COM call of its own.
.PARAMETER Phase
  AutoLoad: one SOLIDWORKS running without the add-in and with no document open (its title shows none). swmcp call
  loads it, then the four tools are checked through swmcp serve. Reattach: no SOLIDWORKS running. swmcp serve answers not_running, then waits while the user starts
  SOLIDWORKS from the 3DEXPERIENCE tab, until sw_status says ready with the add-in loaded by the server.
.PARAMETER App
  The folder swmcp.exe was published to.
.PARAMETER WaitSeconds
  Reattach: how long to wait for the user's SOLIDWORKS to become ready.
#>
param(
    [Parameter(Mandatory)] [ValidateSet('AutoLoad', 'Reattach')] [string] $Phase,
    [string] $App = "$env:LOCALAPPDATA\solidworks-mcp\app",
    [int] $WaitSeconds = 300
)
$ErrorActionPreference = 'Stop'
$swmcp = Join-Path $App 'swmcp.exe'
$script:results = [ordered]@{}

function Check([string] $Case, [bool] $Ok, [string] $Evidence) {
    $script:results[$Case] = $Ok
    Write-Host ("{0} {1}: {2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Case, $Evidence)
}

function Finish {
    $failed = @($script:results.GetEnumerator() | Where-Object { -not $_.Value })
    Write-Host "$($script:results.Count - $failed.Count) of $($script:results.Count) checks passed"
    if ($failed.Count -gt 0) { exit 1 }
    exit 0
}

function PipeUp([int] $SwPid) {
    return @([System.IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { [System.IO.Path]::GetFileName($_) -eq "solidworks-mcp-$SwPid" }).Count -gt 0
}

# ---- swmcp serve, driven over stdio as an MCP client drives it ----

function Start-Serve {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($swmcp, 'serve')
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $script:serve = [System.Diagnostics.Process]::Start($psi)
    $script:serveErr = $script:serve.StandardError.ReadToEndAsync()
    $script:mcpId = 0
    $init = Send-Mcp 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'live-check-2c'; version = '1' } }
    Send-Mcp 'notifications/initialized' @{} -Notification | Out-Null
    return $init
}

function Send-Mcp([string] $Method, [hashtable] $Params, [switch] $Notification, [int] $TimeoutMs = 60000) {
    $msg = @{ jsonrpc = '2.0'; method = $Method; params = $Params }
    if (-not $Notification) { $msg.id = ++$script:mcpId }
    $script:serve.StandardInput.WriteLine(($msg | ConvertTo-Json -Compress -Depth 20))
    $script:serve.StandardInput.Flush()
    if ($Notification) { return $null }
    $until = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $until) {
        $read = $script:serve.StandardOutput.ReadLineAsync()
        if (-not $read.Wait([int][Math]::Max(1, ($until - (Get-Date)).TotalMilliseconds))) { break }
        if ($null -eq $read.Result) { throw "swmcp serve closed its output" }
        $reply = $read.Result | ConvertFrom-Json -Depth 50
        if ($reply.PSObject.Properties['id'] -and $reply.id -eq $msg.id) { return $reply }
    }
    throw "no answer to $Method within $TimeoutMs ms"
}

# Calls a tool; returns @{ IsError; Body } with the tool's JSON text parsed.
function Invoke-Tool([string] $Name, [hashtable] $Arguments = @{}) {
    $reply = Send-Mcp 'tools/call' @{ name = $Name; arguments = $Arguments }
    if ($reply.PSObject.Properties['error']) { throw "$Name failed at the protocol level: $($reply.error.message)" }
    return @{ IsError = [bool]$reply.result.isError; Body = ($reply.result.content[0].text | ConvertFrom-Json -Depth 50) }
}

function Stop-Serve {
    $script:serve.StandardInput.Close()
    $exited = $script:serve.WaitForExit(20000)
    Check 'serve exits when its client closes stdin' ($exited -and $script:serve.ExitCode -eq 0) "exited=$exited code=$(if ($exited) { $script:serve.ExitCode })"
}

if ($Phase -eq 'AutoLoad') {
    $running = @(Get-Process -Name SLDWORKS -ErrorAction SilentlyContinue)
    if ($running.Count -ne 1) { Write-Host "REFUSED: expected exactly one SOLIDWORKS, found $($running.Count)"; exit 3 }
    $swPid = $running[0].Id
    if (PipeUp $swPid) { Write-Host "REFUSED: the add-in is already loaded in SOLIDWORKS $swPid; this phase needs a SOLIDWORKS started without it"; exit 3 }
    # Loading the add-in applies its preferences, so first make sure no document is open: SOLIDWORKS names the active
    # document in its title ("SOLIDWORKS ... - [Part1.SLDPRT]").
    $title = (Get-Process -Id $swPid).MainWindowTitle
    if ($title.Contains(' - [')) { Write-Host "REFUSED: SOLIDWORKS $swPid shows a document ('$title'); nothing was loaded"; exit 3 }

    # 1. swmcp call loads the add-in by itself.
    $out = & $swmcp call addin.state 2>&1
    $code = $LASTEXITCODE
    $state = ($out -join "`n") | ConvertFrom-Json -Depth 50
    Check 'swmcp call loads the add-in' ($code -eq 0 -and (PipeUp $swPid) -and $state.process_id -eq $swPid) "exit=$code pipe_up=$(PipeUp $swPid) process_id=$($state.process_id) addin=$($state.addin_version)"
    if (@($state.documents).Count -gt 0) {
        Write-Host "STOP: documents are open in SOLIDWORKS that this check did not open:"
        foreach ($d in $state.documents) { Write-Host "  $($d.title)  $($d.path)" }
        exit 3
    }

    # 2. swmcp status is the same JSON as sw_status.
    $status = (& $swmcp status 2>&1) -join "`n" | ConvertFrom-Json -Depth 50
    Check 'swmcp status says ready' ($LASTEXITCODE -eq 0 -and $status.state -eq 'ready' -and $status.process_id -eq $swPid) "exit=$LASTEXITCODE state=$($status.state) message='$($status.message)'"

    # 3. The four tools through swmcp serve.
    $init = Start-Serve
    Check 'serve answers initialize' ($init.result.serverInfo.name -eq 'solidworks-mcp') "server=$($init.result.serverInfo.name) $($init.result.serverInfo.version)"
    $tools = @((Send-Mcp 'tools/list' @{}).result.tools)
    $names = @($tools | ForEach-Object { $_.name } | Sort-Object)
    $withDeadline = @($tools | Where-Object { $_.inputSchema.properties.PSObject.Properties['deadline_ms'] }).Count
    Check 'four tools, each with deadline_ms' (($names -join ',') -eq 'sw_dialog_respond,sw_events,sw_reconnect,sw_status' -and $withDeadline -eq 4) "tools=$($names -join ', ') with_deadline_ms=$withDeadline"

    $s = Invoke-Tool 'sw_status'
    Check 'sw_status: ready, responding, watched' (-not $s.IsError -and $s.Body.state -eq 'ready' -and $s.Body.responding -eq $true -and $s.Body.addin.dialog_watch_alive -eq $true -and $null -ne $s.Body.session) `
        "state=$($s.Body.state) responding=$($s.Body.responding) dialog_watch_alive=$($s.Body.addin.dialog_watch_alive) revision=$($s.Body.session.sw_revision) documents=$(@($s.Body.addin.documents).Count)"
    $e = Invoke-Tool 'sw_events' @{ limit = 10 }
    Check 'sw_events answers' (-not $e.IsError -and $e.Body.process_id -eq $swPid) "events=$(@($e.Body.events).Count) last_seq=$($e.Body.last_seq) missed=$($e.Body.missed)"
    $d = Invoke-Tool 'sw_dialog_respond' @{ dialog = 'd0-0'; button = 'OK' }
    Check 'sw_dialog_respond reaches the add-in' ($d.IsError -and $d.Body.code -eq 'invalid_argument') "code=$($d.Body.code) message='$($d.Body.message)'"
    $r = Invoke-Tool 'sw_reconnect' @{ process_id = $swPid }
    Check 'sw_reconnect reconnects' (-not $r.IsError -and $r.Body.state -eq 'ready' -and $r.Body.process_id -eq $swPid) "state=$($r.Body.state) process_id=$($r.Body.process_id)"
    Stop-Serve
    Finish
}

# ---- Reattach ----
$running = @(Get-Process -Name SLDWORKS -ErrorAction SilentlyContinue)
if ($running.Count -ne 0) { Write-Host "REFUSED: this phase starts with no SOLIDWORKS running; found $($running.Count)"; exit 3 }
$init = Start-Serve
$s = Invoke-Tool 'sw_status'
Check 'sw_status without SOLIDWORKS is not_running, not an error' (-not $s.IsError -and $s.Body.state -eq 'not_running' -and $s.Body.message -like '*3DEXPERIENCE dashboard*') "state=$($s.Body.state) message='$($s.Body.message)'"

Write-Host "start SOLIDWORKS from the 3DEXPERIENCE tab now (waiting up to $WaitSeconds s)"
$seen = [System.Collections.Generic.List[string]]::new()
$until = (Get-Date).AddSeconds($WaitSeconds)
$s = $null
while ((Get-Date) -lt $until) {
    $s = Invoke-Tool 'sw_status' @{ deadline_ms = 30000 }
    $label = "$($s.Body.state)$(if ($s.Body.addin.dialog) { " (dialog '$($s.Body.addin.dialog.title)')" })"
    if ($seen.Count -eq 0 -or $seen[$seen.Count - 1] -ne $label) {
        $seen.Add($label)
        Write-Host ("{0:HH:mm:ss} sw_status: {1}: {2}" -f (Get-Date), $label, $s.Body.message)
    }
    if ($s.Body.state -eq 'ready' -and -not $s.Body.addin.dialog) { break }
    Start-Sleep -Seconds 2
}
$newPid = @(Get-Process -Name SLDWORKS -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
Check 'serve reattaches to the restarted SOLIDWORKS and loads the add-in' ($s.Body.state -eq 'ready' -and $newPid.Count -eq 1 -and $s.Body.process_id -eq $newPid[0] -and (PipeUp $newPid[0])) `
    "states seen: $($seen -join ' -> '); process_id=$($s.Body.process_id) addin=$($s.Body.addin.addin_version)"
if ($s.Body.state -eq 'ready' -and @($s.Body.addin.documents).Count -gt 0) {
    Write-Host "NOTE: documents are open: $(@($s.Body.addin.documents | ForEach-Object { $_.title }) -join ', ')"
}
Stop-Serve
Finish
