<#
.SYNOPSIS
  Drives PolyMount.Mcp.exe over stdio against a supervisor of its own.
.DESCRIPTION
  The MCP server is the control plane spoken by a process an agent starts, so what has to be
  proved is the wire: the handshake, the tool list, a call that reaches the service, and the
  errors a caller will actually hit. This starts its own supervisor on its own pipe with a
  disabled "dir" instance - nothing it touches belongs to the installed service - and talks to
  the MCP server the way a client does, one JSON line at a time.

  -Full enables the instance and waits for the mount to come up, so the write path
  (set_instance_enabled -> mount -> unmount) is exercised against WinFsp as well.
#>
[CmdletBinding()]
param(
    [string]$ServiceExecutable,
    [string]$McpExecutable,
    [switch]$Full
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $ServiceExecutable) {
    $ServiceExecutable = Join-Path $repo 'src\PolyMount.Service\bin\Debug\net10.0-windows\PolyMount.Service.exe'
}
if (-not $McpExecutable) {
    $McpExecutable = Join-Path $repo 'src\PolyMount.Mcp\bin\Debug\net10.0-windows\PolyMount.Mcp.exe'
}
$ServiceExecutable = (Resolve-Path -LiteralPath $ServiceExecutable).Path
$McpExecutable = (Resolve-Path -LiteralPath $McpExecutable).Path

$root = Join-Path $env:TEMP ('PolyMount-mcp-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'
$mount = Join-Path $root 'mount'
$store = Join-Path $root 'store'
New-Item -ItemType Directory -Path $source -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $source 'hello.txt'), 'from-source')

$pipe = 'PolyMount.McpTest'
$config = Join-Path $root 'test.json'
@{
    ControlPipe = $pipe
    Mounts      = @(@{
            Id         = [Guid]::NewGuid().ToString('N')
            Name       = 'McpSmoke'
            Enabled    = $false
            MountPoint = $mount
            StoreRoot  = $store
            Layers     = @(
                @{ Id = 'overlay'; Type = 'overlay'; Compression = $true },
                @{ Id = 'dir'; Type = 'dir'; Source = @{ Type = 'dir'; Path = $source } }
            )
        })
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $config -Encoding UTF8

function Send-Control([string]$command) {
    $client = New-Object IO.Pipes.NamedPipeClientStream('.', $pipe, [IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect(1000)
        $writer = New-Object IO.StreamWriter($client)
        $writer.AutoFlush = $true
        $writer.WriteLine((@{ Cmd = $command } | ConvertTo-Json -Compress))
        $reader = New-Object IO.StreamReader($client)
        return ($reader.ReadLine() | ConvertFrom-Json)
    }
    finally { $client.Dispose() }
}

# A supervisor answers as soon as it has a pipe, which is before its workers are up. What this
# test needs first is the supervisor itself.
function Wait-Supervisor {
    for ($i = 0; $i -lt 60; $i++) {
        try { if ((Send-Control 'status').Ok) { return } } catch { }
        Start-Sleep -Milliseconds 250
    }
    throw 'The test supervisor did not answer.'
}

$service = Start-Process -FilePath $ServiceExecutable -ArgumentList @('--config', ('"' + $config + '"')) -PassThru -WindowStyle Hidden
$clients = New-Object System.Collections.Generic.List[System.Diagnostics.Process]

function Start-Mcp([string[]]$extra) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $McpExecutable
    $info.Arguments = (@('--pipe', $pipe) + $extra) -join ' '
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $info
    $process.Start() | Out-Null
    $clients.Add($process)
    return $process
}

function Invoke-Mcp([System.Diagnostics.Process]$process) {
    $script:requestId++
    $message = @{
        jsonrpc = '2.0'
        id      = $script:requestId
        method  = 'initialize'
        params  = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'polymount-mcp-smoke'; version = '1' } }
    }
    $process.StandardInput.WriteLine(($message | ConvertTo-Json -Compress -Depth 20))
    $process.StandardInput.Flush()
    $response = Read-Response $process $script:requestId
    $process.StandardInput.WriteLine((@{ jsonrpc = '2.0'; method = 'notifications/initialized' } | ConvertTo-Json -Compress))
    $process.StandardInput.Flush()
    return $response
}

function Read-Response([System.Diagnostics.Process]$process, [int]$id) {
    while ($true) {
        $task = $process.StandardOutput.ReadLineAsync()
        if (-not $task.Wait(20000)) { throw 'The MCP server did not answer in time.' }
        if ($null -eq $task.Result) { throw 'The MCP server closed its output.' }
        $message = $task.Result | ConvertFrom-Json
        if ($message.id -eq $id) { return $message }
        if ($message.method) { continue }
        throw ("Unexpected MCP message: " + $task.Result)
    }
}

function Send-Mcp([System.Diagnostics.Process]$process, [string]$method, $params) {
    $script:requestId++
    $message = @{ jsonrpc = '2.0'; id = $script:requestId; method = $method; params = $params }
    $process.StandardInput.WriteLine(($message | ConvertTo-Json -Compress -Depth 20))
    $process.StandardInput.Flush()
    return Read-Response $process $script:requestId
}

