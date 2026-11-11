# The rules version.ps1 follows, run by the release workflow before it uses them. Plain PowerShell: no Pester here.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'version.ps1'
$failures = 0
function Case([string]$name, [hashtable]$p, [string]$version, [string]$dry) {
    try { $got = (& $script @p) -join ';' } catch { $got = "error=$($_.Exception.Message)" }
    $ok = if ($version) { $got -eq "version=$version;dry_run=$dry" } else { $got -like 'error=*' }
    if ($ok) { Write-Output "ok   $name ($got)" } else { $script:failures++; Write-Output "FAIL $name : got '$got'" }
}
Case 'a v tag push releases' @{ Event = 'push'; RefType = 'tag'; RefName = 'v0.1.0'; RunNumber = '9' } '0.1.0' 'false'
Case 'a pre-release tag push releases' @{ Event = 'push'; RefType = 'tag'; RefName = 'v0.2.0-rc.1'; RunNumber = '9' } '0.2.0-rc.1' 'false'
Case 'a pull request is a dry run' @{ Event = 'pull_request'; RefType = 'branch'; RefName = '15/merge'; RunNumber = '9' } '0.1.0-dryrun.9' 'true'
Case 'a dispatch on a branch is a dry run' @{ Event = 'workflow_dispatch'; RefType = 'branch'; RefName = 'main'; RunNumber = '12'; InputDryRun = 'true' } '0.1.0-dryrun.12' 'true'
Case 'a dispatch builds the version given' @{ Event = 'workflow_dispatch'; RefType = 'branch'; RefName = 'main'; RunNumber = '12'; InputDryRun = 'true'; InputVersion = '0.1.0-rc.2' } '0.1.0-rc.2' 'true'
Case 'a dispatch on a tag stays dry by default' @{ Event = 'workflow_dispatch'; RefType = 'tag'; RefName = 'v0.1.0'; RunNumber = '3'; InputDryRun = 'true' } '0.1.0' 'true'
Case 'a dispatch on a tag with dry_run false releases' @{ Event = 'workflow_dispatch'; RefType = 'tag'; RefName = 'v0.1.0'; RunNumber = '3'; InputDryRun = 'false' } '0.1.0' 'false'
Case 'a release from a branch is refused' @{ Event = 'workflow_dispatch'; RefType = 'branch'; RefName = 'main'; RunNumber = '3'; InputDryRun = 'false' } $null $null
Case 'a tag that is not a version is refused' @{ Event = 'push'; RefType = 'tag'; RefName = 'vnext'; RunNumber = '3' } $null $null
Case 'a dispatched version that is not one is refused' @{ Event = 'workflow_dispatch'; RefType = 'branch'; RefName = 'main'; RunNumber = '3'; InputDryRun = 'true'; InputVersion = '1.0' } $null $null
if ($failures) { throw "$failures version case(s) failed" }
Write-Output 'version rules: all cases pass'
