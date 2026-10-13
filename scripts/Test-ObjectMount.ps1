[CmdletBinding()]
param([string]$ServiceExecutable)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP ('PolyMount-object-mount-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'; $mount = Join-Path $root 'mount'; $store = Join-Path $root 'store'
New-Item -ItemType Directory -Path (Join-Path $source 'old\sub') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $source 'old\sub\file.txt'),'source')
[IO.File]::WriteAllText((Join-Path $source 'deleted.txt'),'delete-me')
$config = Join-Path $root 'test.json'
@{ ControlPipe = 'PolyMount.ObjectSmoke'; Mounts = @(@{ Name='ObjectSmoke'; MountPoint=$mount; StoreRoot=$store; Layers=@(
    @{Id='overlay';Type='overlay';Compression=$true}, @{Id='dir';Type='dir';Source=@{Type='dir';Path=$source}}
)}) } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $config -Encoding UTF8
$process = Start-Process -FilePath $ServiceExecutable -ArgumentList @('--config',('"'+$config+'"')) -PassThru -WindowStyle Hidden
function Command([string]$cmd) {
    $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', 'PolyMount.ObjectSmoke', [IO.Pipes.PipeDirection]::InOut)
    try { $pipe.Connect(1000); $writer=New-Object IO.StreamWriter($pipe);$writer.AutoFlush=$true;$writer.WriteLine((@{Cmd=$cmd}|ConvertTo-Json -Compress));$reader=New-Object IO.StreamReader($pipe);return ($reader.ReadLine()|ConvertFrom-Json) }
    finally { $pipe.Dispose() }
}
try {
    $ready=$false
    for($i=0;$i -lt 30;$i++) { try { $response=Command 'status';if($response.Mounts[0].Mounted){$ready=$true;break} } catch {}; Start-Sleep -Milliseconds 300 }
    if(-not $ready){throw 'Test mount did not start'}
    [IO.File]::WriteAllText((Join-Path $mount 'new.txt'),'new-object')
    if([IO.File]::ReadAllText((Join-Path $mount 'new.txt')) -ne 'new-object'){throw 'New object read failed'}
    [IO.File]::Delete((Join-Path $mount 'deleted.txt'))
    if([IO.File]::Exists((Join-Path $mount 'deleted.txt'))){throw 'Deleted object reappeared'}
    [IO.Directory]::Move((Join-Path $mount 'old'),(Join-Path $mount 'renamed'))
    if([IO.File]::ReadAllText((Join-Path $mount 'renamed\sub\file.txt')) -ne 'source'){throw 'Nested directory rename failed'}
    if(-not [IO.File]::Exists((Join-Path $source 'deleted.txt'))){throw 'Read-only source was modified'}
    Command 'shutdown' | Out-Null
    $process.WaitForExit(10000) | Out-Null
    $process = Start-Process -FilePath $ServiceExecutable -ArgumentList @('--config',('"'+$config+'"')) -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2
    if([IO.File]::Exists((Join-Path $mount 'deleted.txt'))){throw 'Deletion was not persisted'}
    if([IO.File]::ReadAllText((Join-Path $mount 'new.txt')) -ne 'new-object'){throw 'Compressed object did not persist'}
    'Object mount smoke passed: create/read/delete/recursive rename/persistence/compression; source unchanged.'
} finally {
    try { Command 'shutdown' | Out-Null } catch {}
    if(-not $process.HasExited){$process.WaitForExit(10000)|Out-Null}
    if($process.HasExited){Remove-Item -LiteralPath $root -Recurse -Force}
}
