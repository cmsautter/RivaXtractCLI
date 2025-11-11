# Run rivaxtract on all .ALF files in the current directory.
# For each FILENAME.ALF:
#   - export JSON to FILENAME.JSON (pretty-printed)
#   - export views to FILENAME_ALF_SOFT (using symlink mode)
#
# Params:
#   -Force  Overwrite existing non-empty export directories without prompting

param(
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Ensure rivaxtract.exe is available in the current directory
$exe = ".\rivaxtract.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    Write-Error "rivaxtract.exe not found in the current directory."
    exit 1
}

# Collect all .ALF files (case-insensitive)
$alfFiles = Get-ChildItem -Path . -File -Filter *.alf

if (-not $alfFiles) {
    Write-Host "No .ALF files found in the current directory."
    exit 0
}

foreach ($alf in $alfFiles) {
    try {
        $baseName = [System.IO.Path]::GetFileNameWithoutExtension($alf.Name)
        $jsonOut  = "$baseName.JSON"
        $viewsOut = "${baseName}_ALF_SOFT"

        Write-Host "Processing '$($alf.Name)'..." -ForegroundColor Cyan

        # Export JSON
        & $exe export-json $alf.FullName --out $jsonOut --pretty

        # Export Views (symlink mode) with optional overwrite policy
        $overwrite = @()
        if ($Force) { $overwrite = @('--overwrite','always') }
        & $exe export-views $alf.FullName --out $viewsOut --mode symlink @overwrite

        Write-Host "Done: $($alf.Name) -> $jsonOut, $viewsOut" -ForegroundColor Green
    }
    catch {
        Write-Warning "Failed on '$($alf.Name)': $($_.Exception.Message)"
        continue
    }
}

Write-Host "All done."
