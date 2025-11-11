<#
.SYNOPSIS
  Multi-RID publisher + packager for rivaxtract.

.DESCRIPTION
  Builds self-contained, single-file binaries for:
    - win-x86, win-x64
    - linux-x64, linux-musl-x64, linux-arm64
    - osx-arm64, osx-x64
  Copies each artifact to: ./publish/<rid>/rivaxtract[.exe]
  Then archives each folder:
    - Windows RIDs:   ./publish/rivaxtract-<rid>.zip
    - Non-Windows:    ./publish/rivaxtract-<rid>.tar.gz

.USAGE
  .\publish.ps1 -Project .\RivaXtractCLI.csproj
#>

param(
  [string]$Project = "./RivaXtractCLI.csproj",
  [string]$Configuration = "Release",
  [string]$TFM = "net8.0",
  [string]$AppName = "rivaxtract",
  [string]$OutputRoot = "./publish"
)

$ErrorActionPreference = "Stop"

# RIDs to publish
$RIDs = @(
  "win-x86",
  "win-x64",
  "linux-x64",
  "linux-musl-x64",
  "linux-arm64",
  "osx-arm64",
  "osx-x64"
)

# Common publish properties
$PublishProps = @(
  "-p:PublishSingleFile=true",
  "-p:SelfContained=true",
  "-p:PublishTrimmed=true",
  "-p:EnableCompressionInSingleFile=true",
  "-p:StripSymbols=true",
  "-p:DebugType=none"
)

# ---------------- helpers ----------------

function Get-SevenZipPath {
  # Try PATH first
  $candidates = @("7z", "7z.exe",
    "$Env:ProgramFiles\7-Zip\7z.exe",
    "$Env:ProgramFiles(x86)\7-Zip\7z.exe")
  foreach ($p in $candidates) {
    try {
      $cmd = Get-Command $p -ErrorAction Stop
      return $cmd.Path
    } catch {}
  }
  return $null
}

$SevenZip = Get-SevenZipPath
$HaveTar  = $null -ne (Get-Command tar -ErrorAction SilentlyContinue)

