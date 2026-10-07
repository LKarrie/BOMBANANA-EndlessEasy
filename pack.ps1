# Builds the plugin and produces a Thunderstore-ready zip in dist\.
#   powershell -ExecutionPolicy Bypass -File pack.ps1
#
# The zip gets manifest.json, README.md, CHANGELOG.md and icon.png at its root plus
# plugins\<dll>, which is exactly what Thunderstore expects.
$ErrorActionPreference = "Stop"

$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj  = "$root\BombananaEndlessEasy\BombananaEndlessEasy.csproj"
$built = "$root\BombananaEndlessEasy\bin\Release\net6.0\BombananaEndlessEasy.dll"
$stage = "$root\dist\pkg"

# Find the .NET SDK: on PATH first, then the usual per-user install location.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe" }
if (-not (Test-Path $dotnet)) { throw "dotnet SDK not found. Install it or put it on PATH." }

Write-Host "==> Building (also deploys into the r2modman profile)" -ForegroundColor Cyan
& $dotnet build $proj -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "==> Staging" -ForegroundColor Cyan
if (Test-Path "$root\dist") { Remove-Item "$root\dist" -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$stage\plugins" | Out-Null
Copy-Item $built "$stage\plugins\" -Force

foreach ($f in @("manifest.json", "README.md", "CHANGELOG.md", "icon.png")) {
    if (-not (Test-Path "$root\$f")) { throw "missing $f in the repository root" }
    Copy-Item "$root\$f" $stage -Force
}

$manifest = Get-Content "$root\manifest.json" -Raw | ConvertFrom-Json
$zip = "$root\dist\EndlessEasy-$($manifest.version_number).zip"

Write-Host "==> Zipping" -ForegroundColor Cyan
Compress-Archive -Path "$stage\*" -DestinationPath $zip
Write-Host "==> $zip" -ForegroundColor Green

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::OpenRead($zip).Entries |
    ForEach-Object { "    {0,-40} {1,8} bytes" -f $_.FullName, $_.Length }
