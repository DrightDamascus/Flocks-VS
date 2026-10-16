param(
    [string]$GamePath = 'C:\Steam\steamapps\common\Vintagestory',
    [string]$DataPath = (Join-Path $env:TEMP 'flocks-enginetest'),
    [int]$TimeoutMinutes = 20
)
# In-engine tests: a scratch dedicated server (new world, no players) with the built Flocks zip and the FlocksTest mod.
# FlocksTest builds a test site, runs the goose AI through settle / flush / depart / landing / cycles, logs PASS/FAIL
# lines and shuts the server down. Prints those lines plus any warnings and errors from the server log.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $root 'build_zip.ps1') | Out-Null
& dotnet build (Join-Path $PSScriptRoot 'FlocksTest/FlocksTest.csproj') -c Release -warnaserror | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'FlocksTest build failed.' }

if (Test-Path $DataPath) { Remove-Item -Recurse -Force $DataPath }
$mods = Join-Path $DataPath 'Mods'
New-Item -ItemType Directory -Force (Join-Path $mods 'flockstest') | Out-Null
$version = (Get-Content -Raw (Join-Path $root 'Flocks/modinfo.json') | ConvertFrom-Json).version
Copy-Item (Join-Path $root "dist/flocks_$version.zip") $mods
Copy-Item (Join-Path $PSScriptRoot 'FlocksTest/bin/Release/FlocksTest.dll'), (Join-Path $PSScriptRoot 'FlocksTest/bin/Release/modinfo.json') (Join-Path $mods 'flockstest')

$server = Start-Process -FilePath (Join-Path $GamePath 'VintagestoryServer.exe') -WorkingDirectory $GamePath -PassThru -WindowStyle Hidden `
    -ArgumentList @("--dataPath=`"$DataPath`"", '--port=42455')
if (-not $server.WaitForExit($TimeoutMinutes * 60 * 1000)) { $server.Kill(); Write-Warning "Server killed after $TimeoutMinutes minutes." }

$log = Join-Path $DataPath 'Logs/server-main.log'
Get-Content $log | Select-String -Pattern '\[FlocksTest\]|\[Error\]|\[Warning\]|Exception|\[Flocks\]'
