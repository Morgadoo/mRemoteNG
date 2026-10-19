# Generates mRemoteNG.Platform.Windows/Interop/MSTSCLib.dll from the Remote Desktop ActiveX control
# (mstscax.dll). The dotnet SDK cannot import COM references itself (MSB4803), so the interop assembly is
# produced with the .NET Framework tlbimp and committed-or-rebuilt by hand.
$ErrorActionPreference = "Stop"
$tlbimp = Get-ChildItem "C:\Program Files*\Microsoft SDKs" -Filter "TlbImp.exe" -Recurse |
    Where-Object { $_.FullName -match "NETFX 4.8 Tools\\x64" } |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $tlbimp) { throw "tlbimp.exe (NETFX 4.8 Tools, x64) was not found." }

$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "mRemoteNG.Platform.Windows\Interop\MSTSCLib.dll"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
& $tlbimp "$env:SystemRoot\System32\mstscax.dll" "/out:$out" "/namespace:MSTSCLib" "/silent" "/sysarray"
if ($LASTEXITCODE -ne 0) { throw "tlbimp failed with exit code $LASTEXITCODE" }
Write-Output "Wrote $out"
