<#
.SYNOPSIS
  Multi-RID publisher + packager for rivaxtract (versioned archives, file-only inside).

.DESCRIPTION
  Builds self-contained, single-file binaries for:
    - win-x86, win-x64
    - linux-x64, linux-musl-x64, linux-arm64
    - osx-arm64, osx-x64
  Copies each artifact to: ./publish/<rid>/rivaxtract[.exe]
  Then archives each RID as:
    - Windows RIDs:   ./publish/rivaxtract-<version>-<rid>.zip   (contains only rivaxtract.exe)
    - Non-Windows:    ./publish/rivaxtract-<version>-<rid>.tar.gz (contains only rivaxtract)

.NOTES
  Building archives on Windows cannot preserve Unix execute bits. Linux/macOS users may need: chmod +x rivaxtract
#>

param(
  [string]$Project = "./RivaXtractCLI.csproj",
  [string]$Configuration = "Release",
  [string]$TFM = "net8.0",
  [string]$AppName = "rivaxtract",
  [string]$OutputRoot = "./publish",
  [string]$Version = "",   # optional override; otherwise read from ./version
  [switch]$Force
)

$ErrorActionPreference = "Stop"

# ---- read version file if not provided ----
function Get-Version {
  param([string]$Provided)
  if ($Provided -and $Provided.Trim()) { return $Provided.Trim() }
  $versionPath = Join-Path -Path $PSScriptRoot -ChildPath "version"
  if (-not (Test-Path $versionPath)) {
    Write-Warning "No -Version specified and '$versionPath' not found. Using '0.0.0'."
    return "0.0.0"
  }
  $v = (Get-Content -Raw $versionPath).Trim()
  if (-not $v) { $v = "0.0.0" }
  return $v
}

$Version = Get-Version -Provided $Version
Write-Host "Version: $Version" -ForegroundColor DarkGray

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

# ------------- helpers ----------------

