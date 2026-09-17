param(
    [string]$Version = '1.0.3-preview.20260917',
    [string]$FeedUrl,
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')]
    [string]$Channel
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$sourceSettings = Get-Content -LiteralPath (Join-Path $repoRoot 'src/serial.Desktop/update-settings.json') -Raw | ConvertFrom-Json
if (!$PSBoundParameters.ContainsKey('FeedUrl')) { $FeedUrl = $sourceSettings.feedUrl }
if (!$PSBoundParameters.ContainsKey('Channel')) { $Channel = $sourceSettings.channel }
if ($Channel -notmatch '^[a-z0-9][a-z0-9-]{0,63}$') { throw 'Invalid release channel.' }
if ($Version -notmatch '^\d+\.\d+\.\d+-preview\.\d+(\.\d+)?$') { throw 'Version must be a preview SemVer, for example 1.0.2-preview.20260917.1.' }
if ($FeedUrl) {
    $feedUri = $null
    if (![Uri]::TryCreate($FeedUrl, [UriKind]::Absolute, [ref]$feedUri) -or
        ($feedUri.Scheme -ne 'https' -and !($feedUri.Scheme -eq 'http' -and $feedUri.IsLoopback)) -or
        $feedUri.UserInfo -or $feedUri.Query -or $feedUri.Fragment) { throw 'FeedUrl must be an HTTPS directory URL (HTTP loopback is allowed for tests).' }
}
$publishDir = Join-Path $repoRoot "artifacts/publish/$Version"
$releaseDir = Join-Path $repoRoot "artifacts/releases/$Version"
if (Test-Path $releaseDir) { throw "Release directory already exists: $releaseDir. Use a new version." }
Push-Location $repoRoot
try {
    dotnet tool restore
    if ($LASTEXITCODE) { throw 'Tool restore failed.' }
    dotnet publish src/serial.Desktop -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $publishDir
    if ($LASTEXITCODE) { throw 'Publish failed.' }
    @{ feedUrl = $FeedUrl; channel = $Channel } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publishDir 'update-settings.json') -Encoding utf8
    dotnet tool run vpk -- pack --packId serial --packTitle serial --packAuthors serial --packVersion $Version --packDir $publishDir --mainExe serial.exe --runtime win-x64 --channel $Channel --icon src/serial.Desktop/Assets/serial.ico --framework webview2 --outputDir $releaseDir
    if ($LASTEXITCODE) { throw 'Velopack packaging failed.' }
    Write-Host "Release ready: $releaseDir"
} finally { Pop-Location }
