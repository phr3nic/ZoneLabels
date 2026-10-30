$ErrorActionPreference = 'Stop'

dotnet build .\ZoneLab.csproj -c Release
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

dotnet publish .\ZoneLab.csproj -c Release -r win-x64 --self-contained true -o .\publish
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}
