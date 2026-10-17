[CmdletBinding()]
param(
    [string]$Setup,
    # Leaves the product installed and running when the test finishes, for a machine somebody is
    # about to use. CI always uninstalls, so each run starts from nothing.
    [switch]$KeepInstalled
)
$ErrorActionPreference = 'Stop'

# Installs a real setup on a machine that does not have the product, checks what an installation
# is supposed to leave behind, installs it again over itself to prove the overwrite path, and
# removes it. Everything it asserts is something a reader would notice: the service runs, the
# configuration names the bundled plugin, the supervisor answers with an SVN source, and the
# uninstaller takes the program away without taking the data.

$app = Join-Path $env:ProgramFiles 'PolyMount'
$data = Join-Path $env:ProgramData 'PolyMount'
$config = Join-Path $data 'config.json'
$failures = 0

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $script:failures += if ($ok) { 0 } else { 1 }
    Write-Host ("{0} {1}{2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $(if ($detail) { ": $detail" } else { '' }))
}
function IsAdmin { (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
function Invoke-Setup([string]$path, [string]$arguments) {
    $info = @{ FilePath = $path; ArgumentList = $arguments; Wait = $true; PassThru = $true }
    if (-not (IsAdmin)) { $info.Verb = 'RunAs' }
    $process = Start-Process @info
    if ($null -eq $process) { throw "the setup did not start: $path" }
    return $process.ExitCode
}
function Read-Config { Get-Content -LiteralPath $config -Raw | ConvertFrom-Json }
function Ask-Supervisor([string]$command) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'PolyMount.Control', [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(2000)
        $writer = New-Object System.IO.StreamWriter($pipe); $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($pipe)
        $writer.WriteLine((@{ Cmd = $command } | ConvertTo-Json -Compress))
        return $reader.ReadLine() | ConvertFrom-Json
    } finally { $pipe.Dispose() }
}
# The setup can return before the installation has finished: when it is not already elevated it
# starts an elevated copy of itself and the first process exits at once. Waiting for the service is
# what makes the checks read a finished installation rather than a snapshot taken mid-copy.
function Wait-Service([int]$seconds = 120) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $running = Get-Service -Name PolyMount -ErrorAction SilentlyContinue
        if ($running -and $running.Status -eq 'Running') { return $running }
        Start-Sleep -Milliseconds 500
    }
    return Get-Service -Name PolyMount -ErrorAction SilentlyContinue
}
function Wait-Supervisor([int]$seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try { $reply = Ask-Supervisor 'status'; if ($reply.Ok) { return $reply } } catch { }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

if (-not $Setup) { throw 'Pass -Setup with the installer to test.' }
$Setup = (Resolve-Path -LiteralPath $Setup).Path

Write-Host '=== before ==='
Check 'no service yet' ($null -eq (Get-Service -Name PolyMount -ErrorAction SilentlyContinue))
Check 'no install directory yet' (-not (Test-Path -LiteralPath $app))

Write-Host ''
Write-Host '=== install ==='
$setupLog = Join-Path $env:TEMP 'polymount-setup.log'
Remove-Item -LiteralPath $setupLog -Force -ErrorAction SilentlyContinue
$code = Invoke-Setup $Setup "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$setupLog`""
Check 'setup exited 0' ($code -eq 0) "exit $code"

$service = Wait-Service
Check 'service registered' ($null -ne $service)
Check 'service running' ($service.Status -eq 'Running') $(if ($service) { $service.Status } else { 'absent' })
$registered = Get-CimInstance Win32_Service -Filter "Name='PolyMount'" -ErrorAction SilentlyContinue
if ($registered) {
    # Started by the service control manager rather than left behind by the setup: a product whose
    # backend is a service says so here, with an automatic start type and a binary path that names
    # the installed executable and the configuration it reads.
    Check 'service starts automatically' ($registered.StartMode -eq 'Auto') $registered.StartMode
    Check 'service binary is the installed one' ($registered.PathName -like "*$app\Service\PolyMount.Service.exe*") $registered.PathName
    Check 'service passes the installed configuration' ($registered.PathName -like "*--config*") $registered.PathName
    Check 'service account' ($registered.StartName -ne '') $registered.StartName
} else { Check 'service registration readable' $false }

# When the service is missing, the installer's own log is the only thing that says why.
if ($null -eq (Get-Service -Name PolyMount -ErrorAction SilentlyContinue)) {
    Write-Host '--- sc.exe query PolyMount ---'
    & sc.exe query PolyMount 2>&1 | Write-Host
    if (Test-Path -LiteralPath $setupLog) {
        Write-Host "--- tail of $setupLog ---"
        Get-Content -LiteralPath $setupLog -Tail 40 | Write-Host
    } else { Write-Host "no setup log at $setupLog" }
}

$running = @(Get-Process -Name PolyMount.Service -ErrorAction SilentlyContinue)
Check 'exactly one backend process' ($running.Count -eq 1) "$($running.Count) process(es)"
if ($running.Count -eq 1) {
    Check 'it runs the installed executable' ($running[0].Path -like "$app\Service\*") $running[0].Path
    Check 'it runs in the service session' ($running[0].SessionId -eq 0) "session $($running[0].SessionId)"
}
foreach ($relative in 'Service\PolyMount.Service.exe', 'Tray\PolyMount.Tray.exe', 'Tray\platforms\qwindows.dll',
                        'Shell\PolyMount.Shell.dll', 'Service\plugins\PolyMount.Plugin.Svn.dll',
                        'installer\Install-Host.ps1', 'install.json', 'THIRD-PARTY-NOTICES.txt') {
    Check "installed $relative" (Test-Path -LiteralPath (Join-Path $app $relative))
}

$entry = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ItemProperty $_.PSPath } | Where-Object { $_.DisplayName -like 'PolyMount*' } | Select-Object -First 1
Check 'uninstall entry present' ($null -ne $entry) $(if ($entry) { $entry.DisplayVersion } else { 'missing' })

