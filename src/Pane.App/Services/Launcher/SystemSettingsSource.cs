using Pane.Contracts;
namespace Pane.Services.Launcher;

internal sealed record SystemSetting(string Key,string Title,string English,string[] Aliases,string Target,string[] Arguments,string RequiredFile,string Category,string Glyph="gear");
internal sealed class SystemSettingsSource:LauncherSource
{
    readonly (SystemSetting Setting,SearchName Name,SearchName[] Aliases)[] entries;
    readonly ILauncherExecutor executor;readonly ILaunchProcess process;readonly ILauncherEnvironment environment;readonly Action<string> used;
    public SystemSettingsSource(ILauncherExecutor executor,ILaunchProcess process,ILauncherEnvironment environment,Action<string> used):base("设置",2,"system:")
    {
        this.executor=executor;this.process=process;this.environment=environment;this.used=used;
        var table=new PinyinTable();
        entries=SystemSettingsCatalog.All.Where(e=>environment.FindProgram(e.RequiredFile)!=null).Select(e=>(e,table.Create(e.Title),e.Aliases.Append(e.English).Select(table.Create).ToArray())).ToArray();
    }
    // TODO(design): 设置组未规定条数，暂沿用既有列表来源的 3 条上限。
    public override int Limit(SourceContext c)=>3;
    public override Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext c,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(c.Query.Length==0)return Rows([]);
        var hits=new List<(SystemSetting Setting,NameMatch Match)>();
        foreach(var entry in entries){token.ThrowIfCancellationRequested();var match=entry.Name.Match(c.Query);foreach(var alias in entry.Aliases)if(alias.Match(c.Query) is {} other&&(match==null||other.Score>match.Score))match=new(other.Score,[]);if(match!=null)hits.Add((entry.Setting,match));}
        return Rows(hits.OrderByDescending(h=>h.Match.Score).ThenByDescending(h=>c.Usage.GetValueOrDefault("system:"+h.Setting.Key)?.Count??0).ThenByDescending(h=>c.Usage.GetValueOrDefault("system:"+h.Setting.Key)?.LastUsed??DateTimeOffset.MinValue).ThenBy(h=>h.Setting.Title,StringComparer.OrdinalIgnoreCase).Take(Limit(c)).Select(h=>new LauncherItem("system:"+h.Setting.Key,LauncherItemKind.Command,Group,h.Setting.Title,h.Setting.English,new(h.Setting.Glyph),h.Match.Spans)));
    }
    public override async Task ExecuteAsync(LauncherItem item,SourceContext c,CancellationToken token)
    {
        var entry=entries.First(e=>"system:"+e.Setting.Key==item.Id).Setting;
        if(entry.Arguments.Length==0)await executor.OpenComponentAsync(entry.Target.StartsWith("ms-settings:",StringComparison.Ordinal)?entry.Target:environment.FindProgram(entry.Target)!,token).ConfigureAwait(false);
        else await process.StartAsync(new(environment.FindProgram(entry.Target)!,entry.Arguments,environment.Home),token).ConfigureAwait(false);
        used(item.Id);
    }
}

