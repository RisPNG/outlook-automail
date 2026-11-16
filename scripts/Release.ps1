param(
    [string]$Version,
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$release = Get-Content -LiteralPath 'release.json' -Raw | ConvertFrom-Json
$repositoryUri = [Uri]$release.repository
if ($repositoryUri.Scheme -ne 'https' -or $repositoryUri.Host -ne 'github.com' -or $repositoryUri.Segments.Count -ne 3) {
    throw 'release.json must name the public GitHub releases repository.'
}
$repository = $repositoryUri.AbsolutePath.Trim('/')
[xml]$project = Get-Content -LiteralPath 'src/OutlookAutomail/OutlookAutomail.csproj' -Raw
$baseVersion = [Version]($project.Project.PropertyGroup.Version | Select-Object -First 1)
if (-not $Version) {
    $Version = if ($env:GITHUB_RUN_NUMBER) { "$($baseVersion.Major).$($baseVersion.Minor).$env:GITHUB_RUN_NUMBER" } else { $baseVersion.ToString(3) }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Use a stable three-part version, for example 1.3.0.'
}
$tag = "v$Version"
$releaseDirectory = Join-Path $projectRoot "artifacts/releases/$Version"
if ($Publish) {
    if (-not $env:GH_TOKEN) { throw 'Publishing requires GH_TOKEN with Contents write permission on this repository.' }
    $existing = Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases/tags/$tag" -Headers @{ Accept = 'application/vnd.github+json'; Authorization = "Bearer $env:GH_TOKEN" } -SkipHttpErrorCheck -StatusCodeVariable existingStatus
    if ($existingStatus -notin @(200, 404)) { throw "Release lookup returned HTTP $existingStatus." }
    if ($existingStatus -eq 404) { $existing = $null }
    if ($existing -and -not $existing.draft) {
        if (-not ($existing.assets.name -contains 'releases.win.json') -or -not ($existing.assets.name -contains 'OutlookAutomail-win-Setup.exe') -or -not ($existing.assets.name -contains "OutlookAutomail-$Version-full.nupkg")) {
            throw 'The published release is incomplete. Review it rather than replacing a published version.'
        }
        Write-Output "$tag is already published. No release was changed."
        return
    }
}
$publicReleases = Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases?per_page=100" -Headers @{ Accept = 'application/vnd.github+json' } -SkipHttpErrorCheck -StatusCodeVariable releaseStatus
if ($releaseStatus -notin @(200, 404)) { throw "The public release feed returned HTTP $releaseStatus. Packaging stopped." }
$latestVersion = $publicReleases | Where-Object { -not $_.draft -and -not $_.prerelease -and $_.tag_name -match '^v(\d+\.\d+\.\d+)$' } | ForEach-Object { [Version]$_.tag_name.TrimStart('v') } | Sort-Object -Descending | Select-Object -First 1
if ($latestVersion -and $latestVersion -ge [Version]$Version) {
    if ($Publish) {
        Write-Output "$tag is superseded by $latestVersion. No older release was packaged or published."
        return
    }
    throw "Choose a version newer than the published $latestVersion."
}
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$previousVersion = $env:ReleaseVersion
try {
    $env:ReleaseVersion = $Version
    & mise exec -- dotnet tool restore
    & mise run publish
    if ($releaseStatus -eq 200 -and $publicReleases.Count -gt 0) {
        & mise exec -- dotnet vpk download github --repoUrl $release.repository --outputDir $releaseDirectory --channel win --skip-updates --yes
    }
    & mise exec -- dotnet vpk '[win]' pack --packId OutlookAutomail --packVersion $Version --packDir artifacts/win-x64 --mainExe OutlookAutomail.exe --packTitle 'Outlook Automail' --icon src/OutlookAutomail/Assets/AppIcon.ico --splashImage src/OutlookAutomail/Assets/AppIcon.png --framework webview2 --runtime win-x64 --channel win --outputDir $releaseDirectory --skip-updates --yes
}
finally {
    $env:ReleaseVersion = $previousVersion
}
$assets = @(Get-ChildItem -LiteralPath $releaseDirectory -File)
if (-not ($assets.Name -contains 'releases.win.json') -or -not ($assets.Name -contains 'OutlookAutomail-win-Setup.exe')) {
    throw 'Velopack did not produce the expected Windows installer and update feed.'
}
$feed = Get-Content -LiteralPath (Join-Path $releaseDirectory 'releases.win.json') -Raw | ConvertFrom-Json
foreach ($asset in $feed.Assets) {
    if ($asset.FileName -ne [IO.Path]::GetFileName($asset.FileName)) { throw 'The update feed contains an unexpected package path.' }
    $package = Join-Path $releaseDirectory $asset.FileName
    if (-not (Test-Path -LiteralPath $package) -or (Get-Item -LiteralPath $package).Length -ne $asset.Size -or (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $asset.SHA256) {
        throw "The update feed references a missing or mismatched package: $($asset.FileName)"
    }
}
Write-Output "Packaged $Version in $releaseDirectory"
if (-not $Publish) { return }
$notesPath = Join-Path $projectRoot 'artifacts/release-notes.md'
$sourceCommit = if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { (& mise exec -- git rev-parse HEAD).Trim() }
$source = if ($env:GITHUB_SHA) { "Built from source commit $sourceCommit after the Windows build, formatting, and test checks passed." } else { "Built and published manually from source commit $sourceCommit." }
Set-Content -LiteralPath $notesPath -Value "Outlook Automail $Version`n`nWindows x64 installer with the .NET runtime included. Classic Outlook is required.`n`n$source" -Encoding utf8
if (-not $existing) {
    & mise exec -- gh release create $tag --repo $repository --target $sourceCommit --draft --title "Outlook Automail $Version" --notes-file $notesPath
}
& mise exec -- gh release upload $tag --repo $repository --clobber @($assets.FullName)
& mise exec -- gh release edit $tag --repo $repository --draft=false --latest
Write-Output "Published $($release.repository)/releases/tag/$tag"
