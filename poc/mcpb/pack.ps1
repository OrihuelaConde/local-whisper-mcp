# Packs the proof-of-concept MCP server as a Claude Desktop extension (.mcpb) for win-x64.
# Run from the repository root: pwsh poc/mcpb/pack.ps1
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot/../.."
$staging = Join-Path $root 'poc/mcpb/out/staging'
$bundle = Join-Path $root 'poc/mcpb/out/local-whisper-mcp-poc.mcpb'

# Visual Studio's vcvarsall.bat calls vswhere.exe without a path, so the Native AOT link step
# needs the Visual Studio Installer folder on the PATH.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'poc/AotMcp') -c Release -r win-x64 -o (Join-Path $staging 'server')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# The Whisper.net packages copy native binaries for every platform; keep only win-x64.
$server = Join-Path $staging 'server'
Get-ChildItem (Join-Path $server 'runtimes') -Directory |
    Where-Object { $_.Name -notin 'win-x64', 'vulkan' } |
    Remove-Item -Recurse -Force
Get-ChildItem (Join-Path $server 'runtimes/vulkan') -Directory |
    Where-Object { $_.Name -ne 'win-x64' } |
    Remove-Item -Recurse -Force
Remove-Item (Join-Path $server '*.pdb'), (Join-Path $server 'ggml-metal.metal') -ErrorAction SilentlyContinue

Copy-Item (Join-Path $PSScriptRoot 'manifest.json') $staging
Remove-Item $bundle -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath "$bundle.zip"
Move-Item "$bundle.zip" $bundle
Get-Item $bundle | Select-Object FullName, Length
