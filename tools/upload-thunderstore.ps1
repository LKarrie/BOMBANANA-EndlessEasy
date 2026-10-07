# Uploads the packaged zip to Thunderstore using the same upload flow as the official CLI
# (verified against the `thunderstore` Rust crate's usermedia.rs / submission.rs):
#
#   1. POST /api/experimental/usermedia/initiate-upload   -> upload uuid + part URLs
#   2. PUT  each part URL (raw bytes)                     -> ETag per part
#   3. POST /api/experimental/usermedia/{uuid}/finish-upload
#   4. POST /api/experimental/submission/submit           -> publish for review
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\upload-thunderstore.ps1 `
#       -Token "tss_xxx" -Team "YourThunderstoreTeam"
param(
    [Parameter(Mandatory = $true)][string]$Token,
    # Your Thunderstore team/username. Thunderstore calls it the namespace, and the API
    # requires author_name to match it exactly.
    [Parameter(Mandatory = $true)][string]$Team,
    [string]$Community = "bombanana",
    [string]$Zip
)

$ErrorActionPreference = "Stop"
$base = "https://thunderstore.io"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

if (-not $Zip) {
    $Zip = Get-ChildItem "$root\dist" -Filter "EndlessEasy-*.zip" -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime | Select-Object -Last 1 -Expand FullName
}
if (-not $Zip -or -not (Test-Path $Zip)) { throw "no package zip found; run pack.ps1 first" }

$manifest = Get-Content "$root\manifest.json" -Raw | ConvertFrom-Json
$bytes = [System.IO.File]::ReadAllBytes($Zip)
$auth = @{ Authorization = "Bearer $Token" }

Write-Host "==> Uploading $Zip ($($bytes.Length) bytes) as '$($manifest.name)' to '$Community' for team '$Team'" -ForegroundColor Cyan

# --- 1. initiate ------------------------------------------------------------
$initBody = @{ name = $manifest.name; size = $bytes.Length } | ConvertTo-Json
$init = Invoke-RestMethod -Uri "$base/api/experimental/usermedia/initiate-upload" -Method Post `
    -Headers $auth -ContentType "application/json" -Body $initBody

$uuid = $init.user_media.uuid
if (-not $uuid) { throw "initiate-upload returned no uuid: $($init | ConvertTo-Json -Depth 6)" }
Write-Host "    uuid: $uuid"

# --- 2. upload parts -------------------------------------------------------
$parts = @()
foreach ($part in $init.upload_urls) {
    $offset = [int]$part.offset
    $length = [int]$part.length
    $slice = New-Object byte[] $length
    [Array]::Copy($bytes, $offset, $slice, 0, $length)

    Write-Host "    part $($part.number): $length bytes @ $offset"
    $resp = Invoke-WebRequest -Uri $part.url -Method Put -Body $slice -ContentType "application/octet-stream"
    $etag = $resp.Headers["ETag"]
    if (-not $etag) { throw "part $($part.number) returned no ETag" }
    $parts += @{ tag = $etag; number = $part.number }
}

# --- 3. finish -------------------------------------------------------------
$finishBody = @{ parts = $parts } | ConvertTo-Json -Depth 6
Invoke-RestMethod -Uri "$base/api/experimental/usermedia/$uuid/finish-upload" -Method Post `
    -Headers $auth -ContentType "application/json" -Body $finishBody | Out-Null
Write-Host "    upload finished"

# --- 4. submit -------------------------------------------------------------
# author_name must be the Thunderstore team the package belongs to; the package name and
# version come from manifest.json inside the zip.
$submitBody = @{
    author_name          = $Team
    categories           = @()
    community_categories = @{}
    communities          = @($Community)
    has_nsfw_content     = $false
    upload_uuid          = $uuid
} | ConvertTo-Json -Depth 6

$result = Invoke-RestMethod -Uri "$base/api/experimental/submission/submit" -Method Post `
    -Headers $auth -ContentType "application/json" -Body $submitBody

Write-Host "==> Submitted" -ForegroundColor Green
$result | ConvertTo-Json -Depth 8
