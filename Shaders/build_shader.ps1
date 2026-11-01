# Builds ..\Client\hideoutextensions.bundle (the overview cap shader) with the Unity version the game runs on.
param([string] $Unity = 'C:\Program Files\Unity\Hub\Editor\2022.3.43f1\Editor\Unity.exe',
      [string] $Project = (Join-Path $env:TEMP 'HideoutExtensionsShaders'))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force (Join-Path $Project 'Assets\Editor') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'CapWithHoles.shader') (Join-Path $Project 'Assets') -Force
Remove-Item (Join-Path $Project 'Assets\Portal.shader*') -ErrorAction SilentlyContinue
Copy-Item (Join-Path $PSScriptRoot 'BuildBundle.cs') (Join-Path $Project 'Assets\Editor') -Force
$log = Join-Path $Project 'build.log'
& $Unity -batchmode -nographics -projectPath $Project -executeMethod BuildBundle.Run -quit -logFile $log | Out-Null
$bundle = Join-Path $Project 'Bundles\hideoutextensions.bundle'
if (-not (Test-Path $bundle)) { Get-Content $log -Tail 40; throw "Unity didn't make the bundle (log: $log)." }
Copy-Item $bundle (Join-Path $PSScriptRoot '..\Client\hideoutextensions.bundle') -Force
Write-Host "Built $bundle"
