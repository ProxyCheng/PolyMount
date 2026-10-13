[CmdletBinding()]
param(
    [ValidateSet('Stop','Install','Uninstall','Check')][string]$Action,
    [string]$InstallDirectory,
    [switch]$UserAccount
)
$ErrorActionPreference = 'Stop'
$data = Join-Path $env:ProgramData 'PolyMount'
$config = Join-Path $data 'config.json'
$service = Get-Service -Name PolyMount -ErrorAction SilentlyContinue
function Stop-HostProcesses {
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name PolyMount
        (Get-Service PolyMount).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
    }
    # Also close development/interactive hosting if it was started outside SCM.
    $hosts = @(Get-Process -Name 'PolyMount.Service' -ErrorAction SilentlyContinue)
    if ($hosts.Count -gt 0) {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'PolyMount.Control', [System.IO.Pipes.PipeDirection]::InOut)
        try {
            $pipe.Connect(2000)
            $writer = New-Object System.IO.StreamWriter($pipe)
            $writer.AutoFlush = $true
            $writer.WriteLine('{"Cmd":"shutdown"}')
        } finally { $pipe.Dispose() }
        foreach ($hostProcess in $hosts) {
            if (-not $hostProcess.WaitForExit(30000)) { throw 'PolyMount host did not exit; close it before updating.' }
        }
    }
    Get-Process -Name 'PolyMount.Tray' -ErrorAction SilentlyContinue | Stop-Process
}
try {
    switch ($Action) {
        'Stop' { Stop-HostProcesses }
        'Check' {
            if (-not (Test-Path -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'WinFsp\bin\winfsp-x64.dll'))) { throw 'WinFsp is missing.' }
            if (-not (Test-Path -LiteralPath (Join-Path $InstallDirectory 'Service\PolyMount.Service.exe'))) { throw 'Service executable is missing.' }
            if (-not (Test-Path -LiteralPath (Join-Path $InstallDirectory 'Tray\platforms\qwindows.dll'))) { throw 'Qt Windows platform plugin is missing.' }
        }
        'Install' {
            New-Item -ItemType Directory -Path $data -Force | Out-Null
            if (-not (Test-Path -LiteralPath $config)) {
                @{ WinFspBin = (Join-Path ${env:ProgramFiles(x86)} 'WinFsp\bin'); ControlPipe = 'PolyMount.Control'; Plugins = @(); Mounts = @(); LayerColors = @{} } |
                    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $config -Encoding UTF8
            }
            $exe = Join-Path $InstallDirectory 'Service\PolyMount.Service.exe'
            $binary = '"' + $exe + '" --config "' + $config + '"'
            if ($service) {
                & sc.exe config PolyMount binPath= $binary start= auto
                if ($LASTEXITCODE -ne 0) { throw 'Failed to update service registration.' }
                $account = (Get-CimInstance Win32_Service -Filter "Name='PolyMount'").StartName
                if ($account -eq 'LocalSystem') { $account = 'NT AUTHORITY\SYSTEM' }
            } elseif ($UserAccount) {
                # Credentials never enter installer arguments, logs or files.
                $credential = Get-Credential -Message 'PolyMount service account (use your Windows account to reuse cached SVN credentials)'
                if (-not $credential) { throw 'Service account credentials were cancelled.' }
                New-Service -Name PolyMount -DisplayName 'PolyMount Virtual Mount Service' -BinaryPathName $binary -StartupType Automatic -Credential $credential | Out-Null
                $account = $credential.UserName
            } else {
                & sc.exe create PolyMount binPath= $binary start= auto obj= 'NT AUTHORITY\LocalService' DisplayName= 'PolyMount Virtual Mount Service'
                if ($LASTEXITCODE -ne 0) { throw 'Failed to create service.' }
                $account = 'NT AUTHORITY\LOCAL SERVICE'
            }
            $acl = Get-Acl -LiteralPath $data
            $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($account,'Modify','ContainerInherit,ObjectInherit','None','Allow')
            $acl.SetAccessRule($rule)
            Set-Acl -LiteralPath $data -AclObject $acl
            & sc.exe description PolyMount 'Layered virtual drives, on-demand file access.' | Out-Null
            Start-Service PolyMount
            (Get-Service PolyMount).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(30))
        }
        'Uninstall' {
            Stop-HostProcesses
            if ($service) { & sc.exe delete PolyMount; if ($LASTEXITCODE -ne 0) { throw 'Failed to remove service.' } }
            # ProgramData configuration, credentials and provider data are intentionally kept.
        }
    }
    exit 0
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
