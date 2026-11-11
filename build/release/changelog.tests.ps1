# The rules changelog.ps1 follows, run by the release workflow before notes.ps1 uses it. Plain PowerShell: no Pester here.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'changelog.ps1'
$dir = Join-Path ([System.IO.Path]::GetTempPath()) "changelog-tests-$PID"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$full = Join-Path $dir 'CHANGELOG.md'
@'
# Changelog

## 0.2.0

- Assemblies.

## 0.1.0 (2026-11-11)

First release.

- Parts.

## 0.0.9
'@ | Set-Content -LiteralPath $full -Encoding utf8
$empty = Join-Path $dir 'EMPTY.md'
"# Changelog`n`n## 0.1.0`n`n## 0.0.1`n`n- old" | Set-Content -LiteralPath $empty -Encoding utf8
$one = Join-Path $dir 'ONE.md'
'## 0.1.0' | Set-Content -LiteralPath $one -Encoding utf8
$failures = 0
function Case([string]$name, [string]$path, [string]$version, [string]$expected) {
    try { $got = (& $script -Path $path -Version $version) -join "`n" } catch { $got = "error=$($_.Exception.Message)" }
    $ok = if ($expected) { $got -ceq $expected } else { $got -like 'error=*' }
    if ($ok) { Write-Output "ok   $name" } else { $script:failures++; Write-Output "FAIL $name : got '$got'" }
}
$first = "First release.`n`n- Parts."
Case 'a release reads its own section' $full '0.1.0' $first
Case 'the newest section ends at the next heading' $full '0.2.0' '- Assemblies.'
Case 'a release candidate reads its release' $full '0.1.0-rc.1' $first
Case 'a dry run with a commit reads its release' $full '0.1.0-dryrun.4+9d29439' $first
Case 'a version with no section is refused' $full '0.3.0' ''
Case 'a heading is matched whole, not by prefix' $full '0.1' ''
Case 'an empty section is refused' $empty '0.1.0' ''
Case 'an empty last section is refused' $full '0.0.9' ''
Case 'a one-line file with only a heading is refused' $one '0.1.0' ''
Case 'a missing file is refused' (Join-Path $dir 'NONE.md') '0.1.0' ''
Remove-Item -LiteralPath $dir -Recurse -Force
if ($failures) { throw "$failures changelog case(s) failed" }
Write-Output 'changelog rules: all cases pass'
