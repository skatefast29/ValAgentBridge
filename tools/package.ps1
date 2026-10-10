# Builds the release zips from bin\Release (run `dotnet build -c Release` first):
#   dist\ValAgentBridge-<ver>.zip                    BepInEx layout, for GitHub releases and Nexus
#   dist\BarryWhite-ValAgentBridge-<ver>-thunderstore.zip   Thunderstore layout (manifest.json, icon.png, README.md at the root)
#
#   powershell -ExecutionPolicy Bypass -File tools\package.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$dll = 'bin\Release\ValAgentBridge.dll'
if (-not (Test-Path $dll)) { throw "$dll not found: build first (dotnet build -c Release)" }
$ver = (Get-Item $dll).VersionInfo.FileVersion
$manifest = Get-Content thunderstore\manifest.json -Raw | ConvertFrom-Json
if ($manifest.version_number -ne $ver) { throw "thunderstore\manifest.json says $($manifest.version_number), the DLL is $ver" }

function New-Zip($path, $entries) {
    if (Test-Path $path) { Remove-Item $path }
    $zip = [System.IO.Compression.ZipFile]::Open($path, 'Create')
    try {
        foreach ($k in $entries.Keys) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Resolve-Path $entries[$k]).Path, $k, 'Optimal') | Out-Null
        }
    } finally { $zip.Dispose() }
    "{0}  {1} KB, {2} files" -f $path, [math]::Round((Get-Item $path).Length / 1KB), $entries.Count
}

New-Item -ItemType Directory -Force dist | Out-Null
$docs = [ordered]@{
    'CHANGELOG.md'           = 'CHANGELOG.md'
    'LICENSE'                = 'LICENSE'
    'THIRD-PARTY-NOTICES.md' = 'THIRD-PARTY-NOTICES.md'
}

$plain = [ordered]@{
    'BepInEx/plugins/ValAgentBridge/ValAgentBridge.dll'    = $dll
    'BepInEx/plugins/ValAgentBridge/Lib.GAB.dll'      = 'lib\Lib.GAB.dll'
    'BepInEx/plugins/ValAgentBridge/Gabp.Runtime.dll' = 'lib\Gabp.Runtime.dll'
    'README.md'                                  = 'README.md'
    'TOOLS.md'                                   = 'TOOLS.md'
}
foreach ($k in $docs.Keys) { $plain[$k] = $docs[$k] }
New-Zip "dist\ValAgentBridge-$ver.zip" $plain

$ts = [ordered]@{
    'manifest.json'            = 'thunderstore\manifest.json'
    'icon.png'                 = 'thunderstore\icon.png'
    'README.md'                = 'thunderstore\README.md'
    'TOOLS.md'                 = 'TOOLS.md'
    'plugins/ValAgentBridge.dll'    = $dll
    'plugins/Lib.GAB.dll'      = 'lib\Lib.GAB.dll'
    'plugins/Gabp.Runtime.dll' = 'lib\Gabp.Runtime.dll'
}
foreach ($k in $docs.Keys) { $ts[$k] = $docs[$k] }
New-Zip "dist\BarryWhite-ValAgentBridge-$ver-thunderstore.zip" $ts
