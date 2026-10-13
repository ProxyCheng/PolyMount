using System.Globalization;
using System.Text.Json;

namespace PolyMount.Tray;

/// <summary>Live UI translation with a per-user persisted preference.</summary>
internal static class I18n
{
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyMount", "ui.json");
    private static readonly Dictionary<string, (string Zh, string En)> Strings = Catalog.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim().Split('|')).ToDictionary(p => p[0], p => (p[1].Replace("\\n", "\n"), p[2].Replace("\\n", "\n")));
    public static string Language { get; private set; } = LoadLanguage();
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "en" ? "en-US" : "zh-CN");
    public static event Action? Changed;
    public static string T(string key) => Strings.TryGetValue(key, out var text) ? Language == "en" ? text.En : text.Zh : key;
    public static string F(string key, params object[] args) => string.Format(Culture, T(key), args);
    public static void Bind(Action<string> setter, string key)
    {
        setter(T(key));
        void Translate() => setter(T(key));
        Changed += Translate;
        if (setter.Target is QtSharp.Api.QObject target)
            target.Destroyed += _ => Changed -= Translate;
    }
    public static void SetLanguage(string language)
    {
        if (Language == language) return;
        Language = language == "en" ? "en" : "zh-CN";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { Language }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Changed?.Invoke();
    }
    private static string LoadLanguage()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return json.RootElement.GetProperty("Language").GetString() == "en" ? "en" : "zh-CN";
        }
        catch { return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en"; }
    }

    private const string Catalog = """
        dashboard.title|PolyMount · 挂载工作台|PolyMount · Mount workspace
        metric.layers|启用层数|Active layers
        metric.layers.hint|所有实例的有效层|Active providers across instances
        column.layers|启用层|Active layers
        layer.count|{0} 层|{0} layers
        mount.manage|管理实例与层|Manage instance & layers
        mount.performance|此实例的实时性能|Instance performance
        mount.delete|删除此实例|Delete instance
        editor.instance|管理实例 · {0}|Manage instance · {0}
        stats.instance|实时性能 · {0}|Performance · {0}
        instance.missing|此实例已不存在，请返回主界面|This instance no longer exists. Return to the workspace.
        layer.name|层名称|Layer name
        layer.enabled|启用此层|Enable this layer
        color.choose|选择颜色…|Choose color…
        cache.layer.clear|清空此层缓存|Clear this layer’s cache
        confirm.layer.cache|清空此层的运行时缓存？本地修改会保留。|Clear this layer’s runtime cache? Local modifications are kept.
        confirm.layer.head|将此 SVN 层的运行版本更新到 HEAD？本地修改会保留。|Update this SVN layer’s runtime revision to HEAD? Local modifications are kept.
        operation.done|层操作已完成|Layer operation completed
        overlay.clear|清除本层所有修改|Clear all changes in this layer
        confirm.overlay.clear|清除本层所有本地文件、目录和删除墓碑？下层内容将重新显现。此操作不可撤销，执行时会短暂卸载当前实例，请先关闭相关文件。|Clear every local file, folder and deletion tombstone in this layer? Lower content will become visible again. This cannot be undone. The instance is briefly unmounted; close its files first.
        layer.operation.saveFirst|请先保存并应用配置，再操作对应运行层。|Save and apply the configuration before operating on the runtime layer.
        stats.layerShare|逐层读取命中占比|Read share by layer
        stats.series.count|{0} 个层|{0} layers
        stats.share.definition|命中占比 = 该层提供的成功读取 / 全部成功读取；无读取时显示 0%。|Read share = successful reads served by this layer / all successful reads; 0% when idle.
        brand.subtitle|虚拟磁盘 · 按需访问|Virtual drives. On demand.
        workspace|工作空间|WORKSPACE
        overview|挂载概览|Mount overview
        config|配置管理|Configuration
        stats|实时性能|Live performance
        service.connection|服务连接|SERVICE CONNECTION
        service.start|启动服务|Start service
        service.stop|停止服务|Stop service
        language|界面语言|LANGUAGE
        tray.keepRunning|关闭窗口后仍在托盘运行|Keeps running in the tray
        dashboard.heading|挂载工作台|Mount workspace
        dashboard.subtitle|连接数据源，让文件像本地一样触手可及。|Connect your sources. Access files as if they were local.
        refresh|刷新|Refresh
        refresh.tip|窗口打开时每 10 秒自动刷新|Automatically refreshes every 10 seconds while open
        mount.new|新建挂载|New mount
        metric.total|挂载实例|Mount instances
        metric.total.hint|所有已配置的工作空间|All configured workspaces
        metric.active|已挂载|Mounted
        metric.active.hint|可在资源管理器中访问|Available in File Explorer
        metric.issues|需要关注|Needs attention
        metric.issues.hint|存在错误的挂载实例|Instances reporting errors
        metric.cache|磁盘缓存|Disk cache
        metric.cache.hint|所有实例的缓存用量|Combined cache usage
        mount.instances|挂载实例|Mount instances
        search.placeholder|搜索实例名称或挂载路径…|Search instance name or mount path…
        mount.list.hint|选择实例查看详情 · 双击已挂载实例打开目录|Select for details · Double-click a mounted instance to open
        detail|实例详情|INSTANCE DETAILS
        mount|挂载|Mount
        unmount|卸载|Unmount
        open.folder|打开目录|Open folder
        mount.location|挂载位置|MOUNT LOCATION
        source|数据源|Source
        revision.current|当前版本|CURRENT REVISION
        layers.heading|PROVIDER 链 · 上层优先|PROVIDER CHAIN · TOP WINS
        layers.inspect|查看根目录分层|Inspect root layers
        cache.space|缓存空间|CACHE SPACE
        cache.clear|清空此实例缓存|Clear instance cache
        revision.head|更新到 SVN HEAD|Update to SVN HEAD
        revision.placeholder|指定 revision|Revision number
        revision.pin|固定版本|Pin revision
        mount.edit|编辑此实例|Edit instance
        dashboard.footer|本地写入由可写层接管 · 远端按需读取|Writes go to the writable layer · Remote files load on demand
        connecting|正在连接 PolyMount 服务…|Connecting to PolyMount service…
        refreshing|正在刷新状态…|Refreshing status…
        working|正在处理，请稍候…|Working, please wait…
        working.short|处理中…|Working…
        refreshed|状态已更新|Status updated
        connected|服务已连接，状态同步正常|Service connected. Status is up to date.
        connection.failed|无法连接服务，可在左侧启动服务。|Service unavailable. Start it from the sidebar.
        operation.failed|操作失败：|Operation failed:
        offline.retry|连接已断开 · 自动重试中|Disconnected · Retrying automatically
        updated|上次更新 {0} · 每 10 秒刷新|Updated {0} · Refreshes every 10s
        auto.refresh|自动刷新 · 10 秒|Auto-refresh · 10 seconds
        filter.all|全部状态|All states
        state.mounted|已挂载|Mounted
        state.unmounted|未挂载|Unmounted
        state.error|有错误|Error
        state.disabled|已禁用|Disabled
        service.online|服务已连接|Service connected
        service.offline|服务未连接|Service offline
        column.instance|实例 / 挂载路径|Instance / mount path
        state|状态|State
        cache.usage|缓存用量|Cache usage
        cache.none|无需缓存|No cache
        list.count|{0} / {1} 个实例|{0} of {1} instances
        last.known|上次记录|Last known
        empty.offline|尚未连接服务\n启动服务后，这里会显示挂载实例。|Service is offline\nStart the service to see your mounts.
        empty.new|还没有挂载实例\n点击「新建挂载」，连接第一个数据源。|No mounts yet\nChoose New mount to connect your first source.
        empty.search|没有匹配的实例\n试试其他关键词或「全部状态」。|No matching mounts\nTry another search or select All states.
        detail.select|选择一个挂载实例|Select a mount
        detail.hint|状态、层链和操作会显示在这里|View its status, layers and actions here
        detail.offline|服务离线 · 显示上次状态|Offline · Last known state
        revision.unknown|尚未获取版本|Revision not available
        revision.local|本地目录 · 直接映射|Local directory · Direct mapping
        writable|可写|Writable
        readonly|只读|Read-only
        cache.summary|{0} / {1} · {2:N0} 个文件|{0} / {1} · {2:N0} files
        cache.local|本地源直接读取，不经过磁盘缓存|Local sources are read directly without disk caching
        instance.error|实例错误|Instance error
        disabled.tip|此实例已禁用，请在配置中启用|Enable this instance in configuration first
        selected.tip|仅操作当前选中的实例|Applies only to the selected instance
        confirm.unmount|卸载「{0}」后，{1} 将无法访问。\n请先关闭正在使用此目录的文件。|Unmounting “{0}” makes {1} unavailable.\nClose files using this directory first.
        confirm.cache|清空「{0}」的 {1} 缓存？\n本地修改会保留，后续读取将从源重新加载。|Clear {1} of cache for “{0}”?\nLocal changes are kept. Files will reload from the source.
        confirm.head|将「{0}」更新到 SVN 最新版本？\n远端文件视图将改变，本地修改会保留。|Update “{0}” to SVN HEAD?\nThe remote view changes. Local modifications are kept.
        confirm.pin|将「{0}」切换到 r{1}？\n远端文件视图将改变，本地修改会保留。|Switch “{0}” to r{1}?\nThe remote view changes. Local modifications are kept.
        confirm.service|服务停止后，所有挂载目录都将无法访问。\n请先关闭使用挂载文件的应用。|Stopping the service makes every mount unavailable.\nClose applications using mounted files first.
        confirm|确认|Confirm
        cancel|取消|Cancel
        unmount.done|实例已卸载|Instance unmounted
        mount.done|实例已挂载，现在可以打开目录|Instance mounted. You can now open its folder.
        cache.done|缓存已清空|Cache cleared
        revision.done|远端版本已更新|Remote revision updated
        revision.invalid|请输入正整数 revision，例如 795709。|Enter a positive revision number, such as 795709.
        folder.failed|无法打开目录：|Unable to open folder:
        tray.open|打开 Dashboard|Open dashboard
        tray.shell|资源管理器右键菜单|Explorer context menu
        tray.exit|退出 PolyMount|Exit PolyMount
        tray.message|PolyMount 正在运行。点击托盘图标打开工作台。|PolyMount is running. Click the tray icon to open your workspace.
        service.already|服务已在运行|Service is already running
        service.starting|服务启动中…|Service is starting…
        service.startRequested|已请求启动服务|Service start requested
        service.notRunning|服务未运行|Service is not running
        service.stopping|服务停止中…|Service is stopping…
        editor.title|PolyMount · 层配置|PolyMount · Layer configuration
        editor.heading|构建你的挂载空间|Build your mount workspace
        editor.subtitle|管理此实例的层与参数。禁用层会跳过解析，配置与数据保留。|Configure this instance’s layers. Disabled layers are skipped; settings and data are kept.
        name|实例名称|Instance name
        store.root|存储目录|Store directory
        enabled|启用此实例|Enable this instance
        remove|删除|Delete
        save|保存并应用|Save and apply
        saved|配置已保存，挂载已重建|Configuration saved. Mounts rebuilt.
        saved.errors|配置已保存，但部分实例挂载失败：|Configuration saved, but some mounts failed:
        load.failed|加载失败：|Load failed:
        save.failed|保存失败：|Save failed:
        validation.failed|配置校验失败：|Validation failed:
        editor.loaded|已加载 {0} 个挂载实例|Loaded {0} mount instances
        layer.stack|层栈 · 拖动调整优先级|Layer stack · Drag to reorder
        layer.hint|上层覆盖下层 · 写入落在第一个可写层|Upper layers win · Writes go to the first writable layer
        layer.add|添加层|Add layer
        layer.remove|删除层|Delete layer
        layer.choose|选择要添加的层类型|Choose the layer type
        layer.parameters|层参数|LAYER PARAMETERS
        layer.select|选择一层以编辑参数|Select a layer to configure
        layer.overlay|本地修改层|Local overlay
        layer.cache|LRU 磁盘缓存|LRU disk cache
        layer.svn|SVN 仓库|SVN repository
        layer.dir|本地目录|Local directory
        layer.overlay.hint|保存本地改动并遮蔽远端删除项|Stores local changes and deletion tombstones
        layer.cache.hint|缓存下层文件，按 LRU 自动淘汰|Caches lower files with automatic LRU eviction
        layer.svn.hint|按需读取指定版本的 SVN 文件|Reads SVN files on demand at a selected revision
        layer.dir.hint|将本地目录作为数据源|Uses a local directory as a source
        layer.id|层标识（固定）|Layer ID (fixed)
        layer.storage|层数据目录（留空使用默认）|Layer data folder (blank for default)
        cache.limit|缓存容量 (MB)|Cache capacity (MB)
        svn.repository|Repository 地址|Repository URL
        svn.pin|固定 revision（0 = HEAD）|Pinned revision (0 = HEAD)
        svn.trust|证书信任规则|Certificate trust failures
        svn.user|用户名|Username
        svn.password|密码（留空使用已缓存凭据）|Password (blank to use cached credentials)
        dir.path|本地目录路径|Local directory path
        dir.writable|允许写入源目录|Allow writes to the source directory
        layer.color|层颜色（#RRGGBB）|Layer color (#RRGGBB)
        preset|预设|Preset
        preset.svn|SVN + 缓存 + 本地修改|SVN + cache + overlay
        preset.dir|本地目录 + 本地修改|Local directory + overlay
        preset.confirm|套用预设将替换当前实例的层配置，继续吗？|Applying a preset replaces this instance’s layer configuration. Continue?
        remove.mount.confirm|删除此挂载实例的配置？磁盘上的层数据会保留。|Delete this mount configuration? Layer data on disk is kept.
        remove.layer.confirm|从层栈移除此层？磁盘上的数据会保留。|Remove this layer from the stack? Data on disk is kept.
        unsaved|有未保存的修改|Unsaved changes
        layer.required|层类型与顺序由你决定。无可写层时为只读；禁用层保留配置。|Choose any layers and order. Without writable providers the mount is read-only. Disabled layers keep their settings.
        stats.title|PolyMount · 实时性能|PolyMount · Live performance
        stats.heading|实时性能|Live performance
        stats.subtitle|每秒采样 · 最近 60 秒趋势 · 按挂载实例查看|Sampled every second · Last 60 seconds · Per-mount view
        stats.all|全部挂载|All mounts
        stats.pause|暂停|Pause
        stats.resume|继续|Resume
        stats.reads|读取请求|Read requests
        stats.hit|缓存命中率|Cache hit rate
        stats.latency|读取延迟|Read latency
        stats.bytes|解析文件量|Resolved file sizes
        stats.layer|逐层性能 · 最近 1 秒|Layer performance · Last second
        stats.live|实时更新中 · 每秒采样|Live · Sampling every second
        stats.paused|已暂停采样|Sampling paused
        stats.noData|暂无读取活动|No read activity
        stats.gap|采样中断，等待重新连接|Sampling interrupted. Waiting to reconnect.
        stats.history|60 秒前|60 seconds ago
        stats.now|现在|Now
        hits|读取次数|Reads
        share|占比 %|Share %
        avg|平均 ms|Average ms
        max|最大 ms|Maximum ms
        bytes|字节数|Bytes
        layer|层|Layer
        probe.title|PolyMount · 文件分层|PolyMount · File layers
        probe.legend|绿=生效 · 琥珀=被覆盖 · 红=墓碑 · 灰=未命中|Green: effective · Amber: shadowed · Red: tombstone · Gray: not found
        probe.restore|恢复本地墓碑|Restore tombstone
        probe.hide|墓碑遮蔽|Hide with tombstone
        probe.drop|丢弃本地改动|Discard local changes
        probe.evict|清除此文件缓存|Evict file cache
        probe.failed|探测失败：|Probe failed:
        probe.effective|生效层：|Effective layer:
        probe.invisible|不可见（墓碑或无生效层）|Not visible (tombstoned or missing)
        probe.deleted|墓碑|Tombstone
        probe.shadowed|被覆盖|Shadowed
        probe.unknown|未命中|Unknown
        probe.directory|目录|Directory
        probe.fallthrough|此层无条目，继续向下读取|No entry; falls through
        """;
}
