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

# The install tree ships a source plugin; a configuration that does not name it has no SVN source
# to pick from at all, which is the difference between "installed" and "usable". The path is
# relative to the host's own directory (PluginLoader resolves it against AppContext.BaseDirectory),
# so it survives the install directory being chosen or changed.
$bundledPlugin = 'plugins/PolyMount.Plugin.Svn.dll'
$bundledPluginFile = 'Service\plugins\PolyMount.Plugin.Svn.dll'
# The migration below may only happen once per installation, and "the list happens to be empty"
# is not the same statement as "nobody ever filled it in" -- someone who removed the entry on
# purpose must not find it back after the next upgrade.
$seedMarker = Join-Path $data '.plugins-seeded'

function Save-Config([string]$json) {
    # Written beside the target and moved over it, with the previous file kept: the host reads this
    # file on every start, and one that is caught half-written cannot build its instance at all.
    $temp = $config + '.tmp'
    [System.IO.File]::WriteAllText($temp, $json, (New-Object System.Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $config) { Copy-Item -LiteralPath $config -Destination ($config + '.bak') -Force }
    Move-Item -LiteralPath $temp -Destination $config -Force
}

function Write-FreshConfig {
    Save-Config (@{
        WinFspBin = (Join-Path ${env:ProgramFiles(x86)} 'WinFsp\bin')
        ControlPipe = 'PolyMount.Control'
        Plugins = @(@{ AssemblyPath = $bundledPlugin; Enabled = $true })
        Mounts = @()
        LayerColors = @{}
    } | ConvertTo-Json -Depth 20)
    New-Item -ItemType File -Path $seedMarker -Force | Out-Null
}

function Add-BundledPlugins {
    # An installation made before the plugin was bundled keeps a configuration whose plugin list is
    # empty, and an empty list stays empty: the host deliberately loads nothing it was not told
    # about, so without this the operator has no SVN source to choose from at all. A configuration
    # that already names plugins is the operator's and is left exactly as it is.
    if (Test-Path -LiteralPath $seedMarker) { return }
    if (-not (Test-Path -LiteralPath (Join-Path $InstallDirectory $bundledPluginFile))) { return }
    try { $parsed = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json }
    catch {
        # Unreadable is not ours to overwrite; the marker stays away so a later install may retry.
        Write-Warning "Leaving $config alone: $($_.Exception.Message)"
        return
    }
    if ($null -eq $parsed.Plugins -or @($parsed.Plugins).Count -eq 0) {
        $parsed | Add-Member -NotePropertyName Plugins -NotePropertyValue @(@{ AssemblyPath = $bundledPlugin; Enabled = $true }) -Force
        Save-Config ($parsed | ConvertTo-Json -Depth 20)
    }
    New-Item -ItemType File -Path $seedMarker -Force | Out-Null
}

function Stop-HostProcesses {
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name PolyMount
        (Get-Service PolyMount).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
    }
    # A host started outside the SCM is asked to shut down over its own pipe, so its mounts come
    # down in order; one that does not answer is ended here instead, because a process still
    # holding these files is a process this install cannot replace.
    $hosts = @(Get-Process -Name 'PolyMount.Service' -ErrorAction SilentlyContinue)
    if ($hosts.Count -gt 0) {
        try {
            $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'PolyMount.Control', [System.IO.Pipes.PipeDirection]::InOut)
            try {
                $pipe.Connect(2000)
                $writer = New-Object System.IO.StreamWriter($pipe)
                try { $writer.AutoFlush = $true; $writer.WriteLine('{"Cmd":"shutdown"}') } finally { $writer.Dispose() }
            } finally { $pipe.Dispose() }
        } catch {
            # Nothing is listening, or it went away mid-sentence: there is nobody left to ask.
        }
        foreach ($hostProcess in $hosts) {
            if (-not $hostProcess.WaitForExit(15000)) {
                $hostProcess.Kill()
                $hostProcess.WaitForExit(5000) | Out-Null
            }
        }
    }
    Get-Process -Name 'PolyMount.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
try {
    switch ($Action) {
        'Stop' { Stop-HostProcesses }
        'Check' {
            if (-not (Test-Path -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'WinFsp\bin\winfsp-x64.dll'))) { throw 'WinFsp is missing.' }
            if (-not (Test-Path -LiteralPath (Join-Path $InstallDirectory 'Service\PolyMount.Service.exe'))) { throw 'Service executable is missing.' }
            if (-not (Test-Path -LiteralPath (Join-Path $InstallDirectory 'Tray\platforms\qwindows.dll'))) { throw 'Qt Windows platform plugin is missing.' }
            if (-not (Test-Path -LiteralPath (Join-Path $InstallDirectory 'Service\plugins\PolyMount.Plugin.Svn.dll'))) { throw 'The bundled source plugin is missing.' }
        }
        'Install' {
            # The Setup calls Stop before it copies anything; an Install run by hand has to do the
            # same, or it writes the configuration underneath a host that is watching that file.
            Stop-HostProcesses
            New-Item -ItemType Directory -Path $data -Force | Out-Null
            if (Test-Path -LiteralPath $config) { Add-BundledPlugins } else { Write-FreshConfig }
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
