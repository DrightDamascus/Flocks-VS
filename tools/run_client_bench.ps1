param(
    [string]$Label = 'with-flocks',
    # a world from your saves (file name without .vcdbs); a copy is played, never the original
    [string]$World = 'peaceful kingdom story',
    # mod zips to load besides the benchmark (file names in your Mods folder, or full paths); include a flocks zip or not
    [string[]]$Mods = @(),
    [int]$Warmup = 90,
    [int]$Seconds = 120,
    [int]$TimeoutMinutes = 12,
    # chat commands the player sends once in the world, ';'-separated
    [string]$Commands = '/time set day',
    [string]$GamePath = 'C:\Steam\steamapps\common\Vintagestory',
    [string]$UserData = (Join-Path $env:APPDATA 'VintagestoryData')
)
# Frame-time benchmark in the real game client: a scratch data folder (%TEMP%\flocks-clientbench\<Label>) with your
# client settings (same graphics), only the given mods plus FlocksClientBench, and a copy of the world. The game opens
# the world, warms up, measures, writes flocksbench-results.txt and quits. Leave the mouse and keyboard alone meanwhile.
$ErrorActionPreference = 'Stop'
& dotnet build (Join-Path $PSScriptRoot 'FlocksClientBench/FlocksClientBench.csproj') -c Release -warnaserror | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'FlocksClientBench build failed.' }

$data = Join-Path $env:TEMP "flocks-clientbench\$Label"
if (Test-Path $data) { Remove-Item -Recurse -Force $data }
foreach ($d in 'Mods\flocksbench', 'Saves') { New-Item -ItemType Directory -Force (Join-Path $data $d) | Out-Null }

# your settings, with only this folder's Mods and nothing disabled
# (single quotes inside: Windows PowerShell drops double quotes from a native program's arguments)
$py = 'import json,sys; d=json.load(open(sys.argv[1],encoding=''utf-8-sig'')); s=d[''stringListSettings'']; s[''modPaths'']=[''Mods'', sys.argv[3]]; s[''disabledMods'']=[]; json.dump(d,open(sys.argv[2],''w'',encoding=''utf-8''),indent=1)'
& python -I -c $py (Join-Path $UserData 'clientsettings.json') (Join-Path $data 'clientsettings.json') (Join-Path $data 'Mods')
if ($LASTEXITCODE -ne 0) { throw 'Could not copy the client settings.' }
if (Test-Path (Join-Path $UserData 'ModConfig')) { Copy-Item -Recurse (Join-Path $UserData 'ModConfig') $data }

foreach ($m in $Mods) {
    $src = if (Test-Path $m) { $m } else { Join-Path $UserData "Mods\$m" }
    Copy-Item $src (Join-Path $data 'Mods')
}
Copy-Item (Join-Path $PSScriptRoot 'FlocksClientBench/bin/Release/FlocksClientBench.dll'), (Join-Path $PSScriptRoot 'FlocksClientBench/bin/Release/modinfo.json') (Join-Path $data 'Mods\flocksbench')
foreach ($ext in '.vcdbs', '.vcdbs-wal') {
    $f = Join-Path $UserData "Saves\$World$ext"
    if (Test-Path $f) { Copy-Item $f (Join-Path $data 'Saves') }
}

$env:FLOCKSBENCH_WARMUP = $Warmup; $env:FLOCKSBENCH_SECONDS = $Seconds; $env:FLOCKSBENCH_LABEL = $Label; $env:FLOCKSBENCH_COMMANDS = $Commands
$game = Start-Process -FilePath (Join-Path $GamePath 'Vintagestory.exe') -WorkingDirectory $GamePath -PassThru `
    -ArgumentList @("--dataPath=`"$data`"", "--openWorld=`"$World`"")
if (-not $game.WaitForExit($TimeoutMinutes * 60 * 1000)) { $game.Kill(); Write-Warning "Game killed after $TimeoutMinutes minutes." }

# the copied settings hold your login session: don't leave them lying around
Remove-Item -Force (Join-Path $data 'clientsettings.json') -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force (Join-Path $data 'Saves') -ErrorAction SilentlyContinue
$results = Join-Path $data 'flocksbench-results.txt'
if (Test-Path $results) { Get-Content $results } else { Write-Warning "No results; see $data\Logs\client-main.log" }
