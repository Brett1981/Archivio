$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
& dotnet run --project src/Archivio.App/Archivio.App.csproj
if ($LASTEXITCODE -ne 0) { throw "Archivio exited with code $LASTEXITCODE." }
