<#
.SYNOPSIS
  Publish PolyMount, restart only what actually changed. Invoked by ..\Update.bat.
.DESCRIPTION
  Preserves config.json and layer data. Supports interactive hosting and the installed
  PolyMount Windows service. Run as administrator when updating an installed service.

  Nothing running is stopped until it is known that it has to be: the sources the deployed
  service and tray were built from are recorded next to them, and a run that finds them
  unchanged leaves every process - the supervisor, the instance workers behind their mounts,
  and the tray with whatever windows the reader has open - exactly as it found them. A build
  that changed only the tray restarts the tray alone. -Force restarts both whatever the
  comparison says; -TrayOnly never touches the service, comparison included.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$PipeName = 'PolyMount.Control',
    # Publish and restart the tray alone: the service is not stopped, not published and not
    # compared. For a change that is the tray's own, this is what the comparison would decide
    # anyway - asking for it saves the service's publish as well.
    [switch]$TrayOnly,
    # Restart both even if nothing that would be published has changed.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
# The build tools answer in the system's language, and a console whose code page does not match
# turns those messages into noise. Both halves are pinned here so a log reads the same on every
# machine: the SDK and MSBuild speak English, and the console decodes what it is given as UTF-8.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:VSLANG = '1033'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$repo = Split-Path $PSScriptRoot -Parent
$serviceProject = Join-Path $repo 'src\PolyMount.Service\PolyMount.Service.csproj'
$trayProject = Join-Path $repo 'src\PolyMount.Tray\PolyMount.Tray.csproj'
$mcpProject = Join-Path $repo 'src\PolyMount.Mcp\PolyMount.Mcp.csproj'
$serviceDir = Join-Path $repo "src\PolyMount.Service\bin\$Configuration\net10.0-windows"
$trayDir = Join-Path $repo "src\PolyMount.Tray\bin\$Configuration\net10.0-windows"
$mcpDir = Join-Path $repo "src\PolyMount.Mcp\bin\$Configuration\net10.0-windows"
$serviceExe = Join-Path $serviceDir 'PolyMount.Service.exe'
$trayExe = Join-Path $trayDir 'PolyMount.Tray.exe'
$mcpExe = Join-Path $mcpDir 'PolyMount.Mcp.exe'
# What the two deployments are built from. A file added, removed or edited anywhere under these
# is a deployment; a build that comes out the same is not, and neither is a file touched and put
# back.
$backendRoots = @('src\PolyMount.Core', 'src\PolyMount.Service', 'src\plugins')
$trayRoots = @('src\PolyMount.Tray')
# The MCP server is built from Core and its own sources. It is pulled up on demand by an agent,
# so there is no running process to compare against.
$mcpRoots = @('src\PolyMount.Core', 'src\PolyMount.Mcp')
$installed = Get-Service -Name 'PolyMount' -ErrorAction SilentlyContinue

function Send-Control([string]$Command, [string]$TargetPipe = $PipeName) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $TargetPipe, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(1000)
        $writer = New-Object System.IO.StreamWriter($pipe, (New-Object System.Text.UTF8Encoding($false)))
        $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($pipe, [System.Text.Encoding]::UTF8)
        $writer.WriteLine((@{ Cmd = $Command } | ConvertTo-Json -Compress))
        $reply = $reader.ReadLineAsync()
        if (-not $reply.Wait(3000)) { throw 'Control pipe response timed out.' }
        $response = $reply.Result | ConvertFrom-Json
        if (-not $response.Ok) { throw $response.Error }
        return $response
    }
    finally { $pipe.Dispose() }
}

