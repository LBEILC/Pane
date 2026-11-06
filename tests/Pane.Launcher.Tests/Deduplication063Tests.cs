using Pane.Services.Launcher;

static partial class Program
{
    static async Task Deduplication063Tests(){
        int before=checks;
        // 安装目标和参数取自 issue 28；快捷方式位置用虚构目录，不依赖本机安装。
        (string Name,string Id,string Exe,string First,string Second,bool LinksOnly)[] cases=[
            ("Google Chrome","Chrome",@"C:\Program Files\Google\Chrome\Application\chrome.exe","","--source-shortcut-location=start-menu",false),
            ("Microsoft Edge","MSEdge",@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe","","--source-shortcut-location=start-menu",false),
            ("Microsoft Edge Beta","MSEdgeBeta",@"C:\Program Files (x86)\Microsoft\Edge Beta\Application\msedge.exe","","--source-shortcut-location=start-menu",false),
            ("微信","",@"D:\Weixin\Weixin.exe","","--scene=startmenu",false),
            ("WPS Office","",@"D:\WPS Office\ksolaunch.exe","/prometheus /fromksolaunch /from=startmenu","/prometheus /fromksolaunch /from=desktop_shortcut",true),
            ("迅雷","",@"D:\Thunder\Program\ThunderStart.exe","-StartType:StartMenu","-StartType:DesktopIcon",true),
            ("UC浏览器","",@"D:\UC\UC浏览器\uc.exe","--launch-from=startmenu","--launch-from=desktopicon",true),
            ("夸克网盘","QuarkCloudDrive",@"D:\quark-cloud-drive\QuarkCloudDrive\quark_cloud_drive.exe","","--launch-from=startmenu --brand-clouddrive",false),
            ("迅游加速器","",@"D:\XunYouJiashuqi\xunyou\xunyou.exe","-e=1","-e=4",true)
        ];
        var all=new List<CatalogEntry>();
        foreach(var c in cases){
            var first=Entry053(c.Name,c.Id,c.Exe,c.LinksOnly?@"C:\fixture\first.lnk":@"shell:AppsFolder\"+c.Id,c.LinksOnly?1:0,c.First);
            var second=Entry053(c.Name,c.Id,c.Exe,@"C:\fixture\second.lnk",1,c.Second);
            CatalogEntry[] rows=c.Id=="Chrome"?[first,first with{Parsing=@"C:\fixture\user.lnk",Source=1},second,first with{Parsing=@"C:\fixture\desktop.lnk",Source=1}]:[first,second];
            all.AddRange(rows);
            var group=AppDeduplication.Resolve(rows).Single();
            Check(AppDeduplication.Groups(rows).Length==1&&group.Rows.Length==rows.Length,"063 合并全部来源 "+c.Name);
            Check(group.Rows[0]==first&&group.Rows[0].Args==c.First,"063 保留来源顺序及启动参数 "+c.Name);
            Check(group.StableId==first.LegacyId&&group.MergedIds.Contains(second.LegacyId),"063 053 规则旧 Id 归属 "+c.Name);
            await Migration053(group);
        }
        Check(AppDeduplication.Resolve(all).Length==9,"063 九组互不串组");
        var chrome=all[0];
        var normal=chrome with{Target="  "+chrome.Target.ToUpperInvariant()+"  ",Name=chrome.Name.ToLowerInvariant(),Args="different"};
        Check(AppDeduplication.Groups([chrome,normal]).Length==1,"063 路径空白及名称大小写规范化");
        string variable="PANE_TEST_063_TARGET";Environment.SetEnvironmentVariable(variable,chrome.Target);
        try{Check(AppDeduplication.Groups([chrome,normal with{Target="%"+variable+"%"}]).Length==1,"063 展开环境变量");}
        finally{Environment.SetEnvironmentVariable(variable,null);}
        var negatives=new[]{
            Entry053("Developer Command Prompt for VS","",@"C:\Windows\System32\cmd.exe",@"C:\fixture\vs.lnk",1,"/k vs.cmd"),
            Entry053("x64 Native Tools Command Prompt for VS","",@"C:\Windows\System32\cmd.exe",@"C:\fixture\vs64.lnk",1,"/k vcvars64.cmd"),
            Entry053("张三 - Chrome","",chrome.Target,@"C:\fixture\profile.lnk",1,"--profile-directory=Profile1"),
            Entry053("李四 - Chrome","",chrome.Target,@"C:\fixture\profile2.lnk",1,"--profile-directory=Profile2"),
            Entry053("设置","",@"D:\Chatterfly\FySetting.exe",@"C:\fixture\settings.lnk",1,"--page=settings"),
            Entry053("设置","",@"D:\Sogou\SGTool.exe",@"C:\fixture\sogou.lnk",1,"-line 0 -border --appid=config"),
            Entry053("设置","windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel","",@"shell:AppsFolder\settings"),
            Entry053("Xmind","net.xmind.vana.app",@"D:\Xmind\Xmind.exe",@"shell:AppsFolder\net.xmind.vana.app"),
            Entry053("Xmind","app.xmind.cn-3FF677E1_1gx73yeg4g4e6!App","",@"shell:AppsFolder\xmind-web")
        };
        var separated=AppDeduplication.Resolve(negatives);
        Check(separated.Length==negatives.Length,"063 命令行、配置、设置和 Xmind 反例全部保留");
        foreach(var g in separated)Check(g.StableId==g.Rows[0].LegacyId&&g.MergedIds.Length==0,"063 未合并 Id 不变 "+g.Rows[0].Name);
        Check(AppDeduplication.Groups([negatives[^1],negatives[^1] with{Args="different"}]).Length==2,"063 package 不参加新合并");
        Check(AppDeduplication.Groups([chrome with{Target=""},chrome with{Target="",Args="different"}]).Length==2,"063 shell 不参加新合并");
        // 053 前仅有 Shell 身份；053 补齐目标后生成另一组目标身份，两代均须指向一个保留项。
        var missing=Entry053("迁移应用","Migration.App","",@"shell:AppsFolder\Migration.App");
        var link=Entry053(missing.Name,missing.Id,@"D:\fixture\migration.exe",@"C:\fixture\migration.lnk",1);
        var parameterized=Entry053(missing.Name,"",link.Target,@"C:\fixture\migration-source.lnk",1,"--source=start-menu");
        var migrated=AppDeduplication.Resolve([missing,link,parameterized]).Single();
        Check(migrated.StableId==missing.LegacyId&&migrated.MergedIds.Contains(link.LegacyId)&&migrated.MergedIds.Contains(parameterized.LegacyId),"063 两代旧身份归属");
        foreach(string old in migrated.MergedIds)await Migration053(migrated with{MergedIds=[old]});
        // 旧枚举丢失参数时，同一个 LegacyId 只归属于一个新组；保留 053 生成的 Shell 回退 Id。
        var dev=chrome with{Name="Chrome 开发入口",Id="Dev",Args="--dev"};
        var plain=chrome with{Id="Plain"};
        var source=Entry053(chrome.Name,"",chrome.Target,@"C:\fixture\source.lnk",1,"--source=start-menu");
        var split=AppDeduplication.Resolve([dev,plain,source]);
        Check(split.Length==2&&split.Single(g=>g.Rows[0].Id=="Plain").StableId==AppDeduplication.StableId("Plain"),"063 保留 053 Shell 回退身份");
        var assigned=split.SelectMany(g=>g.MergedIds.Prepend(g.StableId)).ToArray();
        Check(assigned.Length==assigned.Distinct().Count(),"063 一个旧 Id 只有一个目的组");
        await Migration053(split.Single(g=>g.Rows[0].Id=="Plain"));
        var collapsed=AppDeduplication.Resolve([dev with{Name=chrome.Name},plain,source]).Single();
        string fallback=AppDeduplication.StableId("Plain");
        Check(collapsed.StableId==chrome.LegacyId&&collapsed.MergedIds.Contains(fallback),"063 被并掉的 053 Shell 回退 Id 也加入别名");
        await Migration053(collapsed with{MergedIds=[fallback]});
        string[] names=["Xmind","AIHOT — AI 行业动态聚合","QQ邮箱","DeepSeek"];
        string[] packages=["app.xmind.cn-3FF677E1","aihot.news-E2BB379F","wx.mail.qq.com-921465E4","chat.deepseek.com-A7E55CB8"];
        var table=new PinyinTable();
        for(int i=0;i<names.Length;i++){
            string id=packages[i]+"_publisher!App";
            string display=WindowsAppCatalog.DisplayName(names[i],id,true);
            Check(display==string.Format(Pane.Localization.Strings.Launcher_WebAppName,names[i]),"063 网页应用标注 "+names[i]);
            Check(table.Create(display).Match(names[i])!=null,"063 原名搜索 "+names[i]);
            Check(WindowsAppCatalog.DisplayName(names[i],id,false)==names[i],"063 非包应用不标注");
        }
        foreach(string id in new[]{"windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel","Microsoft.WindowsCalculator_8wekyb3d8bbwe!App","Microsoft.Windows.Photos_8wekyb3d8bbwe!App","Microsoft.Paint_8wekyb3d8bbwe!App","Microsoft.WindowsStore_8wekyb3d8bbwe!App","app.example-1234567_p!App","app.example-123456789_p!App","app.example-1234567G_p!App","example-12345678_p!App"})
            Check(WindowsAppCatalog.DisplayName("普通应用",id,true)=="普通应用","063 普通包和无效哈希不标注 "+id);
        Console.WriteLine($"063 assertions={checks-before};issueGroups=9;realExecutions=0");
    }
}
