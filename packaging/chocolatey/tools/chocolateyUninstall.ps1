$ErrorActionPreference = 'Stop'

$software = Get-UninstallRegistryKey -SoftwareName 'Epsilon Download Manager*' | Select-Object -First 1
if ($null -eq $software) {
    Write-Warning 'Epsilon Download Manager is not registered as installed.'
    return
}

$uninstallString = $software.UninstallString
if ([string]::IsNullOrWhiteSpace($uninstallString)) {
    throw 'The Epsilon Download Manager uninstall command is missing.'
}

$file = $uninstallString.Trim('"')
$packageArgs = @{
    packageName    = 'epsilon-download-manager'
    fileType       = 'exe'
    silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
    validExitCodes = @(0)
    file           = $file
}

Uninstall-ChocolateyPackage @packageArgs
