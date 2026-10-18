<#
.SYNOPSIS
  Trusts a self-signed PolyMount code-signing certificate on this machine.
.DESCRIPTION
  A signature made with a self-signed certificate verifies only where that certificate is
  trusted. The Root store makes the chain valid; the TrustedPublisher store makes the publisher
  known to the certificate UI and to application-control policies that name a publisher. This
  does not create SmartScreen reputation -- that comes from a commercial certificate or from
  downloads accumulating -- so the warning a downloaded installer gets is unchanged.

  Machine-wide by default (run from an elevated prompt); -User trusts it for the current
  account only.
.EXAMPLE
  .\Trust-SigningCertificate.ps1 -Certificate .\PolyMount-Signing.cer
.EXAMPLE
  .\Trust-SigningCertificate.ps1 -Certificate .\PolyMount-Signing.cer -User
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Certificate,
    [switch]$User
)
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $Certificate).Path
foreach ($store in 'Root', 'TrustedPublisher') {
    $arguments = @('-addstore', '-f')
    if ($User) { $arguments += '-user' }
    $arguments += @($store, $path)
    & certutil.exe @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "certutil could not add the certificate to $store (exit $LASTEXITCODE)." }
    Write-Host "Imported into $store."
}
Write-Host "Trusted $([IO.Path]::GetFileName($path)). Future PolyMount installers signed with this certificate verify on this machine."
