# Builds the self-contained single-file exe for the version set in Yako.Screenshot.csproj
# and runs the selftest against it. Does not touch git or GitHub - see publish-release.ps1.
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    [xml]$proj = Get-Content "Yako.Screenshot.csproj"
    $version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $version) {
        throw "Yako.Screenshot.csproj has no <Version> set."
    }
    $exeName = "yako-screenshot-$version-win-x64.exe"

    Write-Host "Building $exeName ..."
    if (Test-Path "publish") {
        Remove-Item -Recurse -Force "publish"
    }
    dotnet publish Yako.Screenshot.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed."
    }

    Write-Host "Running selftest ..."
    & "publish\Yako.Screenshot.exe" --selftest
    if ($LASTEXITCODE -ne 0) {
        throw "Selftest failed - see $env:TEMP\yako-selftest.txt"
    }

    Rename-Item "publish\Yako.Screenshot.exe" $exeName -Force
    Write-Host "Built publish\$exeName - ready for scripts\publish-release.ps1"
}
finally {
    Pop-Location
}
