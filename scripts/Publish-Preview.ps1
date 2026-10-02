param(
    [string]$Version = '1.0.7-preview.20260927',
    [string]$FeedUrl,
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')]
    [string]$Channel,
    [ValidateSet('static', 'github')]
    [string]$Source,
    [bool]$IncludePrereleases,
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$CertificateThumbprint,
    [string]$SignTemplate,
    [string]$AzureTrustedSignFile,
    [switch]$RequireSignature
)

$ErrorActionPreference = 'Stop'
$signingMethods = @($CertificateThumbprint, $SignTemplate, $AzureTrustedSignFile) | Where-Object { $_ }
if (@($signingMethods).Count -gt 1) { throw 'Select one signing method.' }
if ($RequireSignature -and @($signingMethods).Count -eq 0) { throw 'RequireSignature needs a certificate or signing service.' }
if ($AzureTrustedSignFile -and !(Test-Path -LiteralPath $AzureTrustedSignFile -PathType Leaf)) { throw 'Azure Trusted Signing metadata file is missing.' }
$repoRoot = Split-Path $PSScriptRoot -Parent
$sourceSettings = Get-Content -LiteralPath (Join-Path $repoRoot 'src/cast.Desktop/update-settings.json') -Raw | ConvertFrom-Json
if (!$PSBoundParameters.ContainsKey('FeedUrl')) { $FeedUrl = $sourceSettings.feedUrl }
if (!$PSBoundParameters.ContainsKey('Channel')) { $Channel = $sourceSettings.channel }
if (!$PSBoundParameters.ContainsKey('Source')) { $Source = if ($sourceSettings.source) { $sourceSettings.source } else { 'static' } }
if (!$PSBoundParameters.ContainsKey('IncludePrereleases')) { $IncludePrereleases = [bool]$sourceSettings.includePrereleases }
if ($Source -notin @('static', 'github')) { throw 'Source must be static or github.' }
if ($Channel -notmatch '^[a-z0-9][a-z0-9-]{0,63}$') { throw 'Invalid release channel.' }
if ($Version -notmatch '^\d+\.\d+\.\d+-preview\.\d+(\.\d+)?$') { throw 'Version must be a preview SemVer, for example 1.0.2-preview.20260917.1.' }
if ($FeedUrl) {
    $feedUri = $null
    if (![Uri]::TryCreate($FeedUrl, [UriKind]::Absolute, [ref]$feedUri) -or
        ($feedUri.Scheme -ne 'https' -and !($feedUri.Scheme -eq 'http' -and $feedUri.IsLoopback)) -or
        $feedUri.UserInfo -or $feedUri.Query -or $feedUri.Fragment) { throw 'FeedUrl must be an HTTPS directory URL (HTTP loopback is allowed for tests).' }
    if ($Source -eq 'github' -and ($feedUri.Scheme -ne 'https' -or $feedUri.Host -ne 'github.com' -or !$feedUri.IsDefaultPort -or
        $feedUri.AbsolutePath -cnotmatch '^/[A-Za-z0-9-]+/[A-Za-z0-9_.-]+/?$' -or $feedUri.AbsolutePath.TrimEnd('/').EndsWith('.git', [StringComparison]::OrdinalIgnoreCase))) {
        throw 'GitHub FeedUrl must be https://github.com/owner/repository.'
    }
}
$publishDir = Join-Path $repoRoot "artifacts/publish/$Version"
$releaseDir = Join-Path $repoRoot "artifacts/releases/$Version"
if (Test-Path $releaseDir) { throw "Release directory already exists: $releaseDir. Use a new version." }
Push-Location $repoRoot
try {
    dotnet tool restore
    if ($LASTEXITCODE) { throw 'Tool restore failed.' }
    dotnet publish src/cast.Desktop -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $publishDir
    if ($LASTEXITCODE) { throw 'Publish failed.' }
    @{ feedUrl = $FeedUrl; channel = $Channel; source = $Source; includePrereleases = $IncludePrereleases } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publishDir 'update-settings.json') -Encoding utf8
    $packArguments = @('tool', 'run', 'vpk', '--', 'pack', '--packId', 'cast', '--packTitle', 'cast', '--packAuthors', 'cast', '--packVersion', $Version,
        '--packDir', $publishDir, '--mainExe', 'cast.exe', '--runtime', 'win-x64', '--channel', $Channel, '--icon', 'src/cast.Desktop/Assets/cast.ico', '--framework', 'webview2', '--outputDir', $releaseDir)
    if ($CertificateThumbprint) { $packArguments += @('--signParams', "/sha1 $CertificateThumbprint /fd sha256 /tr https://timestamp.digicert.com /td sha256") }
    if ($SignTemplate) { $packArguments += @('--signTemplate', $SignTemplate) }
    if ($AzureTrustedSignFile) { $packArguments += @('--azureTrustedSignFile', (Resolve-Path -LiteralPath $AzureTrustedSignFile).Path) }
    & dotnet @packArguments
    if ($LASTEXITCODE) { throw 'Velopack packaging failed.' }
    if (@($signingMethods).Count -gt 0) {
        $signedFiles = @((Join-Path $publishDir 'cast.exe')) + @(Get-ChildItem -LiteralPath $releaseDir -Filter '*Setup.exe' | ForEach-Object FullName)
        foreach ($signedFile in $signedFiles) {
            $signature = Get-AuthenticodeSignature -LiteralPath $signedFile
            if ($signature.Status -ne 'Valid') { throw "Signature verification failed: $signedFile ($($signature.Status))" }
            if ($CertificateThumbprint -and $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) { throw "Unexpected signing certificate: $signedFile" }
        }
    }
    Write-Host "Release ready: $releaseDir"
} finally { Pop-Location }
