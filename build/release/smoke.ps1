<#
.SYNOPSIS
  Checks a release zip as a user meets it: its SHA-256, swmcp.exe running with no .NET for it, and swmcp install and
  uninstall from the unzipped folder with every file marked as downloaded first. It installs into this user's
  %LOCALAPPDATA% and HKCU, so it runs only on a GitHub-hosted runner, which is thrown away afterwards. Installing on
  seiraiyu2 is swmcp-release:1's e2e.
#>
param(
    [Parameter(Mandatory)][string]$Zip,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw "smoke.ps1 installs into this user's %LOCALAPPDATA% and HKCU: it runs only on a GitHub-hosted runner"
}

function Check([bool]$ok, [string]$what) {
    if (-not $ok) { throw "smoke: $what" }
    Write-Output "ok: $what"
}
function Swmcp([string]$exe, [string[]]$arguments) {
    $text = & $exe @arguments | Out-String
    [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text }
}

$zipPath = (Resolve-Path -LiteralPath $Zip).Path
$expected = (Get-Content -LiteralPath "$zipPath.sha256" -Raw).Split(' ')[0]
Check ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expected) 'the zip matches its .sha256'

$dir = Join-Path $env:RUNNER_TEMP 'swmcp-smoke'
if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
Expand-Archive -LiteralPath $zipPath -DestinationPath $dir
# Mark every file as downloaded from the internet, as a browser and Explorer's Extract All do.
Get-ChildItem -LiteralPath $dir -Recurse -File | ForEach-Object {
    Set-Content -LiteralPath $_.FullName -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
}
$exe = Join-Path $dir 'swmcp.exe'
Check (Test-Path -LiteralPath (Join-Path $dir 'addin\SwAgent.AddIn.dll')) 'the add-in is in addin\ beside swmcp.exe'

# Self-contained: no DOTNET_ROOT (setup-dotnet sets one) and no dotnet.exe on PATH.
Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
$env:PATH = ($env:PATH -split ';' | Where-Object { $_ -and -not (Test-Path -LiteralPath (Join-Path $_ 'dotnet.exe')) }) -join ';'
$v = Swmcp $exe @('--version')
Check ($v.Exit -eq 0 -and $v.Text.Trim().StartsWith($Version)) "swmcp --version runs with no .NET and says $Version (it said: $($v.Text.Trim()))"

$i = Swmcp $exe @('install')
Check ($i.Exit -eq 0) "swmcp install exits 0 (exit $($i.Exit): $($i.Text))"
$installed = $i.Text | ConvertFrom-Json
$target = Join-Path $env:LOCALAPPDATA "solidworks-mcp\app\$Version"
Check ($installed.version -eq $Version -and $installed.path -eq $target) "installed $Version at $target"
Check ($installed.marks_of_the_web_removed -gt 0) "install removed $($installed.marks_of_the_web_removed) marks of the web"
$marked = @(Get-ChildItem -LiteralPath $target -Recurse -File | Where-Object { Get-Item -LiteralPath $_.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue })
Check ($marked.Count -eq 0) 'no installed file carries a mark of the web'
Check ((Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'solidworks-mcp\app\current.txt') -Raw).Trim() -eq $Version) 'current.txt names the version'
Check ($installed.claude_code -like 'claude mcp add*serve') "install printed: $($installed.claude_code)"

$s = Swmcp $installed.swmcp @('addin', 'status')
$status = $s.Text | ConvertFrom-Json
Check ($s.Exit -eq 0 -and $status.registered -and $status.path -eq (Join-Path $target 'addin\SwAgent.AddIn.dll')) 'the installed copy is registered under HKCU'

$u = Swmcp $exe @('uninstall')
Check ($u.Exit -eq 0) "swmcp uninstall exits 0 (exit $($u.Exit): $($u.Text))"
$removed = $u.Text | ConvertFrom-Json
Check (@($removed.removed) -contains $target) "uninstall removed $target"
$after = Swmcp $exe @('addin', 'status')
Check ($after.Exit -eq 3) 'nothing is registered afterwards (addin status exits 3)'
Write-Output "smoke passed for $Version"
# The last swmcp call exits 3 on purpose; GitHub's pwsh step exits with $LASTEXITCODE, so say success here.
exit 0
