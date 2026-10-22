<#
.SYNOPSIS
  Mounts a folder that lives on another machine, served over SMB, and reads it through the mount.
.DESCRIPTION
  The remote-folder source is proved end to end: a worker process loads PolyMount.Plugin.Smb, a
  layer maps a UNC path, and a client reads and writes the mount while the bytes come from the
  share rather than from a local directory.

  It needs a share it may read. By default it uses this machine's own administrative share
  (\\localhost\C$) and maps a temporary folder through it, which exercises the whole path --
  session, listing, open, generation -- without asking anybody to configure a share. Pass -Unc
  to use a real one. When no share is reachable the script says so and exits 0: a build agent
  that refuses administrative shares must not look like a broken layer.

  Entirely self-contained: its own configuration, its own store, and a mount point under %TEMP%.
  Nothing it touches belongs to the installed service.
#>
[CmdletBinding()]
param(
    [string]$ServiceExecutable,
    [string]$PluginAssembly,
    [string]$Unc
)

$ErrorActionPreference = 'Stop'
if (-not $ServiceExecutable) {
    $ServiceExecutable = Join-Path $PSScriptRoot '..\src\PolyMount.Service\bin\Debug\net10.0-windows\PolyMount.Service.exe'
}
if (-not $PluginAssembly) {
    $PluginAssembly = Join-Path $PSScriptRoot '..\src\plugins\PolyMount.Plugin.Smb\bin\Debug\net10.0-windows\PolyMount.Plugin.Smb.dll'
}
$ServiceExecutable = (Resolve-Path -LiteralPath $ServiceExecutable).Path
$PluginAssembly = (Resolve-Path -LiteralPath $PluginAssembly).Path

$root = Join-Path $env:TEMP ('PolyMount-smb-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'
$mount = Join-Path $root 'mount'
$store = Join-Path $root 'store'
New-Item -ItemType Directory -Path $source -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $source 'sub') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $source 'hello.txt'), 'from-the-share')
[IO.File]::WriteAllText((Join-Path $source 'sub\nested.txt'), 'nested-through-the-share')

function Get-Unc([string]$local) {
    $root = [IO.Path]::GetPathRoot($local)
    if (-not $root -or $root.Length -lt 2 -or $root[1] -ne ':') { return $null }
    return '\\localhost\' + $root[0] + '$' + $local.Substring(2)
}

$uncSource = if ($Unc) { $Unc } else { Get-Unc $source }
if (-not $uncSource) { throw "Cannot derive a UNC path from $source; pass -Unc" }
if (-not [IO.Directory]::Exists($uncSource)) {
    "Skipped: no reachable share at $uncSource (pass -Unc to point at one)."
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    return
}

$id = [Guid]::NewGuid().ToString('N')
$pipeBase = 'PolyMount.SmbTest'
$pipeName = "$pipeBase.$id"
$config = Join-Path $root 'test.json'
@{
    ControlPipe = $pipeBase
    Plugins = @(@{ AssemblyPath = $PluginAssembly; Enabled = $true })
    Mounts = @(@{
        Id = $id; Name = 'SmbSmoke'; MountPoint = $mount; StoreRoot = $store
        Layers = @(
            @{ Id = 'warmup'; Type = 'warmup' },
            @{ Id = 'overlay'; Type = 'overlay' },
            @{ Id = 'smb'; Type = 'smb'; Source = @{ Type = 'smb'; Path = $uncSource } }
        )
    })
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $config -Encoding UTF8

function Send([string]$cmd, [hashtable]$fields = @{}) {
    $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(1000)
        $writer = New-Object IO.StreamWriter($pipe); $writer.AutoFlush = $true
        $reader = New-Object IO.StreamReader($pipe)
        $writer.WriteLine(((@{ Cmd = $cmd } + $fields) | ConvertTo-Json -Compress))
        return ($reader.ReadLine() | ConvertFrom-Json)
    }
    finally { $pipe.Dispose() }
}