# The state of the sources a deployment would be built from: every file under the given roots,
# its path and the hash of its contents, in one hash. Deciding on this rather than on a build is
# what makes a no-op run possible at all - a running process holds its own folder open, so
# publishing beside itself to be compared only works while nothing has changed, which is exactly
# the case that does not need deciding.
function Set-ControlUsers([string]$ConfigPath) {
    # The control plane's allowlist, seeded from whoever deploys: this script runs in an
    # interactive session, and that is the one thing a service started in session 0 cannot learn
    # about itself. An installation whose list is already there keeps it, and an account already
    # on it is not added twice.
    if (-not (Test-Path -LiteralPath $ConfigPath)) { return }
    try {
        $parsed = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $sid = $identity.User.Value
        $users = @()
        foreach ($entry in @($parsed.ControlUsers)) { if ($entry -and $entry.Sid) { $users += $entry } }
        if ($users | Where-Object { $_.Sid -eq $sid }) { return }
        $users += [pscustomobject]@{ Sid = $sid; Name = $identity.Name }
        $parsed | Add-Member -NotePropertyName ControlUsers -NotePropertyValue @($users) -Force
        $json = $parsed | ConvertTo-Json -Depth 20
        $temp = $ConfigPath + '.tmp'
        [System.IO.File]::WriteAllText($temp, $json, (New-Object System.Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $temp -Destination $ConfigPath -Force
        Write-Host "  added $($identity.Name) to the configuration's ControlUsers" -ForegroundColor DarkGray
    } catch {
        Write-Warning "The configuration's ControlUsers was left as it is: $($_.Exception.Message)"
    }
}

function Get-SourceState([string[]]$Roots) {
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($root in $Roots) {
        $full = Join-Path $repo $root
        if (-not (Test-Path -LiteralPath $full)) { continue }
        foreach ($file in (Get-ChildItem -LiteralPath $full -Recurse -File |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object FullName)) {
            $lines.Add(("{0}|{1}" -f $file.FullName.Substring($repo.Length),
                (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash))
        }
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        # As is, the sources are the state; the hash only has to be comparable with itself.
        $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))
        return (($bytes | ForEach-Object { $_.ToString('x2') }) -join '')
    }
    finally { $sha.Dispose() }
}

# What the deployment in that folder was built from, as written when it was deployed: absent
# means nothing has been deployed here by this script, which is a deployment to do.
function Get-DeployedState([string]$Directory) {
    $manifest = Join-Path $Directory '.deployed'
    if (-not (Test-Path -LiteralPath $manifest)) { return $null }
    return (Get-Content -LiteralPath $manifest -Raw).Trim()
}

function Set-DeployedState([string]$Directory, [string]$State) {
    Set-Content -LiteralPath (Join-Path $Directory '.deployed') -Value $State -Encoding ascii
}

function Publish-Project([string]$Project, [string]$Destination) {
    if (-not (Test-Path -LiteralPath (Split-Path $Destination -Parent))) {
        New-Item -ItemType Directory -Path (Split-Path $Destination -Parent) -Force | Out-Null
    }
    & dotnet publish $Project -c $Configuration --self-contained false -o $Destination --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $Project" }
}

function Start-Supervisor {
    if ($installed) { Start-Service -Name 'PolyMount' }
    else { Start-Process -FilePath $serviceExe -WorkingDirectory $serviceDir -WindowStyle Hidden | Out-Null }
}

function Start-Tray {
    Start-Process -FilePath $trayExe -WorkingDirectory $trayDir | Out-Null
}

function Stop-Supervisor {
    $running = @(Get-Process -Name 'PolyMount.Service' -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }
    try { Send-Control 'shutdown' | Out-Null }
    catch {
        if ($installed) { Stop-Service -Name 'PolyMount' }
        else { throw "Service did not accept graceful shutdown ($($_.Exception.Message)); update cancelled." }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((Get-Process -Name 'PolyMount.Service' -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (Get-Process -Name 'PolyMount.Service' -ErrorAction SilentlyContinue) {
        throw 'Service did not exit within 30 seconds; refusing to overwrite locked binaries.'
    }
}

function Stop-Tray {
    Get-Process -Name 'PolyMount.Tray' -ErrorAction SilentlyContinue | Stop-Process
    Start-Sleep -Milliseconds 500
}

# Waits for the supervisor and every instance worker to answer, and prints what is mounted.
function Wait-ForMounts {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $pending = @()
    $ready = $false
    $response = $null
    $lastError = 'no answer from the control pipe'
    do {
        try {
            # Two questions, because they are two processes: the supervisor knows which instance is
            # running and where it is served, and the instance's own worker is the one that knows
            # whether its mount came up. Nothing is forwarded between them.
            $response = Send-Control 'status'
            $instances = @(Send-Control 'instances').Instances
            $pending = @($response.Mounts | Where-Object { $_.Enabled -and -not $_.Mounted })
            foreach ($instance in $instances) {
                $worker = Send-Control 'status' $instance.Pipe
                $mount = @($worker.Mounts | Where-Object { $_.Name -eq $instance.Name })
                if ($mount.Count -ne 1) { $pending += $instance; $lastError = "instance '$($instance.Name)' answered with no status for itself" }
                elseif (-not $mount[0].Mounted -or $mount[0].Error) { $pending += $mount[0]; $lastError = "instance '$($instance.Name)': $($mount[0].Error)" }
                else { $lastError = 'all instances answered' }
            }
            $ready = $pending.Count -eq 0
        }
        catch { $ready = $false; $pending = @(); $lastError = $_.Exception.Message }
        if (-not $ready) { Start-Sleep -Milliseconds 500 }
    } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
    if (-not $response) { throw "Service did not become ready within 60 seconds: $lastError. Check its log." }
    if (-not $ready) { throw "Service is responding, but mounts did not become ready within 60 seconds: $lastError. Check its log." }
    foreach ($mount in $response.Mounts) {
        if ($mount.Error) { Write-Warning "Mount '$($mount.Name)': $($mount.Error)" }
        else { Write-Host "  $($mount.Name): mounted=$($mount.Mounted), $($mount.MountPoint), worker pid $($mount.WorkerPid)" }
    }
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
        throw 'An installed Windows service was detected. Run Update.bat as administrator.'
    }
}

function ServiceRunning { return [bool](Get-Process -Name 'PolyMount.Service' -ErrorAction SilentlyContinue) }
function TrayRunning { return [bool](Get-Process -Name 'PolyMount.Tray' -ErrorAction SilentlyContinue) }

try {
    # Perform preflight before stopping a running instance.
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK was not found in PATH.' }
    if (-not (Test-Path -LiteralPath $serviceProject) -or -not (Test-Path -LiteralPath $trayProject)) {
        throw "PolyMount projects were not found under $repo"
    }
    if ($installed) {
        # Which build the installed service runs from is a question the comparison cannot answer:
        # its files would look deployed-and-different for the wrong configuration and the publish
        # would land beside them.
        $registration = Get-CimInstance Win32_Service -Filter "Name='PolyMount'"
        $registeredPath = $registration.PathName.Trim().Trim('"')
        if (-not [string]::Equals($registeredPath, $serviceExe, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Installed service points to $registeredPath. Select its matching build configuration instead of $Configuration."
        }
    }

    if ($TrayOnly) {
        # Nothing here touches the service: it is not stopped, not published and not asked
        # anything. The tray is the only process whose files this run replaces.
        Write-Host '[1/2] Stopping the tray (the service keeps running)...'
        Stop-Tray
        Write-Host '[2/2] Publishing the tray...'
        try {
            Publish-Project $trayProject $trayDir
        }
        catch {
            # A publish that fails leaves the tray stopped, and what it failed on is usually somebody
            # else's half-written work in a tree two people share: the reader is left with no tray at
            # all and no way to see what the error was about. The build already in the folder is a
            # working tray, so it goes back in the air before the error is let through.
            Write-Warning "Publishing failed. Starting the tray again from the build already in $trayDir."
            if (Test-Path -LiteralPath $trayExe) { Start-Tray }
            throw
        }
        if (-not (Test-Path -LiteralPath $trayExe)) { throw 'Published tray executable was not found.' }
        Start-Tray
        Set-DeployedState $trayDir ("{0} {1}" -f $Configuration, (Get-SourceState $trayRoots))
        try {
            $status = Send-Control 'status'
            Write-Host ("Tray-only update complete. The service answered with {0} instance(s) (unchanged)." -f @($status.Mounts).Count) -ForegroundColor Green
        }
        catch {
            Write-Warning "Tray-only update complete, but the service did not answer ($($_.Exception.Message)). Start it, or run Update.bat without -TrayOnly."
        }
        exit 0
    }

    Write-Host "[1/3] Comparing the sources with what is deployed ($Configuration)..."
    $backendState = "{0} {1}" -f $Configuration, (Get-SourceState $backendRoots)
    $trayState = "{0} {1}" -f $Configuration, (Get-SourceState $trayRoots)
    $mcpState = "{0} {1}" -f $Configuration, (Get-SourceState $mcpRoots)
    $backendChanged = $Force -or -not (Test-Path -LiteralPath $serviceExe) -or (Get-DeployedState $serviceDir) -ne $backendState
    $trayChanged = $Force -or -not (Test-Path -LiteralPath $trayExe) -or (Get-DeployedState $trayDir) -ne $trayState
    $mcpChanged = $Force -or -not (Test-Path -LiteralPath $mcpExe) -or (Get-DeployedState $mcpDir) -ne $mcpState
    # A process that is not running is work to do whatever the sources say: what is deployed is
    # not what is being served. The MCP server is not that kind of process - an agent starts one
    # when it needs it and it exits with the session - so it is compared on its sources alone.
    if (-not (ServiceRunning)) { $backendChanged = $true }
    if (-not (TrayRunning)) { $trayChanged = $true }
    Write-Host ("  service, core and plugins: {0}" -f $(if ($backendChanged) { 'to deploy' } else { 'unchanged, left running' }))
    Write-Host ("  tray:                      {0}" -f $(if ($trayChanged) { 'to deploy' } else { 'unchanged, left running' }))
    Write-Host ("  MCP server:                {0}" -f $(if ($mcpChanged) { 'to publish' } else { 'unchanged' }))
    if (-not $backendChanged -and -not $trayChanged -and -not $mcpChanged) {
        Write-Host 'Nothing to deploy: every process is already running what the sources build.' -ForegroundColor Green
        exit 0
    }
    if ($backendChanged -and $installed) { Assert-Administrator }

    Write-Host '[2/3] Publishing and restarting what changed...'
    if ($mcpChanged) {
        # Published before anything is stopped: an agent session can hold the exe open, and that
        # failure must not leave the service stopped. A session that is holding it is asked about
        # up front instead of being discovered through MSBuild's ten copy retries: those take a
        # second each, fill the log with the same warning ten times, and end in the same skip.
        Write-Host '  publishing the MCP server (nothing to restart)...'
        $agents = @(Get-Process -Name 'PolyMount.Mcp' -ErrorAction SilentlyContinue)
        if ($agents.Count -gt 0) {
            Write-Host ("  the MCP server is in use by {0} agent process(es): left as it is." -f $agents.Count)
            Write-Host '  close the session using PolyMount.Mcp, then run this again to update it.'
        }
        else {
            try {
                Publish-Project $mcpProject $mcpDir
                if (-not (Test-Path -LiteralPath $mcpExe)) { throw 'Published MCP executable was not found.' }
                Set-DeployedState $mcpDir $mcpState
            }
            catch {
                Write-Warning ("  the MCP server was not updated: {0}" -f $_.Exception.Message)
                Write-Warning '  close the agent session using PolyMount.Mcp, then run this again.'
            }
        }
    }
    if ($backendChanged) {
        # The supervisor and its workers are stopped together: the workers are the service's own
        # children, and the files being replaced are held open by the processes that loaded them.
        Write-Host '  stopping the service (its instances are remounted when it comes back)...'
        Stop-Supervisor
        Publish-Project $serviceProject $serviceDir
        & (Join-Path $PSScriptRoot 'Publish-Plugins.ps1') -Destination (Join-Path $serviceDir 'plugins') -Configuration $Configuration
        if (-not (Test-Path -LiteralPath $serviceExe)) { throw 'Published service executable was not found.' }
        Set-ControlUsers (Join-Path $serviceDir 'config.json')
    }
    if ($trayChanged) {
        Write-Host '  stopping the tray...'
        Stop-Tray
        Publish-Project $trayProject $trayDir
        if (-not (Test-Path -LiteralPath $trayExe)) { throw 'Published tray executable was not found.' }
    }

    Write-Host '[3/3] Starting what was restarted...'
    if ($backendChanged) {
        Start-Supervisor
        Wait-ForMounts
        # Written once the deployment is serving, so a run that failed halfway is deployed again
        # rather than taken for done.
        Set-DeployedState $serviceDir $backendState
    }
    if ($trayChanged) {
        Start-Tray
        Set-DeployedState $trayDir $trayState
    }
    Write-Host 'Update complete.' -ForegroundColor Green
    exit 0
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
