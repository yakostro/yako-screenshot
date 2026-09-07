# Tags the commit, pushes the tag, and creates a GitHub release carrying the exe that
# build-release.ps1 already produced. Run build-release.ps1 first.
param(
    [string]$NotesFile
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    [xml]$proj = Get-Content "Yako.Screenshot.csproj"
    $version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $version) {
        throw "Yako.Screenshot.csproj has no <Version> set."
    }
    $tag = "v$version"
    $exeName = "yako-screenshot-$version-win-x64.exe"
    $exePath = "publish\$exeName"

    if (-not (Test-Path $exePath)) {
        throw "$exePath not found - run scripts\build-release.ps1 first."
    }

    $dirty = git status --porcelain
    if ($dirty) {
        throw "Working tree has uncommitted changes - commit them first:`n$dirty"
    }

    if (git tag --list $tag) {
        throw "Tag $tag already exists. Bump <Version> in Yako.Screenshot.csproj for a new release."
    }

    Write-Host "Tagging $tag ..."
    git tag $tag
    git push origin $tag

    Write-Host "Creating GitHub release $tag ..."
    if ($NotesFile) {
        gh release create $tag $exePath --title $tag --notes-file $NotesFile
    }
    else {
        gh release create $tag $exePath --title $tag --generate-notes
        Write-Host "Used auto-generated notes - consider polishing them: gh release edit $tag"
    }

    Write-Host "Published: https://github.com/yakostro/yako-screenshot/releases/tag/$tag"
}
finally {
    Pop-Location
}
