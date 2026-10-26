[CmdletBinding()]
param(
    [ValidateSet('Stop','Install','Uninstall','Check')][string]$Action,
    [string]$InstallDirectory,
    [switch]$UserAccount,
    # ';'-separated file names of the bundled plugins the wizard selected.
    [string]$Plugins = '',
    # ';'-separated file names of every bundled plugin this setup manages. Both lists are built
    # from one table in the installer script, so neither side has to enumerate a directory and
    # infer which of its contents the operator was ever asked about.
    [string]$Managed = ''
)
$ErrorActionPreference = 'Stop'
$data = Join-Path $env:ProgramData 'PolyMount'
$config = Join-Path $data 'config.json'
$service = Get-Service -Name PolyMount -ErrorAction SilentlyContinue
$pluginDirectory = Join-Path $InstallDirectory 'Service\plugins'

function Split-List([string]$value) {
    @($value -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Get-InstalledPlugins {
    # A hand-run Install names no lists. Then everything the install tree carries is managed and
    # selected, which is what this script did before the wizard had an opinion.
    if (-not (Test-Path -LiteralPath $pluginDirectory)) { return @() }
    @(Get-ChildItem -LiteralPath $pluginDirectory -Filter 'PolyMount.Plugin.*.dll' -File |
        Where-Object { $_.Name -notlike '*.Tests.dll' } | ForEach-Object { $_.Name })
}

$managedFiles = if ($Managed) { Split-List $Managed } else { Get-InstalledPlugins }
$selectedFiles = if ($PSBoundParameters.ContainsKey('Plugins')) { Split-List $Plugins } else { $managedFiles }

function Test-PluginPresent([string]$file) {
    Test-Path -LiteralPath (Join-Path $pluginDirectory $file)
}

function Get-PluginEntry([string]$file) {
    # Relative to the host's own directory (PluginLoader resolves it against
    # AppContext.BaseDirectory), so it survives the install directory being chosen or changed.
    @{ AssemblyPath = 'plugins/' + $file; Enabled = $true }
}

function ConvertTo-PluginKey([string]$path) {
    ([string]$path).Replace('\', '/').ToLowerInvariant()
}

function Save-Config([string]$json) {
    # Written beside the target and moved over it, with the previous file kept: the host reads this
    # file on every start, and one that is caught half-written cannot build its instance at all.
    $temp = $config + '.tmp'
    [System.IO.File]::WriteAllText($temp, $json, (New-Object System.Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $config) { Copy-Item -LiteralPath $config -Destination ($config + '.bak') -Force }
    Move-Item -LiteralPath $temp -Destination $config -Force
}

function Write-FreshConfig {
    $entries = @($selectedFiles | Where-Object { Test-PluginPresent $_ } | ForEach-Object { Get-PluginEntry $_ })
    Save-Config (@{
        WinFspBin = (Join-Path ${env:ProgramFiles(x86)} 'WinFsp\bin')
        ControlPipe = 'PolyMount.Control'
        Plugins = $entries
        Mounts = @()
        LayerColors = @{}
    } | ConvertTo-Json -Depth 20)
}

function Set-ControlUsers {
    # The control plane's allowlist, seeded from the account that ran setup: this is the one moment
    # the product knows both who administers the machine and who expects to drive it. Without it
    # the pipe stays open to every interactive session, which the host reports in its log and in
    # every status answer, because a configuration written by a service in session 0 cannot know
    # who that user is.
    if (-not (Test-Path -LiteralPath $config)) { return }
    try { $parsed = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json }
    catch {
        Write-Warning "Leaving $config alone: $($_.Exception.Message)"
        return
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $sid = $identity.User.Value
    $users = New-Object System.Collections.ArrayList
    foreach ($entry in @($parsed.ControlUsers)) { if ($entry -and $entry.Sid) { [void]$users.Add($entry) } }
    if (@($users | Where-Object { $_.Sid -eq $sid }).Count -gt 0) { return }
    [void]$users.Add([pscustomobject]@{ Sid = $sid; Name = $identity.Name })
    $parsed | Add-Member -NotePropertyName ControlUsers -NotePropertyValue @($users) -Force
    Save-Config ($parsed | ConvertTo-Json -Depth 20)
}

function Protect-DataDirectory([string]$serviceAccount) {
    # The data directory holds the configuration, the stored credentials and the stores. What it
    # inherits from ProgramData is "Users may read this and create entries in it", which is a
    # repository read and a store pre-creation for every local account. Inheritance is broken and
    # the accounts that use it are named instead: SYSTEM, administrators, the service account, and
    # the account that ran setup (the one that drives the control pipe and reads the configuration
    # when the service is stopped).
    #
    # icacls and not Get-Acl/Set-Acl, for the reason the grant below gives. Written so that a
    # failure to tighten is reported but never leaves the directory without the service account's
    # access: the grants are applied first and inheritance is removed afterwards.
    $grantee = switch ($serviceAccount) {
        'NT AUTHORITY\SYSTEM' { '*S-1-5-18' }
        'NT AUTHORITY\LOCAL SERVICE' { '*S-1-5-19' }
        default { '"' + $serviceAccount + '"' }
    }
    $installer = '*' + ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    $rules = @(
        '*S-1-5-18:(OI)(CI)F',
        '*S-1-5-32-544:(OI)(CI)F',
        $grantee + ':(OI)(CI)M',
        $installer + ':(OI)(CI)M'
    )
    & icacls.exe $data /grant:r @rules /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to set the data directory's access rules (icacls exit $LASTEXITCODE)." }
    & icacls.exe $data /inheritance:r /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to remove inherited access from '$data' (icacls exit $LASTEXITCODE)." }
    # The stores are the service's alone: the account that drives the control plane reads the
    # configuration over the pipe, not the cache on disk.
    $stores = Join-Path $data 'stores'
    if (Test-Path -LiteralPath $stores) {
        & icacls.exe $stores /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' ($grantee + ':(OI)(CI)M') /Q | Out-Null
    }
}

function Sync-BundledPlugins {
    # The setup's plugin checkboxes are its statement about the plugins it ships: a selected one is
    # named in the configuration, an unselected one is not. Only entries that point at a managed
    # file are touched -- every other entry is the operator's and is left exactly as it is. This is
    # also what repairs an installation made before the plugin was bundled, whose list is empty:
    # an empty list stays empty, because the host deliberately loads nothing it was not told about.
    if (-not (Test-Path -LiteralPath $config)) { return }
    try { $parsed = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json }
    catch {
        # Unreadable is not ours to overwrite.
        Write-Warning "Leaving $config alone: $($_.Exception.Message)"
        return
    }

    $entries = New-Object System.Collections.ArrayList
    foreach ($entry in @($parsed.Plugins)) { if ($entry) { [void]$entries.Add($entry) } }

    $changed = $false
    foreach ($file in $managedFiles) {
        $key = ConvertTo-PluginKey ('plugins/' + $file)
        $hit = @($entries | Where-Object { (ConvertTo-PluginKey $_.AssemblyPath) -eq $key })
        $selected = $selectedFiles -contains $file
        if ($selected -and $hit.Count -eq 0) {
            # A partial deployment must not name an assembly that is not there: the host fails to
            # start at all when a configured plugin cannot be loaded.
            if (-not (Test-PluginPresent $file)) { continue }
            [void]$entries.Add((Get-PluginEntry $file))
            $changed = $true
        } elseif (-not $selected -and $hit.Count -gt 0) {
            foreach ($existing in $hit) { $entries.Remove($existing) }
            $changed = $true
        }
    }
    if (-not $changed) { return }
    $parsed | Add-Member -NotePropertyName Plugins -NotePropertyValue @($entries) -Force
    Save-Config ($parsed | ConvertTo-Json -Depth 20)
}

function Remove-ShimInstrumentation {
    # The TxF shim leaves Image File Execution Options values behind; each points at the launcher
    # the plugin deploys beside the machine's configuration. A value that survived the program
    # files would make the instrumented programs fail to start ("the debugger is missing"), which
    # is worse than the transaction error this shim exists to avoid. Only values naming this
    # product's launcher are removed; anyone else's value belongs to something else.
    $launcher = Join-Path $data 'txf-shim\PolyMount.TxFShim.exe'
    $root = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'
    Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue | ForEach-Object {
        $debugger = (Get-ItemProperty -LiteralPath $_.PSPath -Name Debugger -ErrorAction SilentlyContinue).Debugger
        if ($debugger -and [string]::Equals([string]$debugger, $launcher, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-ItemProperty -LiteralPath $_.PSPath -Name Debugger -ErrorAction SilentlyContinue
        }
    }
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
    # Everything else that runs out of the installation directory holds the files this setup has
    # to replace: an MCP server an agent started, a probe window, a worker whose supervisor is
    # already gone. Setup's own Restart Manager step runs after this one and turns a survivor into
    # "Setup was unable to automatically close all applications", so nothing that lives in this
    # directory is left running.
    #
    # Except the process that asked for this script: the uninstaller lives here too, and ending
    # it ends the uninstall half-way (it reports -1). The caller chain and any Inno uninstaller
    # are kept.
    $keep = New-Object System.Collections.Generic.HashSet[int]
    [void]$keep.Add($PID)
    $probe = $PID
    for ($hop = 0; $hop -lt 5; $hop++) {
        $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$probe" -ErrorAction SilentlyContinue
        if (-not $parent -or -not $parent.ParentProcessId) { break }
        $probe = [int]$parent.ParentProcessId
        [void]$keep.Add($probe)
    }
    foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
        $path = $null
        try { $path = $process.Path } catch { continue }
        if (-not $path) { continue }
        if ($keep.Contains($process.Id)) { continue }
        if ([IO.Path]::GetFileNameWithoutExtension($path) -like 'unins*') { continue }
        if (-not $path.StartsWith($InstallDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
        try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { }
    }
}
try {
    switch ($Action) {
        'Stop' { Stop-HostProcesses }
        'Check' {
            if (-not (Test-Path -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'WinFsp\bin\winfsp-x64.dll'))) { throw 'WinFsp is missing.' }
            if (-not (Test-Path -LiteralPath (Join-Path $InstallDirectory 'Service\PolyMount.Service.exe'))) { throw 'Service executable is missing.' }
            if (-not (Test-Path -LiteralPath (Join-Path $InstallDirectory 'Tray\platforms\qwindows.dll'))) { throw 'Qt Windows platform plugin is missing.' }
            # One entry per bundled plugin, as in the setup's table: a plugin that is copied but
            # never named in the configuration is installed and inert, which is not what the
            # wizard's checkboxes promised.
            foreach ($plugin in 'PolyMount.Plugin.Svn.dll', 'PolyMount.Plugin.Smb.dll', 'PolyMount.Plugin.TxFShim.dll') {
                if (-not (Test-Path -LiteralPath (Join-Path $InstallDirectory ('Service\plugins\' + $plugin)))) {
                    throw "The bundled plugin $plugin is missing."
                }
            }
            if (-not (Test-Path -LiteralPath (Join-Path $InstallDirectory 'Mcp\PolyMount.Mcp.exe'))) { throw 'The MCP server executable is missing.' }
        }
        'Install' {
            # The Setup calls Stop before it copies anything; an Install run by hand has to do the
            # same, or it writes the configuration underneath a host that is watching that file.
            Stop-HostProcesses
            New-Item -ItemType Directory -Path $data -Force | Out-Null
            if (Test-Path -LiteralPath $config) { Sync-BundledPlugins } else { Write-FreshConfig }
            Set-ControlUsers
            $exe = Join-Path $InstallDirectory 'Service\PolyMount.Service.exe'
            $binary = '"' + $exe + '" --config "' + $config + '"'
            # sc.exe is not used to set a binary path here: handed a binPath containing quotes from
            # PowerShell it answers 1639 (invalid command line) and creates nothing, because
            # PowerShell escapes the inner quotes in a way sc does not parse. The service provider
            # takes the string exactly as given, which is what the service control manager wants.
            if ($service) {
                $current = Get-CimInstance Win32_Service -Filter "Name='PolyMount'"
                $changed = Invoke-CimMethod -InputObject $current -MethodName Change -Arguments @{ PathName = $binary; StartMode = 'Automatic' }
                if ($changed.ReturnValue -ne 0) { throw "Failed to update service registration (service provider returned $($changed.ReturnValue))." }
                $account = $current.StartName
                if ($account -eq 'LocalSystem') { $account = 'NT AUTHORITY\SYSTEM' }
            } elseif ($UserAccount) {
                # Credentials never enter installer arguments, logs or files.
                $credential = Get-Credential -Message 'PolyMount service account (use your Windows account to reuse cached SVN credentials)'
                if (-not $credential) { throw 'Service account credentials were cancelled.' }
                New-Service -Name PolyMount -DisplayName 'PolyMount Virtual Mount Service' -BinaryPathName $binary -StartupType Automatic -Credential $credential | Out-Null
                $account = $credential.UserName
            } else {
                New-Service -Name PolyMount -DisplayName 'PolyMount Virtual Mount Service' -BinaryPathName $binary -StartupType Automatic | Out-Null
                # Created as LocalSystem and moved to LocalService: the provider cannot set an
                # account without a password, and LocalService needs none.
                $created = Get-CimInstance Win32_Service -Filter "Name='PolyMount'"
                $changed = Invoke-CimMethod -InputObject $created -MethodName Change -Arguments @{ StartName = 'NT AUTHORITY\LocalService' }
                if ($changed.ReturnValue -ne 0) { throw "Failed to set the service account (service provider returned $($changed.ReturnValue))." }
                $account = 'NT AUTHORITY\LOCAL SERVICE'
            }
            # The account is given access with icacls rather than Get-Acl/Set-Acl: those two live in
            # a module that Windows PowerShell failed to autoload on a build runner, and the install
            # stopped there -- after creating the service, so the service existed and had never been
            # started. icacls is an executable and cannot fail that way. Well-known accounts are
            # named by SID, which needs no lookup and no translation on a non-English system.
            $grantee = switch ($account) {
                'NT AUTHORITY\SYSTEM' { '*S-1-5-18' }
                'NT AUTHORITY\LOCAL SERVICE' { '*S-1-5-19' }
                default { '"' + $account + '"' }
            }
            & icacls.exe $data /grant ($grantee + ':(OI)(CI)M') /Q | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Failed to grant the service account access to '$data' (icacls exit $LASTEXITCODE)." }
            Protect-DataDirectory $account
            & sc.exe description PolyMount 'Layered virtual drives, on-demand file access.' | Out-Null
            Start-Service PolyMount
            (Get-Service PolyMount).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(30))
        }
        'Uninstall' {
            Stop-HostProcesses
            Remove-ShimInstrumentation
            if ($service) { & sc.exe delete PolyMount; if ($LASTEXITCODE -ne 0) { throw 'Failed to remove service.' } }
            # ProgramData configuration, credentials and provider data are intentionally kept.
        }
    }
    exit 0
} catch {
    # The setup runs this in a console window nobody is looking at, so a failure here reaches the
    # installer as a bare "service setup failed". The reason goes to a file the installer log, the
    # install test and a person all can read.
    $detail = (Get-Date).ToString('o') + ' ' + $Action + ': ' + ($_ | Out-String)
    foreach ($path in @((Join-Path $data 'install-error.log'), (Join-Path $env:TEMP 'polymount-install-host.log'))) {
        try { Add-Content -LiteralPath $path -Value $detail; break } catch { }
    }
    Write-Error $_ -ErrorAction Continue
    exit 1
}
