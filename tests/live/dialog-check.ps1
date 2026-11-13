<#
.SYNOPSIS
  swmcp 2.B live check: holds one connection to the add-in, raises message boxes over SOLIDWORKS with diag.dialog,
  answers them with dialog.respond, and checks that a dialog opened by hand blocks new requests. It never ends or
  restarts SOLIDWORKS, and it refuses to raise anything while a document it was not told about is open.
.PARAMETER ExpectedFiles
  Full paths of documents the check may find open. It creates none; any other open document (or an unsaved one)
  refuses the run before the first dialog and again before each later one.
.PARAMETER ManualDialogSeconds
  How long to wait for a dialog opened by hand (Tools > Options) for the nothing-running case; 0 skips that case.
#>
param(
    [string[]] $ExpectedFiles = @(),
    [int] $ManualDialogSeconds = 120
)
$ErrorActionPreference = 'Stop'

$running = @(Get-Process -Name SLDWORKS -ErrorAction SilentlyContinue)
if ($running.Count -ne 1) { Write-Host "REFUSED: expected exactly one SOLIDWORKS, found $($running.Count)"; exit 3 }
$swPid = $running[0].Id

$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', "solidworks-mcp-$swPid", [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(5000)
$utf8 = [System.Text.UTF8Encoding]::new($false)
$reader = [System.IO.StreamReader]::new($pipe, $utf8)
$writer = [System.IO.StreamWriter]::new($pipe, $utf8)
$writer.NewLine = "`n"
$writer.AutoFlush = $true
$script:nextId = 0
$script:results = [ordered]@{}

# Sends one request and returns its whole response (result or error); event notifications are skipped.
function Send-Request([string] $Method, [hashtable] $Params = @{}, [int] $DeadlineMs = 10000) {
    $id = ++$script:nextId
    $writer.WriteLine((@{ jsonrpc = '2.0'; id = $id; method = $Method; params = $Params; deadline_ms = $DeadlineMs } | ConvertTo-Json -Compress -Depth 10))
    while ($true) {
        $line = $reader.ReadLine()
        if ($null -eq $line) { throw "the add-in closed the connection" }
        $msg = $line | ConvertFrom-Json -Depth 50
        if ($null -eq $msg.PSObject.Properties['id']) { continue }
        if ($null -eq $msg.id -and $msg.PSObject.Properties['error']) { throw "refused: $($msg.error.data.message)" }
        if ($msg.id -ne $id) { continue }
        return $msg
    }
}

function Invoke-Addin([string] $Method, [hashtable] $Params = @{}, [int] $DeadlineMs = 10000) {
    $msg = Send-Request $Method $Params $DeadlineMs
    if ($msg.PSObject.Properties['error']) { throw "$Method failed: $($msg.error.data.code): $($msg.error.data.message)" }
    return $msg.result
}

