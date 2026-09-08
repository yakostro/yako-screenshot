# Tags the commit, pushes the tag, and creates a GitHub release carrying the exe.
# If the version in Yako.Screenshot.csproj was already released, auto-bumps the patch
# version and rebuilds before publishing - so this always publishes whatever is on HEAD.
param(
    [string]$NotesFile
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $dirty = git status --porcelain
    if ($dirty) {
        throw "Working tree has uncommitted changes - commit them first:`n$dirty"
    }

    function Get-CsprojVersion {
        [xml]$proj = Get-Content "Yako.Screenshot.csproj"
        $v = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
        if (-not $v) {
            throw "Yako.Screenshot.csproj has no <Version> set."
        }
        return $v
    }

    $version = Get-CsprojVersion
    $tag = "v$version"

    if (git tag --list $tag) {
        Write-Host "$tag already released - bumping patch version ..."
        do {
            $parts = $version -split '\.'
            $parts[2] = [int]$parts[2] + 1
            $version = $parts -join '.'
            $tag = "v$version"
        } while (git tag --list $tag)

        (Get-Content "Yako.Screenshot.csproj") -replace '<Version>[^<]*</Version>', "<Version>$version</Version>" |
            Set-Content "Yako.Screenshot.csproj"
        git add "Yako.Screenshot.csproj"
        git commit -m "Bump version to $version"
        git push origin HEAD

        Write-Host "Rebuilding for $version ..."
        & (Join-Path $PSScriptRoot "build-release.ps1")
    }

    $exeName = "yako-screenshot-$version-win-x64.exe"
    $exePath = "publish\$exeName"

    if (-not (Test-Path $exePath)) {
        throw "$exePath not found - run scripts\build-release.ps1 first."
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
