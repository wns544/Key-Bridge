$ErrorActionPreference = "Stop"

$appName = "KeyBridge"
$installDir = Join-Path $env:LOCALAPPDATA "Programs\KeyBridge"
$sourceExe = Join-Path $PSScriptRoot "KeyBridge.exe"
$targetExe = Join-Path $installDir "KeyBridge.exe"

if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "KeyBridge.exe was not found next to the installer script."
}

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force

$shell = New-Object -ComObject WScript.Shell

$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\KeyBridge"
New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null

function New-KeyBridgeShortcut {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $targetExe
    $shortcut.WorkingDirectory = $installDir
    $shortcut.IconLocation = "$targetExe,0"
    $shortcut.Description = "Launch KeyBridge"
    $shortcut.Save()
}

New-KeyBridgeShortcut -Path (Join-Path $startMenuDir "$appName.lnk")
New-KeyBridgeShortcut -Path (Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "$appName.lnk")

Write-Host "KeyBridge installed to $installDir"
