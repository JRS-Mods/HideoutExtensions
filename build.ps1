param(
    [string] $SPTPath = 'F:\SPT',
    [switch] $NoInstall
)

$ErrorActionPreference = 'Stop'
$name = 'HideoutExtensions'
$root = $PSScriptRoot
$client = Join-Path $root "Client\$name.csproj"
$server = Join-Path $root "Server\$name.Server.csproj"

if (-not (Test-Path (Join-Path $SPTPath 'BepInEx\core\BepInEx.dll'))) {
    throw "$SPTPath doesn't look like an SPT folder (no BepInEx\core\BepInEx.dll)."
}

# One version for both halves.
$version = ([xml](Get-Content $client)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$pluginVersion = (Select-String -Path (Join-Path $root 'Client\Plugin.cs') -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
$serverVersion = ([xml](Get-Content $server)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$metaVersion = (Select-String -Path (Join-Path $root 'Server\ModMetadata.cs') -Pattern 'new\("([^"]+)"\)').Matches[0].Groups[1].Value
if ($version -ne $pluginVersion -or $version -ne $serverVersion -or $version -ne $metaVersion) {
    throw "Versions differ: client csproj $version, Plugin.cs $pluginVersion, server csproj $serverVersion, ModMetadata $metaVersion."
}

$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dotnet = if (Test-Path $userDotnet) { $userDotnet } else { 'dotnet' }
& $dotnet build $client -c Release "-p:SPTPath=$SPTPath" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Client build failed.' }
& $dotnet build $server -c Release "-p:SPTPath=$SPTPath" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }

$clientDll = Join-Path $root "Client\bin\Release\$name.dll"
$clientXml = Join-Path $root "Client\bin\Release\$name.xml"
$serverDll = Join-Path $root "Server\bin\Release\$name.Server.dll"
# The overview cap's shader (Shaders\build_shader.ps1, needs a signed-in Unity 2022.3.43); without it built rooms stay black on the overview.
$shaderBundle = Join-Path $root 'Client\hideoutextensions.bundle'
if (-not (Test-Path $shaderBundle)) { Write-Warning "No $shaderBundle (run Shaders\build_shader.ps1): built rooms will stay black on the overview." }
$spots = @(Get-ChildItem -LiteralPath (Join-Path $root 'spots') -Filter '*.json' -File)
$readme = Join-Path $root 'README.md'
$license = Join-Path $root 'LICENSE.txt'
# The overview icon (plain, and glowing while its panel is open).
$pictures = @(Get-ChildItem -LiteralPath (Join-Path $root 'Client\assets') -Filter '*.png' -File -ErrorAction SilentlyContinue)
foreach ($f in $clientDll, $serverDll, $readme, $license) { if (-not (Test-Path $f)) { throw "Missing $f" } }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist "$name-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    $add = { param($file, $entry) [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, $entry) | Out-Null }
    & $add $clientDll "BepInEx/plugins/$name/$name.dll"
    # The API's documentation, for mods that build against it.
    if (Test-Path $clientXml) { & $add $clientXml "BepInEx/plugins/$name/$name.xml" }
    & $add $readme "BepInEx/plugins/$name/README.md"
    & $add $license "BepInEx/plugins/$name/LICENSE.txt"
    & $add $license "SPT_Runtime/user/mods/$name.Server/LICENSE.txt"
    foreach ($picture in $pictures) { & $add $picture.FullName "BepInEx/plugins/$name/$($picture.Name)" }
    if (Test-Path $shaderBundle) { & $add $shaderBundle "BepInEx/plugins/$name/hideoutextensions.bundle" }
    & $add $serverDll "SPT_Runtime/user/mods/$name.Server/$name.Server.dll"
    foreach ($spot in $spots) { & $add $spot.FullName "SPT_Runtime/user/mods/$name.Server/spots/$($spot.Name)" }
}
finally {
    $archive.Dispose()
}
Write-Host "Packed $zip ($($spots.Count) spot(s))"

if (-not $NoInstall) {
    $clientDest = Join-Path $SPTPath "BepInEx\plugins\$name"
    New-Item -ItemType Directory -Force $clientDest | Out-Null
    Copy-Item $clientDll, $readme $clientDest -Force
    if ($pictures.Count -gt 0) { Copy-Item -LiteralPath $pictures.FullName -Destination $clientDest -Force }
    if (Test-Path $clientXml) { Copy-Item $clientXml $clientDest -Force }
    if (Test-Path $shaderBundle) { Copy-Item $shaderBundle $clientDest -Force }
    $serverDest = Join-Path $SPTPath "SPT_Runtime\user\mods\$name.Server"
    New-Item -ItemType Directory -Force (Join-Path $serverDest 'spots') | Out-Null
    # A running server locks its DLL: move that one aside (it keeps running) and clear older ones once they're free.
    Get-ChildItem $serverDest -Filter '*.dll.previous*' -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item $_.FullName -ErrorAction SilentlyContinue }
    $installed = Join-Path $serverDest "$name.Server.dll"
    if (Test-Path $installed) {
        try { [IO.File]::Open($installed, 'Open', 'ReadWrite', 'None').Close() }
        catch { Rename-Item $installed "$name.Server.dll.previous-$(Get-Date -Format yyyyMMddHHmmss)" }
    }
    Copy-Item $serverDll $serverDest -Force
    # The spots folder is the mod's: what's installed matches this build.
    Get-ChildItem (Join-Path $serverDest 'spots') -Filter '*.json' -File | Where-Object { $spots.Name -notcontains $_.Name } | Remove-Item
    if ($spots.Count -gt 0) { Copy-Item -LiteralPath $spots.FullName -Destination (Join-Path $serverDest 'spots') -Force }
    Write-Host "Installed to $clientDest and $serverDest (restart the SPT server)"
}