Check 'configuration written' (Test-Path -LiteralPath $config)
if (Test-Path -LiteralPath $config) {
    $plugins = @((Read-Config).Plugins)
    Check 'SVN plugin configured once' (($plugins.Count -eq 1) -and ($plugins[0].AssemblyPath -eq 'plugins/PolyMount.Plugin.Svn.dll')) "$($plugins.Count) plugin(s)"
}

$reply = Wait-Supervisor
Check 'supervisor answers' ($null -ne $reply)
if ($reply) {
    $configReply = Ask-Supervisor 'getConfig'
    Check 'SVN source is offered' ($configReply.SourceTypes -contains 'svn') ($configReply.SourceTypes -join ', ')
}

Write-Host ''
Write-Host '=== install over the top ==='
$marker = Join-Path $app 'overwrite-marker.txt'
Set-Content -LiteralPath $marker -Value 'first install' -Encoding ASCII
$parsed = Read-Config
$parsed | Add-Member -NotePropertyName KeepMe -NotePropertyValue 'from the first install' -Force
Set-Content -LiteralPath $config -Value ($parsed | ConvertTo-Json -Depth 20) -Encoding UTF8
# Proof that the second setup replaced the program files: a file it owns is tampered with, and has
# to come back as the setup wrote it. Timestamps cannot say this -- reinstalling the same version
# writes identical bytes with identical times.
$owned = Join-Path $app 'install.json'
$originalOwned = Get-Content -LiteralPath $owned -Raw
Set-Content -LiteralPath $owned -Value '{"tampered":true}' -Encoding UTF8

$code = Invoke-Setup $Setup "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$setupLog`""
Check 'second setup exited 0' ($code -eq 0) "exit $code"
$service = Wait-Service
Check 'the overwrite left the previous install in place' (Test-Path -LiteralPath $marker)
Check 'the program files were replaced' ((Get-Content -LiteralPath $owned -Raw) -eq $originalOwned)
Check 'still exactly one service, running' (($null -ne $service) -and ($service.Status -eq 'Running'))
$after = Read-Config
Check 'the configuration kept what was not the setup''s' ($after.KeepMe -eq 'from the first install')
Check 'the plugin is still named once' (@($after.Plugins).Count -eq 1) "$(@($after.Plugins).Count) plugin(s)"
$reply = Wait-Supervisor
Check 'supervisor still answers' ($null -ne $reply)

if (-not $KeepInstalled) {
    Write-Host ''
    Write-Host '=== uninstall ==='
    # The marker is the test's own file; Inno leaves a directory holding something it did not
    # install, so keeping it would make "the program directory is gone" fail for the wrong reason.
    Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue
    $uninstaller = Join-Path $app 'unins000.exe'
    Check 'uninstaller present' (Test-Path -LiteralPath $uninstaller)
    $code = Invoke-Setup $uninstaller '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
    Check 'uninstaller exited 0' ($code -eq 0) "exit $code"
    Check 'service removed' ($null -eq (Get-Service -Name PolyMount -ErrorAction SilentlyContinue))
    Check 'program directory removed' (-not (Test-Path -LiteralPath $app))
    Check 'configuration kept (a silent uninstall does not ask)' (Test-Path -LiteralPath $config)
    Check 'uninstall entry removed' ($null -eq (Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ItemProperty $_.PSPath } | Where-Object { $_.DisplayName -like 'PolyMount*' }))
    Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($failures -gt 0) { Write-Host "$failures check(s) failed"; exit 1 }
Write-Host 'Install test passed: clean install, overwrite, and uninstall behave as described.'