function Invoke-DotnetPublish {
  param([Parameter(Mandatory=$true)][string]$Rid)
  Write-Host "=== Publishing RID: $Rid ===" -ForegroundColor Cyan

  & dotnet publish $Project `
    -c $Configuration `
    -r $Rid `
    @PublishProps

  if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for RID '$Rid' (exit $LASTEXITCODE)"
  }
}

function Get-ArtifactPath {
  param([Parameter(Mandatory=$true)][string]$Rid)
  $srcDir = Join-Path "bin/$Configuration/$TFM/$Rid/publish" ""
  if (-not (Test-Path $srcDir)) { throw "Publish folder not found: $srcDir" }

  $isWindows = $Rid.StartsWith("win-")
  $expected = if ($isWindows) { Join-Path $srcDir "$AppName.exe" } else { Join-Path $srcDir $AppName }
  if (Test-Path $expected) { return $expected }

  $probe = Get-ChildItem -File $srcDir | Where-Object {
    $_.Name -eq "$AppName.exe" -or $_.Name -eq "$AppName"
  } | Select-Object -First 1
  if ($null -eq $probe) { throw "Could not locate single-file artifact for RID '$Rid' in $srcDir" }
  return $probe.FullName
}

function Copy-Artifact {
  param(
    [Parameter(Mandatory=$true)][string]$Rid,
    [Parameter(Mandatory=$true)][string]$Src
  )
  $dstDir = Join-Path $OutputRoot $Rid
  New-Item -ItemType Directory -Force -Path $dstDir | Out-Null

  $isWindows = $Rid.StartsWith("win-")
  $dst = if ($isWindows) { Join-Path $dstDir "$AppName.exe" } else { Join-Path $dstDir $AppName }

  Copy-Item -Force $Src $dst
  Write-Host "  -> $dst" -ForegroundColor Green
}

function New-Zip {
  param([string]$SourceDir, [string]$ZipPath)

  if ($SevenZip) {
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    # Pass args as an array; let PowerShell handle quoting
    & $SevenZip @('a','-tzip','-mx=9','--', $ZipPath, $SourceDir) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip failed creating zip '$ZipPath' (exit $LASTEXITCODE)" }
  } else {
    # Fallback: Compress-Archive (doesn't preserve Unix exec bits)
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    $parent = Split-Path -Parent $SourceDir
    $name   = Split-Path -Leaf   $SourceDir
    Push-Location $parent
    try {
      Compress-Archive -Path $name -DestinationPath $ZipPath -Force
    } finally { Pop-Location }
  }
}

function New-TarGz {
  param([string]$SourceDir, [string]$TgzPath)

  if ($SevenZip) {
    # Build a sibling .tar path robustly (do not rely on ChangeExtension(".tar.gz"))
    $dir  = Split-Path -Parent $TgzPath
    $base = Split-Path -Leaf   $TgzPath
    $baseNoGz = [System.IO.Path]::GetFileNameWithoutExtension($base)  # strip .gz
    $tmpTar = Join-Path $dir ($baseNoGz + '.tar')

    if (Test-Path $tmpTar) { Remove-Item $tmpTar -Force }
    & $SevenZip @('a','-ttar','--', $tmpTar, $SourceDir) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip failed creating tar '$tmpTar' (exit $LASTEXITCODE)" }

    if (Test-Path $TgzPath) { Remove-Item $TgzPath -Force }
    & $SevenZip @('a','-tgzip','--', $TgzPath, $tmpTar) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip failed gzip'ing '$tmpTar' -> '$TgzPath' (exit $LASTEXITCODE)" }

    Remove-Item $tmpTar -Force
  } elseif ($HaveTar) {
    # Use system tar: tar -czf out.tgz -C parent folderName
    $parent = Split-Path -Parent $SourceDir
    $name   = Split-Path -Leaf   $SourceDir
    if (Test-Path $TgzPath) { Remove-Item $TgzPath -Force }
    Push-Location $parent
    try {
      & tar -czf "$TgzPath" "$name"
      if ($LASTEXITCODE -ne 0) { throw "tar failed creating '$TgzPath' (exit $LASTEXITCODE)" }
    } finally { Pop-Location }
  } else {
    throw "Neither 7-Zip nor tar available to create tar.gz"
  }
}


function Archive-RidFolder {
  param([Parameter(Mandatory=$true)][string]$Rid)

  $srcDir = Join-Path $OutputRoot $Rid
  if (-not (Test-Path $srcDir)) { throw "Folder to archive not found: $srcDir" }

  $baseName = "$AppName-$Rid"
  if ($Rid.StartsWith("win-")) {
    $zip = Join-Path $OutputRoot "$baseName.zip"
    New-Zip -SourceDir $srcDir -ZipPath $zip
    Write-Host "  + zip: $zip" -ForegroundColor Yellow
  } else {
    $tgz = Join-Path $OutputRoot "$baseName.tar.gz"
    New-TarGz -SourceDir $srcDir -TgzPath $tgz
    Write-Host "  + tgz: $tgz" -ForegroundColor Yellow
  }
}

# --------------- main ---------------

# Ensure output root exists
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

if ($SevenZip) {
  Write-Host "Using 7-Zip at: $SevenZip" -ForegroundColor DarkGray
} else {
  Write-Host "7-Zip not found; using Compress-Archive for .zip and tar.exe for .tar.gz (if present)" -ForegroundColor DarkGray
}

foreach ($rid in $RIDs) {
  Invoke-DotnetPublish -Rid $rid
  $artifact = Get-ArtifactPath -Rid $rid
  Copy-Artifact -Rid $rid -Src $artifact
  Archive-RidFolder -Rid $rid
}

Write-Host "`nAll done. Artifacts and archives are under '$OutputRoot'." -ForegroundColor Green
Write-Host "Note: ZIP does not preserve Unix execute bits; Linux/macOS users may need 'chmod +x $AppName' after extracting."
