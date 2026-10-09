# Packs the Local Whisper skill as a .zip to upload to claude.ai (Settings > Capabilities > Skills).
# Usage, from the repository root: pwsh packaging/skill/pack.ps1
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot/../.."
$out = Join-Path $root 'packaging/skill/out'
$bundle = Join-Path $out 'local-whisper.zip'

New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item $bundle -ErrorAction SilentlyContinue

# claude.ai expects the skill folder, with SKILL.md inside it, at the root of the archive.
Compress-Archive -Path (Join-Path $root 'skills/local-whisper') -DestinationPath $bundle
Get-Item $bundle | Select-Object FullName, Length
