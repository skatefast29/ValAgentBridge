# Decompiles the game and selected mods into reference\decompiled\ so Claude can search the real code.
# Re-run after a Valheim or mod update; unchanged DLLs are skipped (compared by SHA-256).
# The output is decompiled game code: it stays local (reference\ is gitignored) and is never published.
#
#   powershell -ExecutionPolicy Bypass -File tools\decompile.ps1
#   powershell -ExecutionPolicy Bypass -File tools\decompile.ps1 -Mods AdventureBackpacks,SomeOtherMod
#
# -Mods matches DLL file names inside the Gale profile's plugins folder.

param(
    [string[]]$Mods = @('AdventureBackpacks', 'Backpacks'),
    [string]$Profile = 'Performance Test',
    [string]$ValheimDir = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$env:PATH = "C:\Program Files\dotnet;$env:USERPROFILE\.dotnet\tools;$env:PATH"
$out = Join-Path $PSScriptRoot '..\reference\decompiled' | Resolve-Path -ErrorAction SilentlyContinue
if (-not $out) { $out = New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot '..\reference\decompiled') }
$out = "$out"

$managed = Join-Path $ValheimDir 'valheim_Data\Managed'
$plugins = Join-Path $env:APPDATA "com.kesomannen.gale\valheim\profiles\$Profile\BepInEx\plugins"
$core = Join-Path $env:APPDATA "com.kesomannen.gale\valheim\profiles\$Profile\BepInEx\core"

$targets = @(
    Get-Item (Join-Path $managed 'assembly_valheim.dll')
    Get-Item (Join-Path $managed 'assembly_utils.dll')
)
foreach ($m in $Mods) {
    $found = @(Get-ChildItem $plugins -Recurse -Filter "$m.dll")
    if ($found.Count -eq 0) { Write-Warning "$m.dll not found in $plugins"; continue }
    $targets += $found[0]
}

foreach ($dll in $targets) {
    $name = [IO.Path]::GetFileNameWithoutExtension($dll.Name)
    $dest = Join-Path $out $name
    $stamp = Join-Path $dest 'SOURCE.txt'
    $hash = (Get-FileHash $dll.FullName -Algorithm SHA256).Hash
    if (-not $Force -and (Test-Path $stamp) -and (Select-String -Path $stamp -SimpleMatch $hash -Quiet)) {
        Write-Host "$name - unchanged, skipped"
        continue
    }
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Write-Host "$name - decompiling..."
    # -p = one .cs file per type (easy to grep); -r = where to resolve references from
    & ilspycmd -p -o $dest -r $managed -r $core -r $plugins $dll.FullName 2>&1 |
        Where-Object { $_ -notmatch '^\s*$' } | Select-Object -Last 3
    $ver = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll.FullName).FileVersion
    @(
        "File:     $($dll.FullName)"
        "Version:  $ver"
        "Modified: $($dll.LastWriteTime.ToString('yyyy-MM-dd HH:mm'))"
        "SHA256:   $hash"
        "Decompiled: $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
    ) | Set-Content -Encoding utf8 $stamp
}
Write-Host "Done: $out"
