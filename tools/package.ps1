# Builds the release zip from bin\Release (run `dotnet build -c Release` first):
#   dist\ValAgentBridge-<ver>.zip   Thunderstore layout (manifest.json, icon.png, README.md at the root, DLLs in
#                                   plugins\), the one package for Thunderstore, other mod sites and GitHub releases
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

$entries = [ordered]@{
    'manifest.json'            = 'thunderstore\manifest.json'
    'icon.png'                 = 'thunderstore\icon.png'
    'README.md'                = 'thunderstore\README.md'
    'TOOLS.md'                 = 'TOOLS.md'
    'CHANGELOG.md'             = 'CHANGELOG.md'
    'LICENSE'                  = 'LICENSE'
    'THIRD-PARTY-NOTICES.md'   = 'THIRD-PARTY-NOTICES.md'
    'plugins/ValAgentBridge.dll' = $dll
    'plugins/Lib.GAB.dll'      = 'lib\Lib.GAB.dll'
    'plugins/Gabp.Runtime.dll' = 'lib\Gabp.Runtime.dll'
}

New-Item -ItemType Directory -Force dist | Out-Null
$path = "dist\ValAgentBridge-$ver.zip"
if (Test-Path $path) { Remove-Item $path }
$zip = [System.IO.Compression.ZipFile]::Open($path, 'Create')
try {
    foreach ($k in $entries.Keys) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Resolve-Path $entries[$k]).Path, $k, 'Optimal') | Out-Null
    }
} finally { $zip.Dispose() }
"{0}  {1} KB, {2} files" -f $path, [math]::Round((Get-Item $path).Length / 1KB), $entries.Count
