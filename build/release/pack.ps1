<#
.SYNOPSIS
  Builds the release zip (design 371): swmcp.exe published self-contained for win-x64 with the add-in in addin\,
  the README and the licence, zipped as solidworks-mcp-<version>-win-x64.zip with its SHA-256 beside it.
.EXAMPLE
  ./build/release/pack.ps1 -Version 0.1.0-local.1 -Output dist
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Output = 'dist'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$out = [IO.Path]::GetFullPath($Output)
$name = "solidworks-mcp-$Version-win-x64"
$stage = Join-Path $out $name
$zip = Join-Path $out "$name.zip"
foreach ($old in @($stage, $zip, "$zip.sha256")) {
    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $out | Out-Null

# The net48 add-in is restored only by a solution restore; the publish builds it through build/AddInPayload.targets.
dotnet restore (Join-Path $repo 'SolidWorksMcp.slnx')
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed ($LASTEXITCODE)" }
dotnet publish (Join-Path $repo 'src\SwAgent.Mcp\SwAgent.Mcp.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

foreach ($file in @('swmcp.exe', 'addin\SwAgent.AddIn.dll', 'addin\SwAgent.AddIn.Core.dll', 'addin\SwAgent.Contracts.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $file))) { throw "the publish has no $file" }
}
Copy-Item -LiteralPath (Join-Path $repo 'README.md'), (Join-Path $repo 'LICENSE') -Destination $stage

[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $name.zip" -NoNewline -Encoding ascii
$files = (Get-ChildItem -LiteralPath $stage -Recurse -File).Count
$mb = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)
Write-Output "packed $zip ($files files, $mb MB)"
Write-Output "sha256 $hash"