function Call-Tool([System.Diagnostics.Process]$process, [string]$name, [hashtable]$arguments = @{}) {
    return (Send-Mcp $process 'tools/call' @{ name = $name; arguments = $arguments })
}

function Assert-Tool-Error($response, [string]$contains, [string]$what) {
    if (-not $response.result.isError) { throw "$what should have failed but did not." }
    $text = $response.result.content[0].text
    if ($text -notlike ('*' + $contains + '*')) { throw "$what failed with an unexpected message: $text" }
}

$script:requestId = 0
try {
    Wait-Supervisor

    $mcp = Start-Mcp @()
    $init = Invoke-Mcp $mcp
    if ($init.result.serverInfo.name -ne 'polymount') { throw "unexpected server name: $($init.result.serverInfo.name)" }

    $list = Send-Mcp $mcp 'tools/list' @{}
    $tools = @($list.result.tools)
    if ($tools.Count -ne 22) { throw "expected 22 tools, got $($tools.Count)" }
    $names = @($tools | ForEach-Object { $_.name })
    foreach ($required in @('list_instances', 'probe_path', 'save_config', 'shutdown_service')) {
        if ($names -notcontains $required) { throw "tools/list is missing $required" }
    }

    $instances = Call-Tool $mcp 'list_instances'
    if ($instances.result.isError) { throw ('list_instances failed: ' + $instances.result.content[0].text) }
    $rows = @((($instances.result.content[0].text) | ConvertFrom-Json).instances)
    if ($rows.Count -ne 1) { throw "expected one instance, got $($rows.Count)" }
    if ($rows[0].name -ne 'McpSmoke') { throw "unexpected instance: $($rows[0].name)" }
    if ($rows[0].enabled) { throw 'the test instance should start disabled' }

    $configDocument = Call-Tool $mcp 'get_config'
    if ($configDocument.result.isError) { throw ('get_config failed: ' + $configDocument.result.content[0].text) }
    $document = $configDocument.result.content[0].text | ConvertFrom-Json
    if ($document.Mounts[0].Name -ne 'McpSmoke') { throw 'get_config did not return the test instance' }

    $outside = Call-Tool $mcp 'probe_path' @{ path = 'C:\Windows\not-mounted.txt' }
    Assert-Tool-Error $outside 'no mount point contains' 'probe_path outside every mount'

    $missing = Call-Tool $mcp 'set_instance_enabled' @{ instance = 'McpSmoke' }
    Assert-Tool-Error $missing "missing required argument 'enabled'" 'set_instance_enabled without enabled'

    $readOnly = Start-Mcp @('--read-only')
    $readOnlyInit = Invoke-Mcp $readOnly
    if ($readOnlyInit.result.serverInfo.name -ne 'polymount') { throw 'the read-only server did not start' }
    $readOnlyTools = Send-Mcp $readOnly 'tools/list' @{}
    if (@($readOnlyTools.result.tools).Count -ne 8) { throw "expected 8 read-only tools, got $(@($readOnlyTools.result.tools).Count)" }

    if ($Full) {
        $enabled = Call-Tool $mcp 'set_instance_enabled' @{ instance = 'McpSmoke'; enabled = $true }
        if ($enabled.result.isError) { throw ('enabling failed: ' + $enabled.result.content[0].text) }

        $mounted = $false
        for ($i = 0; $i -lt 60 -and -not $mounted; $i++) {
            Start-Sleep -Milliseconds 300
            $status = Call-Tool $mcp 'get_instance' @{ instance = 'McpSmoke' }
            if ($status.result.isError) { continue }
            $row = $status.result.content[0].text | ConvertFrom-Json
            # A directory that merely exists is the placeholder an elevated mount creates
            # before it attaches the volume; the mount point itself is a reparse point.
            $isMountPoint = $false
            try { $isMountPoint = ([IO.File]::GetAttributes($mount) -band [IO.FileAttributes]::ReparsePoint) -ne 0 } catch {}
            $mounted = $row.mounted -and $isMountPoint
        }
        if (-not $mounted) { throw 'the instance did not mount through set_instance_enabled' }

        $probe = Call-Tool $mcp 'probe_path' @{ path = $mount }
        if ($probe.result.isError) { throw ('probe_path on a mounted path failed: ' + $probe.result.content[0].text) }

        $disabled = Call-Tool $mcp 'set_instance_enabled' @{ instance = 'McpSmoke'; enabled = $false }
        if ($disabled.result.isError) { throw ('disabling failed: ' + $disabled.result.content[0].text) }
    }

    Send-Control 'shutdown' | Out-Null
    $service.WaitForExit(15000) | Out-Null

    $down = Call-Tool $mcp 'list_instances'
    Assert-Tool-Error $down 'not reachable' 'list_instances with the service stopped'

    'MCP smoke passed: handshake, 22 tools (8 read-only), list/get/config, path errors, argument errors, service-down error.'
}
finally {
    foreach ($client in $clients) {
        try { if (-not $client.HasExited) { $client.Kill() } } catch { }
    }
    try { Send-Control 'shutdown' | Out-Null } catch { }
    if (-not $service.HasExited) { $service.WaitForExit(10000) | Out-Null }
    if (-not $service.HasExited) { $service.Kill() }
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