function Get-SevenZipPath {
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

function Confirm-TargetDir {
  param(
    [Parameter(Mandatory=$true)][string]$Path,
    [switch]$Force
  )
  # If forcing, skip checks
  if ($Force) { return $true }

  if (-not (Test-Path -LiteralPath $Path)) { return $true }

  $nonEmpty = $false
  try {
    $probe = Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop | Select-Object -First 1
    if ($null -ne $probe) { $nonEmpty = $true }
  } catch {
    # If we can't list, err on the side of caution and prompt anyway
    $nonEmpty = $true
  }

  if (-not $nonEmpty) { return $true }

  Write-Warning "Target dir exists and is not empty: $Path"
  try {
    $ans = Read-Host "Proceed? Files may be overwritten. (y/N)"
  } catch {
    return $false
  }
  if ($ans -and ($ans.Trim().ToLower() -in @('y','yes'))) { return $true }
  return $false
}

function Copy-Artifact {
  param(
    [Parameter(Mandatory=$true)][string]$Rid,
    [Parameter(Mandatory=$true)][string]$Src
  )
  $dstDir = Join-Path $OutputRoot $Rid

  if (-not (Confirm-TargetDir -Path $dstDir -Force:$Force)) {
    Write-Warning "Skipping RID '$Rid' at '$dstDir' by user choice."
    return $null
  }

  New-Item -ItemType Directory -Force -Path $dstDir | Out-Null

  $isWindows = $Rid.StartsWith("win-")
  $dst = if ($isWindows) { Join-Path $dstDir "$AppName.exe" } else { Join-Path $dstDir $AppName }

  Copy-Item -Force $Src $dst
  Write-Host "  -> $dst" -ForegroundColor Green
  return $dst
}

function New-ZipFile {
  param([string]$FilePath, [string]$ZipPath)
  $dir     = Split-Path -Parent $FilePath
  $name    = Split-Path -Leaf   $FilePath
  $zipAbs  = [System.IO.Path]::GetFullPath($ZipPath)

  if ($SevenZip) {
    if (Test-Path $zipAbs) { Remove-Item $zipAbs -Force }
    Push-Location $dir
    try {
      # 7z: add a single file named $name into $zipAbs (absolute path avoids nested publish/)
      & $SevenZip @('a','-tzip','-mx=9','--', $zipAbs, $name) | Out-Null
      if ($LASTEXITCODE -ne 0) { throw "7-Zip failed creating zip '$zipAbs' (exit $LASTEXITCODE)" }
    } finally { Pop-Location }
  } else {
    if (Test-Path $zipAbs) { Remove-Item $zipAbs -Force }
    Push-Location $dir
    try {
      Compress-Archive -Path $name -DestinationPath $zipAbs -Force
    } finally { Pop-Location }
  }
}

function New-TarGzFile {
  param([string]$FilePath, [string]$TgzPath)
  $dir     = Split-Path -Parent $FilePath
  $name    = Split-Path -Leaf   $FilePath
  $tgzAbs  = [System.IO.Path]::GetFullPath($TgzPath)

  if ($SevenZip) {
    # tmpTar = same base as .tar.gz but with .tar (strip .gz, then .tar if present)
    $baseNoGz = [System.IO.Path]::GetFileNameWithoutExtension($tgzAbs)        # drop .gz
    $baseNoTar= [System.IO.Path]::GetFileNameWithoutExtension($baseNoGz)      # drop .tar (if it existed)
    $tmpTar   = Join-Path (Split-Path -Parent $tgzAbs) ($baseNoTar + '.tar')

    if (Test-Path $tmpTar) { Remove-Item $tmpTar -Force }
    Push-Location $dir
    try {
      & $SevenZip @('a','-ttar','--', $tmpTar, $name) | Out-Null
      if ($LASTEXITCODE -ne 0) { throw "7-Zip failed creating tar '$tmpTar' (exit $LASTEXITCODE)" }
    } finally { Pop-Location }

    if (Test-Path $tgzAbs) { Remove-Item $tgzAbs -Force }
    & $SevenZip @('a','-tgzip','--', $tgzAbs, $tmpTar) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip failed gzip'ing '$tmpTar' -> '$tgzAbs' (exit $LASTEXITCODE)" }

    Remove-Item $tmpTar -Force
  }
  elseif ($HaveTar) {
    if (Test-Path $tgzAbs) { Remove-Item $tgzAbs -Force }
    # put *only* the file at archive root
    & tar -czf "$tgzAbs" -C "$dir" "$name"
    if ($LASTEXITCODE -ne 0) { throw "tar failed creating '$tgzAbs' (exit $LASTEXITCODE)" }
  }
  else {
    throw "Neither 7-Zip nor tar available to create tar.gz"
  }
}


function Archive-RidArtifact {
  param(
    [Parameter(Mandatory=$true)][string]$Rid,
    [Parameter(Mandatory=$true)][string]$ArtifactPath,
    [Parameter(Mandatory=$true)][string]$Version
  )

  $baseName = "$AppName-$Version-$Rid"
  if ($Rid.StartsWith("win-")) {
    $zip = Join-Path $OutputRoot "$baseName.zip"
    New-ZipFile -FilePath $ArtifactPath -ZipPath $zip
    Write-Host "  + zip: $zip" -ForegroundColor Yellow
  } else {
    $tgz = Join-Path $OutputRoot "$baseName.tar.gz"
    New-TarGzFile -FilePath $ArtifactPath -TgzPath $tgz
    Write-Host "  + tgz: $tgz" -ForegroundColor Yellow
  }
}

# ------------- main ----------------

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

if ($SevenZip) {
  Write-Host "Using 7-Zip at: $SevenZip" -ForegroundColor DarkGray
} elseif ($HaveTar) {
  Write-Host "7-Zip not found; using tar for .tar.gz and Compress-Archive for .zip" -ForegroundColor DarkGray
} else {
  Write-Host "No 7-Zip and no tar; will still zip via Compress-Archive for Windows RIDs." -ForegroundColor DarkGray
}

foreach ($rid in $RIDs) {
  Invoke-DotnetPublish -Rid $rid
  $artifact = Get-ArtifactPath -Rid $rid
  $copied   = Copy-Artifact -Rid $rid -Src $artifact   # also shows path in publish/<rid>/
  if ($copied) {
    Archive-RidArtifact -Rid $rid -ArtifactPath $copied -Version $Version
  } else {
    Write-Warning "Did not archive RID '$rid' because copy was skipped."
  }
}

Write-Host "`nAll done. Artifacts and archives are under '$OutputRoot'." -ForegroundColor Green
Write-Host "Note: Linux/macOS users may need: chmod +x $AppName"
