param(
    [switch]$WhatIf
)

$root = Get-Location

Write-Host "Searching under:"
Write-Host $root
Write-Host ""

# Find bin and obj folders
$folders = Get-ChildItem -Path $root -Directory -Recurse -Force |
    Where-Object {
        $_.Name -in @("bin", "obj") -and
        $_.FullName -notmatch "\\\.git\\"
    } |
    Sort-Object FullName -Descending

# Find zip files
$zipFiles = Get-ChildItem -Path $root -File -Recurse -Force -Filter "*.zip" |
    Where-Object {
        $_.FullName -notmatch "\\\.git\\"
    } |
    Sort-Object FullName -Descending

if (-not $folders -and -not $zipFiles) {
    Write-Host "No bin folders, obj folders, or zip files found."
    exit 0
}

foreach ($folder in $folders) {
    if ($WhatIf) {
        Write-Host "[WhatIf] Would remove folder: $($folder.FullName)"
    }
    else {
        Write-Host "Removing folder: $($folder.FullName)"
        Remove-Item -LiteralPath $folder.FullName -Recurse -Force
    }
}

foreach ($zip in $zipFiles) {
    if ($WhatIf) {
        Write-Host "[WhatIf] Would remove zip file: $($zip.FullName)"
    }
    else {
        Write-Host "Removing zip file: $($zip.FullName)"
        Remove-Item -LiteralPath $zip.FullName -Force
    }
}

Write-Host ""
Write-Host "Done."