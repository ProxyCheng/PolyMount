[CmdletBinding()]
<#
    What happens to a plugin somebody installed by hand when the product is updated or set up over
    the top of it.

    Two paths used to be able to lose one. The update script swept every file named like a plugin
    that the build did not produce; the setup never listed what it found, so the operator could
    neither keep nor remove one knowingly. Both are now decided from what each deployment can
    account for:

      * the update script deletes only what an earlier run of itself recorded, and says out loud
        what it found that it does not own;
      * the setup's plugin page lists those plugins after its own, checked, and the host script
        removes one only when the page said so.

    Runs anywhere, needs no administrator and touches no installation: the "installation" here is
    a directory under %TEMP% holding a config file and a plugins folder. The plugin it uses is
    real (tests/plugins/PolyMount.Plugin.Puppet) rather than a stand-in file, because the whole
    question is about a plugin that works.

    powershell -File scripts\Test-PluginUpgrade.ps1
#>
param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug', [string]$PuppetDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$bundled = @('PolyMount.Plugin.Svn.dll', 'PolyMount.Plugin.Smb.dll', 'PolyMount.Plugin.TxFShim.dll')

function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host "  ok    $what" }
    else { Write-Host "  FAIL  $what - $detail"; $script:failed = $true }
}

$script:failed = $false
$root = Join-Path $env:TEMP ('polymount-upgrade-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
try {
    # The puppet plugin, built here: the update script and the setup both look for its manifest
    # and move its files, so what they are given has to be what a real install would hold.
    if (-not $PuppetDirectory) {
        $project = Join-Path $repo 'tests\plugins\PolyMount.Plugin.Puppet\PolyMount.Plugin.Puppet.csproj'
        $PuppetDirectory = Join-Path $root 'puppet'
        & dotnet build $project -c $Configuration --nologo -v quiet -o $PuppetDirectory | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'the puppet plugin did not build' }
    }
    $puppetFiles = @('PolyMount.Plugin.Puppet.dll', 'PolyMount.Plugin.Puppet.plugin.json')

    # ---------------------------------------------------------------- the update path
    Write-Host 'update (Publish-Plugins.ps1):'
    $target = Join-Path $root 'service\plugins'
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($file in $puppetFiles) { Copy-Item (Join-Path $PuppetDirectory $file) $target -Force }
    # What an earlier run of the script installed and this build no longer produces.
    Set-Content -LiteralPath (Join-Path $target 'PolyMount.Plugin.Gone.dll') -Value 'removed from the tree' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $target '.installed') -Value @('PolyMount.Plugin.Gone.dll') -Encoding ascii
    # A dependency file that is not a plugin: it must not be mistaken for one.
    Set-Content -LiteralPath (Join-Path $target 'Some.Library.dll') -Value 'not a plugin' -Encoding ascii

    $output = & (Join-Path $PSScriptRoot 'Publish-Plugins.ps1') -Destination $target -Configuration $Configuration *>&1 | Out-String
    Check 'the plugin installed by hand is still there' (Test-Path (Join-Path $target 'PolyMount.Plugin.Puppet.dll'))
    Check 'its manifest is still there' (Test-Path (Join-Path $target 'PolyMount.Plugin.Puppet.plugin.json'))
    Check 'what this build no longer produces is removed' (-not (Test-Path (Join-Path $target 'PolyMount.Plugin.Gone.dll')))
    Check 'a library file is not mistaken for a plugin' (Test-Path (Join-Path $target 'Some.Library.dll'))
    Check 'and it says so rather than removing it silently' (
        ($output -match 'not part of this build') -and ($output -match 'PolyMount\.Plugin\.Puppet\.dll')) "output was: $output"
    $record = @(Get-Content -LiteralPath (Join-Path $target '.installed'))
    Check 'the record names only what this build owns' (($record -contains 'PolyMount.Plugin.Svn.dll') -and -not ($record -contains 'PolyMount.Plugin.Puppet.dll')) ($record -join ', ')

    # A deployment made before the record existed: it must be migrated, not swept. This is the
    # state every installation is in the first time the new script runs.
    $legacy = Join-Path $root 'legacy\plugins'
    New-Item -ItemType Directory -Path $legacy -Force | Out-Null
    foreach ($file in $puppetFiles) { Copy-Item (Join-Path $PuppetDirectory $file) $legacy -Force }
    & (Join-Path $PSScriptRoot 'Publish-Plugins.ps1') -Destination $legacy -Configuration $Configuration *>$null
    Check 'a deployment with no record loses nothing' (Test-Path (Join-Path $legacy 'PolyMount.Plugin.Puppet.dll'))
    Check 'and gets a record for next time' (Test-Path (Join-Path $legacy '.installed'))

    # ---------------------------------------------------------------- what the page is offered
    Write-Host 'discovery (Install-Host.ps1 -Action Discover):'
    $install = Join-Path $root 'app'
    $pluginDir = Join-Path $install 'Service\plugins'
    New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
    foreach ($file in $puppetFiles) { Copy-Item (Join-Path $PuppetDirectory $file) $pluginDir -Force }
    Set-Content -LiteralPath (Join-Path $pluginDir 'PolyMount.Plugin.Abstractions.dll') -Value 'the contract the host supplies' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $pluginDir 'Serilog.dll') -Value 'a dependency' -Encoding ascii
    $discovered = Join-Path $root 'extra-plugins.txt'
    & (Join-Path $repo 'installer\Install-Host.ps1') -Action Discover -InstallDirectory $install `
        -Managed ($bundled -join ';') -Output $discovered
    $lines = @(Get-Content -LiteralPath $discovered -Encoding UTF8 | Where-Object { $_.Trim() })
    Check 'the plugin it did not ship is listed once' ($lines.Count -eq 1) "got $($lines.Count): $($lines -join ' | ')"
    $parts = if ($lines.Count -eq 1) { $lines[0] -split "`t" } else { @() }
    # What the plugin calls itself, from its own manifest: the point is that the page shows the
    # plugin's name and not its file name, in both languages the setup speaks.
    $manifest = Get-Content -LiteralPath (Join-Path $PuppetDirectory 'PolyMount.Plugin.Puppet.plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Check 'the line names the file and both titles' (
        $parts.Count -eq 3 -and $parts[0] -eq 'PolyMount.Plugin.Puppet.dll' -and
        $parts[1] -eq $manifest.name.en -and $parts[2] -eq $manifest.name.'zh-CN') ($lines -join ' | ')
    Check 'the contract and the dependency are not plugins' (-not ($lines -join "`n" -match 'Abstractions|Serilog'))

    # ---------------------------------------------------------------- the answer it gets
    Write-Host 'the page''s answer (Install-Host.ps1 -Action ApplyPlugins):'
    $configPath = Join-Path $root 'config.json'
    function Write-Config {
        @{
            ControlPipe = 'PolyMount.Control'
            Plugins = @(
                @{ AssemblyPath = 'plugins/PolyMount.Plugin.Puppet.dll'; Enabled = $true },
                @{ AssemblyPath = 'plugins/PolyMount.Plugin.Svn.dll'; Enabled = $true }
            )
            Mounts = @()
        } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding utf8
    }
    function ConfigPaths { @((Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json).Plugins | ForEach-Object { $_.AssemblyPath }) }

    Write-Config
    & (Join-Path $repo 'installer\Install-Host.ps1') -Action ApplyPlugins -InstallDirectory $install `
        -Managed ($bundled -join ';') -Keep '' -ConfigPath $configPath | Out-Null
    Check 'a cleared box removes the assembly' (-not (Test-Path (Join-Path $pluginDir 'PolyMount.Plugin.Puppet.dll')))
    Check 'and its manifest' (-not (Test-Path (Join-Path $pluginDir 'PolyMount.Plugin.Puppet.plugin.json')))
    Check 'and its entry in the configuration' (-not ((ConfigPaths) -contains 'plugins/PolyMount.Plugin.Puppet.dll')) ((ConfigPaths) -join ', ')
    Check 'and leaves the plugin this setup ships alone' ((ConfigPaths) -contains 'plugins/PolyMount.Plugin.Svn.dll') ((ConfigPaths) -join ', ')

    foreach ($file in $puppetFiles) { Copy-Item (Join-Path $PuppetDirectory $file) $pluginDir -Force }
    Write-Config
    & (Join-Path $repo 'installer\Install-Host.ps1') -Action ApplyPlugins -InstallDirectory $install `
        -Managed ($bundled -join ';') -Keep 'PolyMount.Plugin.Puppet.dll' -ConfigPath $configPath | Out-Null
    Check 'a kept box keeps the assembly' (Test-Path (Join-Path $pluginDir 'PolyMount.Plugin.Puppet.dll'))
    Check 'and its entry' ((ConfigPaths) -contains 'plugins/PolyMount.Plugin.Puppet.dll') ((ConfigPaths) -join ', ')

    # A silent install never shows the page, so it never says anything about these plugins.
    foreach ($file in $puppetFiles) { if (-not (Test-Path (Join-Path $pluginDir $file))) { Copy-Item (Join-Path $PuppetDirectory $file) $pluginDir -Force } }
    Write-Config
    & (Join-Path $repo 'installer\Install-Host.ps1') -Action ApplyPlugins -InstallDirectory $install `
        -Managed ($bundled -join ';') -ConfigPath $configPath | Out-Null
    Check 'silence is not a decision' ((Test-Path (Join-Path $pluginDir 'PolyMount.Plugin.Puppet.dll')) -and
        ((ConfigPaths) -contains 'plugins/PolyMount.Plugin.Puppet.dll')) ((ConfigPaths) -join ', ')
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
if ($script:failed) { Write-Host 'Plugin upgrade checks failed.'; exit 1 }
Write-Host 'Plugin upgrade checks passed: the update keeps what it does not own, the page lists it, and only a cleared box removes it.'