$process = $null
try {
    $process = Start-Process -FilePath $ServiceExecutable `
        -ArgumentList @('--config', ('"' + $config + '"'), '--instance', $id) -PassThru -WindowStyle Hidden

    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        if ($process.HasExited) { throw "The worker exited at once with code $($process.ExitCode)" }
        try { $status = Send 'status'; if ($status.Mounts[0].Mounted) { $ready = $true; break } } catch { }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'The worker did not mount within 10 seconds' }
    if ($status.Mounts[0].Error) { throw "The worker reported an error: $($status.Mounts[0].Error)" }

    # Reads come from the share, at the root and one level down.
    if ([IO.File]::ReadAllText((Join-Path $mount 'hello.txt')) -ne 'from-the-share') { throw 'The file on the share did not read through the mount' }
    if ([IO.File]::ReadAllText((Join-Path $mount 'sub\nested.txt')) -ne 'nested-through-the-share') { throw 'A nested file on the share did not read through the mount' }
    $names = @(Get-ChildItem -LiteralPath $mount | ForEach-Object { $_.Name })
    if ($names -notcontains 'hello.txt' -or $names -notcontains 'sub') { throw "The mount listed $($names -join ', ') instead of the share's children" }

    # Writes land in the overlay: the share is read-only to this layer.
    [IO.File]::WriteAllText((Join-Path $mount 'local.txt'), 'written-through-the-mount')
    if ([IO.File]::ReadAllText((Join-Path $mount 'local.txt')) -ne 'written-through-the-mount') { throw 'A file written through the mount did not read back' }
    if ([IO.File]::Exists((Join-Path $source 'local.txt'))) { throw 'A write reached the source share; the layer is supposed to be read-only' }

    # A deletion is a local one: the share still has its file.
    Remove-Item -LiteralPath (Join-Path $mount 'hello.txt')
    if (-not [IO.File]::Exists((Join-Path $source 'hello.txt'))) { throw 'A delete reached the source share' }
    if ([IO.File]::Exists((Join-Path $mount 'hello.txt'))) { throw 'A deleted file is still listed by the mount' }

    $probe = Send 'probe' @{ Path = (Join-Path $mount 'sub\nested.txt') }
    if (-not $probe.Ok -or -not $probe.Probe.Found) { throw "Probing a file on the share failed: $($probe.Error)" }
    # The plugin is what answers for the source: the worker reports the types it loaded with the
    # configuration answers, not with a polled status.
    if ($probe.SourceTypes -notcontains 'smb') { throw "The worker did not load the smb source: $($probe.SourceTypes -join ', ')" }

    $describedSource = Send 'smb.source'
    if (-not $describedSource.Ok) { throw "The plugin's source command failed: $($describedSource.Error)" }
    $described = $describedSource.Result | ConvertFrom-Json
    if ($described.layers.Count -ne 1) { throw "The plugin described $($described.layers.Count) layers instead of one" }
    if ($described.layers[0].path -ne $uncSource) { throw "The plugin reported '$($described.layers[0].path)' instead of '$uncSource'" }
    if (-not $described.layers[0].connected) { throw 'The plugin reported no session' }
    if (-not $described.layers[0].generation) { throw 'The plugin reported no generation' }

    if ((Send 'shutdown').Ok -ne $true) { throw 'The worker refused to be told to stop' }
    if (-not $process.WaitForExit(15000)) { throw 'The worker did not exit within 15 seconds of being told to stop' }
    if ($process.ExitCode -ne 0) { throw "The worker exited with code $($process.ExitCode)" }

    "Remote folder smoke passed: mapped $uncSource, listed and read through the mount, kept writes and deletes local, and reported its session."
}
finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit(10000) | Out-Null }
    # The mount point is a WinFsp junction: removing it with rmdir takes the link and not the store.
    if (Test-Path -LiteralPath $mount) { cmd /c "rmdir `"$mount`"" 2>$null | Out-Null }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
