<#
.SYNOPSIS
  Builds AddinLoader and installs it for one or more Revit versions.

.EXAMPLE
  .\install.ps1                     # Revit 2024
  .\install.ps1 -RevitVersion 2023,2025
  .\install.ps1 -RevitVersion 2025 -Uninstall
#>
param(
    [int[]] $RevitVersion = @(2024),
    [string] $Configuration = "Release",
    [switch] $Uninstall
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\AddinLoader\AddinLoader.csproj"

foreach ($version in $RevitVersion) {
    $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$version"
    $manifest = Join-Path $addins "AddinLoader.addin"
    $target = Join-Path $addins "AddinLoader"

    if ($Uninstall) {
        Remove-Item $manifest -ErrorAction SilentlyContinue
        Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "AddinLoader removed from Revit $version."
        continue
    }

    Write-Host "Building AddinLoader for Revit $version..."
    dotnet build $project -c $Configuration -p:RevitVersion=$version
    if ($LASTEXITCODE -ne 0) { throw "Build failed for Revit $version." }

    $output = Join-Path $PSScriptRoot "src\AddinLoader\bin\$Configuration\$version"
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item (Join-Path $output "*") $target -Recurse -Force -Exclude "AddinLoader.addin"
    Copy-Item (Join-Path $output "AddinLoader.addin") $manifest -Force
    Write-Host "AddinLoader installed for Revit $version -> $addins"
}
