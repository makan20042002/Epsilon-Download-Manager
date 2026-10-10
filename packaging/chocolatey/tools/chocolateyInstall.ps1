$ErrorActionPreference = 'Stop'

$packageArgs = @{
    packageName    = 'epsilon-download-manager'
    fileType       = 'exe'
    url64bit       = 'https://github.com/makan20042002/Epsilon-Download-Manager/releases/download/v1.7.1/EpsilonDownloadManager-1.7.1-Setup.exe'
    checksum64     = '03748DD0151BA1D73D60AA0FBAA2F7D7471E5CFECB845AF1BE89B7825C9B778B'
    checksumType64 = 'sha256'
    silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
    validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
