# Packs the server as a Claude Desktop extension (.mcpb) for one runtime identifier.
# Native AOT can't cross-compile between operating systems, so run it on the target system.
# Usage, from the repository root: pwsh packaging/mcpb/pack.ps1 [-Rid win-x64] [-PublishDir DIR] [-OutDir DIR]
# Without -PublishDir, the script publishes the server first.
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string] $Rid = 'win-x64',
    [string] $PublishDir,
    [string] $OutDir
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot/../.."
$out = if ($OutDir) { New-Item -ItemType Directory -Force $OutDir | Select-Object -ExpandProperty FullName } else { Join-Path $root 'packaging/mcpb/out' }
$staging = Join-Path $out "staging-$Rid"
$server = Join-Path $staging 'server'

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
if ($PublishDir) {
    New-Item -ItemType Directory -Force $server | Out-Null
    Copy-Item (Join-Path $PublishDir '*') $server -Recurse
}
else {
    if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        # Visual Studio's vcvarsall.bat calls vswhere.exe without a path, so the Native AOT link step
        # needs the Visual Studio Installer folder on the PATH.
        $env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
    }

    dotnet publish (Join-Path $root 'src/LocalWhisperMcp') -c Release -r $Rid -o $server
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
}

Remove-Item (Join-Path $server '*.pdb'), (Join-Path $server '*.dbg') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $server '*.dSYM') -Recurse -ErrorAction SilentlyContinue

# The manifest in the repository describes win-x64; other systems get their executable name and platform.
$manifest = Get-Content (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json
$executable = if ($Rid.StartsWith('win-')) { 'local-whisper-mcp.exe' } else { 'local-whisper-mcp' }
$platform = switch -Wildcard ($Rid) { 'win-*' { 'win32' } 'osx-*' { 'darwin' } default { 'linux' } }
$manifest.server.entry_point = "server/$executable"
$manifest.server.mcp_config.command = "`${__dirname}/server/$executable"
$manifest.compatibility.platforms = @($platform)
$manifest | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $staging 'manifest.json') -Encoding utf8NoBOM

$bundle = Join-Path $out "local-whisper-mcp-$($manifest.version)-$Rid.mcpb"
Remove-Item $bundle, "$bundle.zip" -ErrorAction SilentlyContinue
if ($platform -eq 'win32') {
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath "$bundle.zip"
    Move-Item "$bundle.zip" $bundle
}
else {
    # zip keeps the executable bit, which Compress-Archive drops.
    Push-Location $staging
    try { zip -qr $bundle . } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'zip failed.' }
}

Remove-Item $staging -Recurse -Force
Get-Item $bundle | Select-Object FullName, Length
