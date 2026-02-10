# Test script to manually read a flat file's metadata
param(
    [Parameter(Mandatory=$true)]
    [string]$FlatDirectory
)

# Find first FITS or XISF file
$testFile = Get-ChildItem -Path $FlatDirectory -Recurse -Include *.fits,*.fit,*.xisf | Select-Object -First 1

if ($testFile) {
    Write-Host "Testing file: $($testFile.FullName)"
    Write-Host "Size: $($testFile.Length) bytes"
    
    # Check if FITS
    if ($testFile.Extension -match '\.fits?$') {
        Write-Host "`nFITS Header (first 2880 bytes):"
        $bytes = [System.IO.File]::ReadAllBytes($testFile.FullName)
        $headerBytes = $bytes[0..([Math]::Min(2879, $bytes.Length-1))]
        $header = [System.Text.Encoding]::ASCII.GetString($headerBytes)
        
        # Parse key FITS keywords
        $header -split "`n" | ForEach-Object {
            if ($_ -match '^(IMAGETYP|EXPTIME|EXPOSURE|FRAMETYPE|FILTER|BINNING|XBINNING)\s*=') {
                Write-Host $_
            }
        }
    }
    elseif ($testFile.Extension -eq '.xisf') {
        Write-Host "`nXISF Header (first 50KB):"
        $content = Get-Content $testFile.FullName -Raw -Encoding UTF8 | Select-Object -First 50000
        if ($content -match '<FITSKeyword.*?name="(IMAGETYP|EXPTIME|EXPOSURE)".*?value="([^"]+)"') {
            Write-Host "  $($matches[1]) = $($matches[2])"
        }
    }
} else {
    Write-Host "No FITS or XISF files found in $FlatDirectory"
}
