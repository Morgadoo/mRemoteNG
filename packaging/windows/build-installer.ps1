# ─────────────────────────────────────────────────────────────────────────────
# mRemoteNG — Windows MSI/MSIX installer build script (PowerShell)
#
# Usage:
#   pwsh -File packaging/windows/build-installer.ps1 [-Arch x64|arm64] [-Version 1.78.2]
#
# Requirements:
#   • .NET SDK 10.0+
#   • WiX Toolset 5.x (dotnet tool install --global wix)
#     OR
#   • MSIX Packaging Tool / Windows SDK for MSIX build
# ─────────────────────────────────────────────────────────────────────────────
param(
    [string]$Arch = "x64",
    [string]$Version = "1.78.2-dev",
    [switch]$Msix
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$OutDir  = Join-Path $RootDir "dist\windows"
$PubDir  = Join-Path $OutDir "publish-$Arch"

Write-Host "==> Building mRemoteNG Windows installer ($Arch, v$Version)" -ForegroundColor Cyan

# 1. Publish
Write-Host "==> Publishing .NET app (win-$Arch, self-contained)…"
dotnet publish `
    "$RootDir\mRemoteNG.Avalonia\mRemoteNG.Avalonia.csproj" `
    -c Release `
    -r "win-$Arch" `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -o "$PubDir" `
    "-p:Version=$Version"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if ($Msix) {
    # ── MSIX package ─────────────────────────────────────────────────────
    Write-Host "==> Building MSIX package…"

    $MsixManifestDir = Join-Path $RootDir "packaging\windows\msix"
    New-Item -ItemType Directory -Force -Path $MsixManifestDir | Out-Null

    # AppxManifest.xml
    @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10">
  <Identity Name="mRemoteNG.mRemoteNG"
            Publisher="CN=mRemoteNG"
            Version="$($Version.Split('-')[0]).0"
            ProcessorArchitecture="$Arch"/>
  <Properties>
    <DisplayName>mRemoteNG</DisplayName>
    <PublisherDisplayName>mRemoteNG</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop"
                        MinVersion="10.0.19041.0"
                        MaxVersionTested="10.0.26100.0"/>
  </Dependencies>
  <Resources>
    <Resource Language="en-us"/>
  </Resources>
  <Applications>
    <Application Id="App"
                 Executable="mRemoteNG.Avalonia.exe"
                 EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="mRemoteNG"
                          Description="Multi-protocol remote connection manager"
                          BackgroundColor="transparent"
                          Square150x150Logo="Assets\Square150x150Logo.png"
                          Square44x44Logo="Assets\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png"/>
      </uap:VisualElements>
    </Application>
  </Applications>
  <Capabilities>
    <Capability Name="internetClient"/>
    <Capability Name="privateNetworkClientServer"/>
    <desktop:Capability Name="runFullTrust"/>
  </Capabilities>
</Package>
"@ | Set-Content (Join-Path $MsixManifestDir "AppxManifest.xml")

    $MsixOutput = Join-Path $OutDir "mRemoteNG-$Version-win-$Arch.msix"
    # Copy published files into MSIX staging
    Copy-Item -Recurse -Force "$PubDir\*" $MsixManifestDir
    # Use MakeAppx (from Windows SDK)
    $MakeAppx = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\MakeAppx.exe"
    if (Test-Path $MakeAppx) {
        & $MakeAppx pack /d $MsixManifestDir /p $MsixOutput /nv
        Write-Host "==> MSIX: $MsixOutput"
    } else {
        Write-Warning "MakeAppx.exe not found. Install Windows SDK."
    }
} else {
    # ── WiX MSI installer ────────────────────────────────────────────────
    Write-Host "==> Building MSI with WiX…"

    # Check WiX is installed
    if (-not (Get-Command "wix" -ErrorAction SilentlyContinue)) {
        Write-Host "Installing WiX toolset…"
        dotnet tool install --global wix
    }

    $WxsFile = Join-Path $RootDir "packaging\windows\mRemoteNG.wxs"
    @"
<?xml version="1.0" encoding="UTF-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"
     xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui">

  <Package Name="mRemoteNG"
           Version="$($Version.Split('-')[0]).0"
           Manufacturer="mRemoteNG"
           UpgradeCode="7B9E2A4F-1234-4321-ABCD-0123456789AB"
           Compressed="yes">

    <MajorUpgrade DowngradeErrorMessage="A newer version of mRemoteNG is already installed." />
    <MediaTemplate EmbedCab="yes"/>

    <Feature Id="ProductFeature" Title="mRemoteNG" Level="1">
      <ComponentGroupRef Id="ProductComponents"/>
    </Feature>

    <ui:WixUI Id="WixUI_InstallDir" InstallDirectory="INSTALLFOLDER"/>

    <Property Id="WIXUI_INSTALLDIR" Value="INSTALLFOLDER"/>

    <StandardDirectory Id="ProgramFiles6432Folder">
      <Directory Id="INSTALLFOLDER" Name="mRemoteNG"/>
    </StandardDirectory>

    <ComponentGroup Id="ProductComponents" Directory="INSTALLFOLDER">
      <!-- Files will be harvested by wix harvest or added manually -->
    </ComponentGroup>

  </Package>
</Wix>
"@ | Set-Content $WxsFile

    $MsiOutput = Join-Path $OutDir "mRemoteNG-$Version-win-$Arch.msi"

    wix build $WxsFile `
        -ext WixToolset.UI.wixext `
        -d "SourceDir=$PubDir" `
        -o $MsiOutput

    Write-Host "==> MSI built: $MsiOutput"
    Get-Item $MsiOutput | Select-Object FullName, Length
}