internal static class SystemSettingsCatalog
{
    // 内置白名单，不读取 Windows 搜索内部索引。拼音由现有 Unicode 拼音表生成。
    public static readonly SystemSetting[] All=Build();
    static SystemSetting[] Build()
    {
        var result=new List<SystemSetting>();
        foreach(var line in Pages.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)){
            var p=line.Split('|');result.Add(new(p[0],p[1],p[2],p.Length>3?p[3].Split(','):[],"ms-settings:"+p[0],[],"SystemSettings.exe","设置页面"));
        }
        foreach(var line in Tools.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)){
            var p=line.Split('|');result.Add(new(p[0],p[1],p[2],p[3].Split(','),p[0],[],p[0],p[0].EndsWith(".cpl",StringComparison.Ordinal)?"控制面板":"管理工具"));
        }
        result.Add(new("environment","编辑系统环境变量","Environment Variables",["环境变量","系统变量","PATH"],"rundll32.exe",["sysdm.cpl,EditEnvironmentVariables"],"sysdm.cpl","管理工具"));
        result.Add(new("credentials","凭据管理器","Credential Manager",["Windows 凭据","凭证管理"],"control.exe",["/name","Microsoft.CredentialManager"],"vaultcli.dll","控制面板"));
        return result.ToArray();
    }
    const string Pages="""
display|显示设置|Display|分辨率,缩放,屏幕
sound|声音设置|Sound|音量,扬声器
apps-volume|音量混合器|Volume Mixer|应用音量
sound-devices|管理声音设备|Sound Devices|音频设备
sound-defaultinputproperties|默认麦克风|Default Microphone|录音
sound-defaultoutputproperties|默认音频输出|Default Audio Output|扬声器属性
notifications|通知设置|Notifications|消息通知
quiethours|专注助手|Focus Assist|免打扰
quietmomentsscheduled|自动免打扰时段|Scheduled Quiet Hours|专注时段
quietmomentspresentation|投影时免打扰|Presentation Quiet Hours|复制屏幕
quietmomentsgame|游戏时免打扰|Gaming Quiet Hours|游戏通知
powersleep|电源和睡眠|Power and Sleep|电源设置,休眠时间
storagesense|存储设置|Storage|磁盘空间
storagepolicies|存储感知|Storage Sense|自动清理
storagerecommendations|清理建议|Storage Recommendations|释放空间
disksandvolumes|磁盘和卷|Disks and Volumes|分区
savelocations|新内容保存位置|Save Locations|默认保存
multitasking|多任务处理|Multitasking|贴靠窗口
project|投影到此电脑|Projecting to This PC|无线显示
crossdevice|跨设备共享|Shared Experiences|附近共享
clipboard|系统剪贴板|Clipboard|剪贴板设置
about|系统信息设置|About|设备规格,电脑名称
nightlight|夜间模式|Night Light|护眼
bluetooth|蓝牙设置|Bluetooth|蓝牙设备
connecteddevices|连接的设备|Connected Devices|添加设备
printers|打印机和扫描仪|Printers and Scanners|打印设置
mousetouchpad|鼠标设置|Mouse|鼠标速度
typing|输入设置|Typing|键入
devicestyping-hwkbtextsuggestions|硬件键盘文本建议|Text Suggestions|输入建议
autoplay|自动播放|AutoPlay|移动设备
usb|USB 设置|USB|通用串行总线
camera|相机设置|Camera|摄像头
network-status|网络和 Internet|Network and Internet|网络设置
network-advancedsettings|高级网络设置|Advanced Network Settings|网络适配器
network-ethernet|以太网|Ethernet|有线网络
network-vpn|VPN 设置|VPN|虚拟专用网络
network-proxy|代理设置|Proxy|代理服务器
network-dialup|拨号设置|Dial Up|宽带连接
personalization|个性化设置|Personalization|外观
personalization-background|桌面背景|Background|壁纸
personalization-colors|颜色设置|Colors|主题色,深色模式
lockscreen|锁屏界面|Lock Screen|锁屏壁纸
themes|主题设置|Themes|桌面主题
fonts|字体设置|Fonts|字体管理
personalization-start|开始菜单设置|Start Menu|开始屏幕
personalization-start-places|开始菜单文件夹|Start Folders|开始快捷方式
taskbar|任务栏设置|Taskbar|任务栏图标
personalization-textinput|文本输入个性化|Text Input|输入主题
personalization-touchkeyboard|触摸键盘|Touch Keyboard|屏幕键盘外观
appsfeatures|安装的应用|Installed Apps|应用和功能,卸载应用
defaultapps|默认应用|Default Apps|文件关联,默认浏览器
optionalfeatures|可选功能|Optional Features|添加功能
appsforwebsites|网站应用|Apps for Websites|网站关联
videoplayback|视频播放|Video Playback|视频设置
startupapps|启动应用|Startup Apps|开机启动,自启动
yourinfo|账户信息|Your Info|头像,我的账户
emailandaccounts|电子邮件和账户|Email and Accounts|邮件账户
signinoptions|登录选项|Sign In Options|密码,PIN
signinoptions-dynamiclock|动态锁|Dynamic Lock|离开锁定
workplace|访问工作或学校|Work or School|工作账户
otherusers|其他用户|Other Users|添加用户
sync|同步设置|Sync Settings|Windows 备份
dateandtime|日期和时间|Date and Time|时区,校时
regionlanguage|语言和区域|Language and Region|语言包,输入法
regionformatting|区域格式|Regional Format|地区格式
keyboard|键盘语言|Keyboard Language|输入语言
keyboard-advanced|高级键盘设置|Advanced Keyboard|默认输入法
speech|语音设置|Speech|语音识别
gaming-gamebar|游戏栏|Game Bar|Xbox
gaming-gamedvr|游戏捕获|Game Captures|游戏录制,录屏
gaming-gamemode|游戏模式|Game Mode|游戏优化
easeofaccess-display|辅助显示|Accessibility Display|文字大小
easeofaccess-mousepointer|鼠标指针和触控|Mouse Pointer|指针大小
easeofaccess-cursor|文本光标|Text Cursor|光标指示器
easeofaccess-magnifier|放大镜设置|Magnifier Settings|屏幕放大
easeofaccess-colorfilter|颜色滤镜|Color Filters|色盲
easeofaccess-highcontrast|对比度主题|Contrast Themes|高对比度
easeofaccess-narrator|讲述人设置|Narrator Settings|屏幕阅读
easeofaccess-narrator-isautostartenabled|讲述人自动启动|Narrator Startup|自动朗读
easeofaccess-audio|辅助音频|Accessibility Audio|单声道
easeofaccess-closedcaptioning|字幕设置|Closed Captions|隐藏字幕
easeofaccess-speechrecognition|辅助语音|Speech Recognition|语音控制
easeofaccess-keyboard|辅助键盘|Accessibility Keyboard|粘滞键,筛选键
easeofaccess-mouse|辅助鼠标|Accessibility Mouse|鼠标键
easeofaccess-visualeffects|视觉效果|Visual Effects|透明效果,动画效果
privacy|隐私设置|Privacy|隐私和安全性
privacy-general|常规隐私|General Privacy|广告设置
privacy-speech|在线语音识别|Online Speech|语音隐私
privacy-speechtyping|墨迹书写和键入个性化|Inking and Typing|输入隐私
privacy-feedback|诊断和反馈|Diagnostics and Feedback|诊断数据
privacy-activityhistory|活动历史记录|Activity History|活动记录
privacy-location|位置权限|Location Privacy|定位
privacy-webcam|相机权限|Camera Privacy|摄像头权限
privacy-microphone|麦克风权限|Microphone Privacy|录音权限
privacy-voiceactivation|语音激活|Voice Activation|语音唤醒
privacy-notifications|通知权限|Notification Privacy|应用通知访问
privacy-accountinfo|账户信息权限|Account Info Privacy|账户访问
privacy-contacts|联系人权限|Contacts Privacy|通讯录
privacy-calendar|日历权限|Calendar Privacy|日程
privacy-phonecalls|电话权限|Phone Calls Privacy|拨打电话
privacy-callhistory|通话记录权限|Call History Privacy|通话历史
privacy-email|电子邮件权限|Email Privacy|邮件访问
privacy-tasks|任务权限|Tasks Privacy|任务访问
privacy-messaging|消息权限|Messaging Privacy|短信
privacy-radios|无线电权限|Radios Privacy|无线电控制
privacy-customdevices|其他设备权限|Other Devices Privacy|设备通信
privacy-appdiagnostics|应用诊断权限|App Diagnostics|诊断访问
privacy-automaticfiledownloads|自动文件下载|Automatic File Downloads|下载权限
privacy-documents|文档权限|Documents Privacy|文档访问
privacy-downloadsfolder|下载文件夹权限|Downloads Privacy|下载访问
privacy-pictures|图片权限|Pictures Privacy|照片访问
privacy-videos|视频权限|Videos Privacy|视频访问
privacy-musiclibrary|音乐库权限|Music Library Privacy|音乐访问
privacy-broadfilesystemaccess|文件系统权限|File System Privacy|文件访问
search|Windows 搜索设置|Windows Search|搜索设置
search-permissions|搜索权限|Search Permissions|搜索历史
search-moredetails|搜索 Windows|Searching Windows|搜索索引
activation|激活设置|Activation|产品密钥
delivery-optimization|传递优化|Delivery Optimization|更新下载
delivery-optimization-activity|传递优化活动监视器|Delivery Activity|下载统计
delivery-optimization-advanced|传递优化高级选项|Delivery Advanced Options|下载带宽
findmydevice|查找我的设备|Find My Device|设备位置
developers|开发者选项|For Developers|开发人员模式
recovery|恢复设置|Recovery|重置电脑
troubleshoot|疑难解答|Troubleshoot|故障排除
windowsdefender|Windows 安全中心|Windows Security|安全设置,杀毒
windowsupdate|Windows 更新|Windows Update|系统更新
windowsupdate-activehours|更新使用时段|Update Active Hours|活跃时间
windowsupdate-options|更新高级选项|Update Advanced Options|更新设置
windowsupdate-optionalupdates|可选更新|Optional Updates|驱动更新
windowsupdate-restartoptions|更新重启选项|Update Restart Options|重启计划
windowsupdate-history|更新历史记录|Update History|已安装更新
""";
    const string Tools="""
appwiz.cpl|程序和功能|Programs and Features|卸载程序
powercfg.cpl|电源选项|Power Options|电源计划
sysdm.cpl|系统属性|System Properties|高级系统设置
inetcpl.cpl|Internet 选项|Internet Options|浏览器选项
ncpa.cpl|网络连接|Network Connections|适配器设置
mmsys.cpl|声音控制面板|Sound Control Panel|播放设备,录制设备
main.cpl|鼠标属性|Mouse Properties|鼠标指针
timedate.cpl|日期和时间控制面板|Date and Time Control Panel|时间属性
intl.cpl|区域控制面板|Region Control Panel|区域选项
desk.cpl|显示控制面板|Display Control Panel|显示属性
firewall.cpl|Windows 防火墙|Windows Firewall|防火墙设置
joy.cpl|游戏控制器|Game Controllers|手柄
hdwwiz.cpl|设备管理器|Device Manager|硬件管理
telephon.cpl|电话和调制解调器|Phone and Modem|调制解调器
netplwiz.exe|用户账户|User Accounts|用户管理
credwiz.exe|凭据备份和还原|Credential Backup and Restore|凭据备份
gpedit.msc|编辑组策略|Group Policy Editor|组策略,gpedit
services.msc|服务|Services|系统服务
devmgmt.msc|设备管理控制台|Device Management Console|devmgmt
diskmgmt.msc|磁盘管理|Disk Management|磁盘分区
eventvwr.msc|事件查看器|Event Viewer|系统日志
taskschd.msc|任务计划程序|Task Scheduler|计划任务
compmgmt.msc|计算机管理|Computer Management|电脑管理
secpol.msc|本地安全策略|Local Security Policy|安全策略
lusrmgr.msc|本地用户和组|Local Users and Groups|用户组
wf.msc|高级安全 Windows 防火墙|Advanced Windows Firewall|防火墙规则
certmgr.msc|用户证书管理|User Certificates|证书管理
certlm.msc|计算机证书管理|Computer Certificates|本机证书
printmanagement.msc|打印管理|Print Management|打印服务器
regedit.exe|注册表编辑器|Registry Editor|注册表,regedit
msconfig.exe|系统配置|System Configuration|启动配置
perfmon.exe|性能监视器|Performance Monitor|性能计数器
resmon.exe|资源监视器|Resource Monitor|资源占用
taskmgr.exe|任务管理器|Task Manager|进程管理
msinfo32.exe|系统信息|System Information|硬件信息
cleanmgr.exe|磁盘清理|Disk Cleanup|清理磁盘
dfrgui.exe|优化驱动器|Optimize Drives|碎片整理
mdsched.exe|Windows 内存诊断|Memory Diagnostic|内存检测
dxdiag.exe|DirectX 诊断工具|DirectX Diagnostic|显卡信息
optionalfeatures.exe|Windows 功能|Windows Features|启用或关闭功能
odbcad32.exe|ODBC 数据源|ODBC Data Sources|数据库连接
charmap.exe|字符映射表|Character Map|特殊字符
""";
}
