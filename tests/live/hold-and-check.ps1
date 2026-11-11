<#
.SYNOPSIS
  swmcp live checks: holds one connection to the add-in, reports its state, and optionally ends SOLIDWORKS by
  process id -- only when every open document is one this check created. It is the only way a live check ends
  SOLIDWORKS: -Close for a normal close, -Kill for a crash.
.PARAMETER ExpectedFiles
  Full paths of every document the live check created; @() when it created none. Any other open document (or an
  unsaved one) refuses -Kill and -Close.
.PARAMETER HoldSeconds
  How long to hold the connection (preferences applied) before the final check, for inspecting SOLIDWORKS.
.PARAMETER Kill
  End SOLIDWORKS (Stop-Process -Id) after the final check, leaving the preferences applied so the journal is exercised.
.PARAMETER Close
  Close SOLIDWORKS normally after the final check: disconnect (so the add-in restores the preferences), then close
  the main window of the process addin.state names, and wait up to 120 s for it to exit.
#>
param(
    [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $ExpectedFiles,
    [int] $HoldSeconds = 0,
    [switch] $Kill,
    [switch] $Close
)
$ErrorActionPreference = 'Stop'
if ($Kill -and $Close) { Write-Host "REFUSED: -Kill and -Close cannot be used together"; exit 2 }

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

function Invoke-Addin([string] $Method, [hashtable] $Params = @{}, [int] $DeadlineMs = 10000) {
    $id = ++$script:nextId
    $writer.WriteLine((@{ jsonrpc = '2.0'; id = $id; method = $Method; params = $Params; deadline_ms = $DeadlineMs } | ConvertTo-Json -Compress -Depth 10))
    while ($true) {
        $line = $reader.ReadLine()
        if ($null -eq $line) { throw "the add-in closed the connection" }
        $msg = $line | ConvertFrom-Json -Depth 50
        if ($null -eq $msg.PSObject.Properties['id']) { continue }                     # an event notification
        if ($null -eq $msg.id -and $msg.PSObject.Properties['error']) { throw "refused: $($msg.error.data.message)" }
        if ($msg.id -ne $id) { continue }
        if ($msg.PSObject.Properties['error']) { throw "$Method failed: $($msg.error.data.code): $($msg.error.data.message)" }
        return $msg.result
    }
}

function Get-Foreign($State) {
    $expected = @($ExpectedFiles | ForEach-Object { [System.IO.Path]::GetFullPath($_).ToLowerInvariant() })
    return @($State.documents | Where-Object { -not $_.path -or ($expected -notcontains $_.path.ToLowerInvariant()) })
}

function Show-State($State) {
    Write-Host "process $($State.process_id), add-in $($State.addin_version), documents as of $($State.documents_as_of):"
    foreach ($d in $State.documents) { Write-Host ("  {0,-8} dirty={1,-5} {2}  {3}" -f $d.type, $d.dirty, $d.title, $d.path) }
    Write-Host "preferences applied=$($State.preferences_applied) not_applied=[$($State.preferences_not_applied -join ', ')] recovered=$($State.preferences_recovered)"
}

$hello = Invoke-Addin 'session.hello' @{ protocol_version = 8; client_version = 'live-check-2a' }
Write-Host "hello: SOLIDWORKS $($hello.sw_revision), add-in $($hello.addin_version)"
$state = Invoke-Addin 'addin.state'
if ($state.process_id -ne $swPid) { Write-Host "REFUSED: the pipe for $swPid reports process $($state.process_id)"; exit 3 }
Show-State $state

if ($HoldSeconds -gt 0) {
    Write-Host "holding the connection (preferences applied) for $HoldSeconds s: inspect SOLIDWORKS now, and close any dialog you open"
    Start-Sleep -Seconds $HoldSeconds
}

if ($Kill -or $Close) {
    $state = Invoke-Addin 'addin.state'
    Show-State $state
    $foreign = Get-Foreign $state
    if ($foreign.Count -gt 0) {
        Write-Host "REFUSED: documents are open that this check did not create (or never saved):"
        foreach ($d in $foreign) { Write-Host "  $($d.title)  $($d.path)" }
        $pipe.Dispose()
        exit 3
    }
    if ($state.PSObject.Properties['dialog'] -and $state.dialog) {
        Write-Host "REFUSED: SOLIDWORKS is showing a dialog: $($state.dialog.title)"
        $pipe.Dispose()
        exit 3
    }
    if ($Kill) {
        Write-Host "ending SOLIDWORKS process $($state.process_id) with the preferences still applied"
        Stop-Process -Id $state.process_id -Force
        exit 0
    }
    $target = Get-Process -Id $state.process_id
    $pipe.Dispose()
    Start-Sleep -Seconds 1   # the add-in restores the preferences when the client disconnects
    Write-Host "closing SOLIDWORKS process $($state.process_id) normally"
    [void]$target.CloseMainWindow()
    if (-not $target.WaitForExit(120000)) {
        Write-Host "SOLIDWORKS process $($state.process_id) did not exit within 120 s (it may be showing a prompt); nothing more was done"
        exit 4
    }
    Write-Host "SOLIDWORKS process $($state.process_id) exited"
    exit 0
}
$pipe.Dispose()
exit 0
