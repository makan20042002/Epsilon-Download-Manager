param(
  [Parameter(Mandatory=$true)][string]$CertificateThumbprint,
  [Parameter(Mandatory=$true)][string[]]$Files
)
$ErrorActionPreference='Stop'
$cert=Get-ChildItem Cert:\CurrentUser\My\$CertificateThumbprint -ErrorAction Stop
foreach($file in $Files){
  if(!(Test-Path $file)){throw "Missing file: $file"}
  Set-AuthenticodeSignature -FilePath $file -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com' | Format-Table Status,StatusMessage
}
