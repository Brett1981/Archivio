$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Write-Host 'Restoring Archivio...'
Invoke-DotNet @('restore', 'Archivio.sln', '--configfile', 'NuGet.Config')

Write-Host 'Building Archivio...'
Invoke-DotNet @('build', 'Archivio.sln', '--configuration', 'Release', '--no-restore')

Write-Host 'Running unit tests...'
Invoke-DotNet @('test', 'tests/Archivio.UnitTests/Archivio.UnitTests.csproj', '--configuration', 'Release', '--no-build')

Write-Host 'Running integration tests...'
Invoke-DotNet @('test', 'tests/Archivio.IntegrationTests/Archivio.IntegrationTests.csproj', '--configuration', 'Release', '--no-build')

Write-Host 'Archivio Milestone 1 build completed successfully.' -ForegroundColor Green