function Check([string] $Case, [bool] $Ok, [string] $Evidence) {
    $script:results[$Case] = $Ok
    Write-Host ("{0} {1}: {2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Case, $Evidence)
}

# Refuses (exit 3) unless every open document is expected and no dialog is open; returns addin.state.
function Assert-Safe {
    $state = Invoke-Addin 'addin.state'
    $expected = @($ExpectedFiles | ForEach-Object { [System.IO.Path]::GetFullPath($_).ToLowerInvariant() })
    $foreign = @($state.documents | Where-Object { -not $_.path -or ($expected -notcontains $_.path.ToLowerInvariant()) })
    if ($foreign.Count -gt 0) {
        Write-Host "REFUSED: documents are open that this check was not told about (or never saved):"
        foreach ($d in $foreign) { Write-Host "  $($d.title)  $($d.path)" }
        $pipe.Dispose()
        exit 3
    }
    if ($state.dialog) {
        Write-Host "REFUSED: a dialog is already open in SOLIDWORKS: $($state.dialog.title)"
        $pipe.Dispose()
        exit 3
    }
    return $state
}

function Buttons($Dialog) { return (@($Dialog.buttons | ForEach-Object { "$($_.text) ($($_.control_id))" }) -join ', ') }

$hello = Invoke-Addin 'session.hello' @{ protocol_version = 10; client_version = 'live-check-2b' }
Write-Host "hello: SOLIDWORKS $($hello.sw_revision), add-in $($hello.addin_version), process $($hello.process_id)"
$state = Assert-Safe
Check 'dialog watch alive' ([bool]$state.dialog_watch_alive) "dialog_watch_alive=$($state.dialog_watch_alive)"

# 1. A dialog raised by a running request: dialog_open, shown in addin.state, answered by text with its outcome.
$null = Assert-Safe
$open = Send-Request 'diag.dialog' @{ title = 'swmcp 2.B live check'; text = 'Raised by the swmcp live check; it is answered automatically.'; buttons = 'ok_cancel' } 10000
$err = $open.error.data
Check 'dialog during an operation answers dialog_open' ($err.code -eq 'dialog_open' -and $err.details.started -eq $true) `
    "code=$($err.code) started=$($err.details.started) dialog=$($err.details.dialog.id) title='$($err.details.dialog.title)' buttons=[$(Buttons $err.details.dialog)]"
$dialogId = $err.details.dialog.id
$state = Invoke-Addin 'addin.state'
Check 'addin.state shows the dialog and its request' ($state.dialog.id -eq $dialogId -and $state.running.method -eq 'diag.dialog' -and $state.running.answered -eq $true) `
    "dialog=$($state.dialog.id) request_id=$($state.dialog.request_id) running=$($state.running.method) answered=$($state.running.answered)"
$resp = Invoke-Addin 'dialog.respond' @{ dialog = $dialogId; button = 'cancel'; wait_ms = 5000 }
Check 'respond by text returns the outcome' ($resp.outcome -eq 'result' -and $resp.result.pressed -eq 'cancel') `
    "pressed=$($resp.pressed) control_id=$($resp.control_id) outcome=$($resp.outcome) result=$($resp.result | ConvertTo-Json -Compress)"
$stale = Send-Request 'dialog.respond' @{ dialog = $dialogId; button = 'OK' }
Check 'a closed dialog id is refused' ($stale.error.data.code -eq 'invalid_argument') "code=$($stale.error.data.code)"

# 2. Close, which posts WM_CLOSE (a Cancel on an OK/Cancel box), and a button by control id.
$null = Assert-Safe
$open = Send-Request 'diag.dialog' @{ title = 'swmcp 2.B live check (close)'; text = 'Raised by the swmcp live check; it is closed automatically.'; buttons = 'ok_cancel' } 10000
$resp = Invoke-Addin 'dialog.respond' @{ dialog = $open.error.data.details.dialog.id; button = 'close'; wait_ms = 5000 }
Check 'close sends WM_CLOSE' ($resp.pressed -eq 'close' -and $resp.outcome -eq 'result' -and $resp.result.pressed -eq 'cancel') `
    "pressed=$($resp.pressed) outcome=$($resp.outcome) result=$($resp.result | ConvertTo-Json -Compress)"
$null = Assert-Safe
$open = Send-Request 'diag.dialog' @{ title = 'swmcp 2.B live check (yes/no)'; text = 'Raised by the swmcp live check; answered by control id.'; buttons = 'yes_no' } 10000
$no = @($open.error.data.details.dialog.buttons | Where-Object { $_.text -eq 'No' })[0]
$resp = Invoke-Addin 'dialog.respond' @{ dialog = $open.error.data.details.dialog.id; button = "$($no.control_id)"; wait_ms = 5000 }
Check 'respond by control id' ($resp.outcome -eq 'result' -and $resp.result.pressed -eq 'no') "control_id=$($no.control_id) pressed=$($resp.pressed) result=$($resp.result | ConvertTo-Json -Compress)"

# 3. A dialog opened by hand while nothing runs: requests are refused unstarted until it is answered.
if ($ManualDialogSeconds -gt 0) {
    $null = Assert-Safe
    Write-Host "open Tools > Options in SOLIDWORKS now (waiting up to $ManualDialogSeconds s)"
    $until = (Get-Date).AddSeconds($ManualDialogSeconds)
    $state = $null
    while ((Get-Date) -lt $until) {
        $state = Invoke-Addin 'addin.state'
        if ($state.dialog) { break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $state.dialog) {
        Check 'a dialog opened by hand is seen' $false "no dialog in addin.state within $ManualDialogSeconds s"
    } else {
        Check 'a dialog opened by hand is seen' ($null -eq $state.dialog.request_id) "title='$($state.dialog.title)' buttons=[$(Buttons $state.dialog)] request_id=$($state.dialog.request_id)"
        $blocked = Send-Request 'session.status' @{} 10000
        Check 'a request under a dialog is not started' ($blocked.error.data.code -eq 'dialog_open' -and $blocked.error.data.details.started -eq $false) `
            "code=$($blocked.error.data.code) started=$($blocked.error.data.details.started)"
        $button = if (@($state.dialog.buttons | Where-Object { $_.text -eq 'Cancel' }).Count -gt 0) { 'Cancel' } else { 'close' }
        $resp = Invoke-Addin 'dialog.respond' @{ dialog = $state.dialog.id; button = $button }
        Check 'respond with nothing running' ($resp.outcome -eq 'no_request') "pressed=$($resp.pressed) outcome=$($resp.outcome)"
        Start-Sleep -Seconds 1
        $after = Send-Request 'session.status' @{} 10000
        Check 'requests run again once it is answered' ($null -eq $after.PSObject.Properties['error']) "error=$($after.error.data.code)"
    }
}

# 4. The events the add-in raised, in order.
$events = Invoke-Addin 'addin.events'
$kinds = @($events.events | ForEach-Object { $_.kind })
$dialogKinds = @($kinds | Where-Object { $_ -like 'dialog.*' -or $_ -eq 'operation.completed' }) -join ', '
Check 'dialog events' ($kinds -contains 'dialog.opened' -and $kinds -contains 'dialog.answered' -and $kinds -contains 'dialog.closed' -and $kinds -contains 'operation.completed') $dialogKinds
$byAgent = @($events.events | Where-Object { $_.kind -eq 'dialog.answered' -and $_.data.by -eq 'agent' }).Count
Check 'every answer was the agent''s' ($byAgent -ge 3 -and @($events.events | Where-Object { $_.kind -eq 'dialog.answered' -and $_.data.by -ne 'agent' }).Count -eq 0) "answered by agent: $byAgent"

$pipe.Dispose()
$failed = @($script:results.GetEnumerator() | Where-Object { -not $_.Value })
Write-Host "$($script:results.Count - $failed.Count) of $($script:results.Count) checks passed"
if ($failed.Count -gt 0) { exit 1 }
exit 0
