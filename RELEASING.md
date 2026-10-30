# Releasing

1. Bump `Version`, `AssemblyVersion`, and `FileVersion` in `ZoneLab.csproj`.
2. Run `.\build.ps1` from PowerShell.
3. Zip the contents of the generated `publish` directory.
4. Create a GitHub Release for the version and attach the zip.
5. Never commit `ZoneLabels.exe` or any other executable.
