<#
.SYNOPSIS
  The version a release run builds and whether it is a dry run, as GITHUB_OUTPUT lines (version=..., dry_run=...).
  A v* tag builds its own version, and makes a release on a push, or on a dispatch with dry_run false. Anything else
  is a dry run, of the dispatched version or 0.1.0-dryrun.<run number>; a dispatch asking for a release from a
  branch is refused.
#>
param(
    [Parameter(Mandatory)][string]$Event,
    [Parameter(Mandatory)][string]$RefType,
    [Parameter(Mandatory)][string]$RefName,
    [Parameter(Mandatory)][string]$RunNumber,
    [string]$InputVersion = '',
    [string]$InputDryRun = ''
)
$ErrorActionPreference = 'Stop'
$semver = '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$'
if ($RefType -eq 'tag') {
    if (-not $RefName.StartsWith('v')) { throw "tag '$RefName' does not start with v" }
    $version = $RefName.Substring(1)
    $dry = $Event -ne 'push' -and $InputDryRun -ne 'false'
}
else {
    if ($Event -eq 'workflow_dispatch' -and $InputDryRun -eq 'false') { throw "a release is made only from a v* tag; this run is on $RefType '$RefName'" }
    $version = if ($InputVersion) { $InputVersion } else { "0.1.0-dryrun.$RunNumber" }
    $dry = $true
}
if ($version -cnotmatch $semver) { throw "'$version' is not a version such as 0.1.0 or 0.1.0-rc.1" }
"version=$version"
"dry_run=$(if ($dry) { 'true' } else { 'false' })"
