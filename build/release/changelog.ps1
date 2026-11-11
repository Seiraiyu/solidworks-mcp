<#
.SYNOPSIS
  Prints the body of a changelog's section for a version: the lines after its "## <version>" heading, up to the next
  "## " heading, trimmed. A pre-release or a build (0.1.0-rc.1, 0.1.0-dryrun.4+9d29439) reads its release's section
  (0.1.0). Fails when the file has no such section or the section is empty, so a release cannot ship without notes.
#>
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
$release = ($Version -split '[-+]', 2)[0]
$lines = @(Get-Content -LiteralPath $Path -Encoding utf8)
$heading = '^## ' + [regex]::Escape($release) + '(\s|$)'
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $heading) { $start = $i; break }
}
if ($start -lt 0) { throw "$Path has no '## $release' section" }
$body = [System.Collections.Generic.List[string]]::new()
for ($i = $start + 1; $i -lt $lines.Count -and $lines[$i] -notmatch '^## '; $i++) { $body.Add($lines[$i]) }
$text = ($body -join "`n").Trim()
if (-not $text) { throw "$Path's '## $release' section is empty" }
Write-Output $text
