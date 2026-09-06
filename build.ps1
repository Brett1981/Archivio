$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Write-Host 'Restoring Metaroq...'
Invoke-DotNet @('restore', 'Archivio.sln', '--configfile', 'NuGet.Config')

Write-Host 'Checking formatting...'
Invoke-DotNet @('format', 'Archivio.sln', '--verify-no-changes', '--no-restore', '--verbosity', 'minimal')

Write-Host 'Building Metaroq...'
Invoke-DotNet @('build', 'Archivio.sln', '--configuration', 'Release', '--no-restore')

Write-Host 'Running all tests...'
Invoke-DotNet @('test', 'Archivio.sln', '--configuration', 'Release', '--no-build')

Write-Host 'Metaroq validation completed successfully.' -ForegroundColor Green
