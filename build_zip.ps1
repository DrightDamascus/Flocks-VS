param([string]$GamePath = (Join-Path $env:USERPROFILE 'agent-tools\vsref1227'))
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Flocks/Flocks.csproj'
& dotnet build $project -c Release "-p:VINTAGE_STORY=$GamePath" -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Build failed; no release packaged.' }
$info = Get-Content -Raw (Join-Path $PSScriptRoot 'Flocks/modinfo.json') | ConvertFrom-Json
$out = Join-Path $PSScriptRoot 'Flocks/bin/Release'
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$archive = Join-Path $dist "flocks_$($info.version).zip"
Compress-Archive -LiteralPath (@('modinfo.json','Flocks.dll','Flocks.pdb','assets') | % { Join-Path $out $_ }) -DestinationPath $archive -Force
Get-Item $archive
