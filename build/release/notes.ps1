<#
.SYNOPSIS
  Writes a release's notes: what to download, how to install, and the live-suite report for the tagged commit, which
  is in the body of the pull request that merged it. A release with no such pull request fails, unless
  -AllowMissingReport (dry runs, whose commit is a pull request's merge preview).
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Output,
    [switch]$AllowMissingReport
)
$ErrorActionPreference = 'Stop'
$json = gh api "repos/$Repository/commits/$Commit/pulls" --jq '[.[] | select(.merged_at != null) | {number, url: .html_url, title}]'
if ($LASTEXITCODE -ne 0) {
    if (-not $AllowMissingReport) { throw "could not ask GitHub which pull request merged ${Commit}" }
    $json = '[]'
}
$prs = @($json | ConvertFrom-Json)
if ($prs.Count -eq 0 -and -not $AllowMissingReport) { throw "no merged pull request has ${Commit}: the release notes need its live-suite report" }
$zip = "solidworks-mcp-$Version-win-x64.zip"
$report = if ($prs.Count -gt 0) {
    ($prs | ForEach-Object { "- [#$($_.number) $($_.title)]($($_.url))" }) -join "`n"
} else {
    "- none found for ${Commit} (dry run)"
}
$notes = @"
solidworks-mcp $Version for SOLIDWORKS 2026 on Windows (x64). The binaries are not signed.

## Install

1. Download ``$zip`` and check it against ``$zip.sha256``: ``(Get-FileHash .\$zip -Algorithm SHA256).Hash``.
2. Unzip it anywhere, such as your Downloads folder.
3. In PowerShell, in that folder: ``.\swmcp.exe install``. It needs no administrator rights, and prints the ``claude mcp add`` line to run next.

The README says what the tools do, what they cannot do yet, and how to turn the escape hatch (``sw_api_call``) off.

## Live-suite report

The parts live suite's report for $($Commit.Substring(0, 12)) is in the pull request that merged it:

$report
"@
Set-Content -LiteralPath $Output -Value $notes -Encoding utf8
Write-Output $notes
