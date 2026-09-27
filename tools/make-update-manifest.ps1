param(
  [Parameter(Mandatory=$true)][string]$Package,
  [Parameter(Mandatory=$true)][string]$Version,
  [Parameter(Mandatory=$true)][string]$PackageUrl,
  [Parameter(Mandatory=$true)][string]$Output
)
$ErrorActionPreference='Stop'
if($PackageUrl -notmatch '^https://'){ throw 'PackageUrl must use HTTPS.' }
$hash=(Get-FileHash -Algorithm SHA256 -Path $Package).Hash.ToLowerInvariant()
@{version=$Version;packageUrl=$PackageUrl;sha256=$hash} | ConvertTo-Json | Set-Content -Encoding UTF8 $Output
Write-Host "Update manifest created: $Output" -ForegroundColor Green
Write-Host "SHA-256: $hash"
