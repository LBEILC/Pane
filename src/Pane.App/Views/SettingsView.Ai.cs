// 设置窗口：AI（设计负责人）。从 SettingsView.xaml.cs 按分区拆出（2026-11-06），外壳和公共状态在主文件。
using Pane.Localization;
using System.Reflection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pane.Contracts;
using Pane.DesignData;
using Pane.Hosting;
using Pane.Settings;
using Pane.Views.Controls;
using Windows.Foundation;
using G = Pane.Gestures;

namespace Pane.Views;

public sealed partial class SettingsView
{
    // ================= AI =================

    // 联网操作（拉模型、测试、登录）的状态：只在本次打开设置窗口期间保留
    IReadOnlyList<AiModel>? aiModels;
    string? aiBusy;                           // "models" / "test" / "signin" / "proxy"
    string? aiLast;                           // aiResult 是哪一项的结果（测试代理的结果只显示在代理那一行）
    bool proxyCustomPending;                  // 选了「自定义」还没填地址
    bool focusEndpoint;                       // 刚添加自定义服务商：这次渲染把焦点给地址框（只一次）
    bool deleteEndpointArmed;                 // 「删除这个服务商」点过一次，3 秒内再点才删
    (bool Ok, string Text)? aiResult;
    string? aiFieldError;
    CancellationTokenSource? signIn;

    void RenderAi()
    {
        var a = app.Store.Current.Ai;   // AI 总开关（issue 8）挪到「能力」页（055）
        var setup = app.AiSetup;
        bool api = a.Provider == AiProvider.Api;
        var rows = new List<FrameworkElement>
        {
            Row(Strings.Settings_Ai_Provider, null, Seg([("Api", Strings.Settings_Ai_ApiProvider), ("ChatGpt", Strings.Settings_Ai_ChatGptProvider)], a.Provider.ToString(), v =>
            {
                SaveAi(x => x with { Provider = Enum.Parse<AiProvider>(v) });
                aiModels = null; aiResult = null;
                Later(0);
            })),
        };
        if (api)
        {
            rows.Add(VendorRow(a));
            rows.AddRange(EndpointRows(a));
            rows.Add(KeyRow(setup));
        }
        else rows.AddRange(AccountRows(setup));
        if (api || setup.Account != null)
        {
            rows.Add(ModelRow(a, api));
            var test = Button(aiBusy == "test" ? Strings.Settings_Ai_Testing : Strings.Common_Test, null, () => _ = RunAi("test", async t => { await setup.TestAsync(t); return Strings.Settings_Ai_Connected; }));
            Enable(test, aiBusy == null);
            rows.Add(aiResult is { } r && aiLast != "proxy" ? Row(Strings.Settings_Ai_TestConnection, null, Status(r.Ok, r.Text), test) : Row(Strings.Settings_Ai_TestConnection, Strings.Settings_Ai_TestConnectionDescription, test));
        }
        if (aiFieldError != null) rows.Add(Row(Strings.Common_NotSaved, null, Status(false, aiFieldError)));
        if (api && AiPresets.Find(a.CurrentEndpoint.Id) is null) rows.Add(DeleteEndpointRow(a));
        // 思考程度（042，用户提）：ChatGPT 的推理模型等支持；不支持的模型服务自动去掉这个参数
        rows.Add(Row(Strings.Settings_Ai_Reasoning, a.Reasoning == AiReasoningEffort.Default ? Strings.Settings_Ai_DefaultReasoningDescription : Strings.Settings_Ai_ReasoningDescription,
            Seg([("Default", Strings.Common_Default), ("Low", Strings.Settings_Ai_ReasoningLow), ("Medium", Strings.Settings_Ai_ReasoningMedium), ("High", Strings.Settings_Ai_ReasoningHigh)], a.Reasoning.ToString(),
                v => { SaveAi(x => x with { Reasoning = Enum.Parse<AiReasoningEffort>(v) }); Later(); })));
        Group(Strings.Settings_Ai_Service, rows.ToArray());

        RenderAiActions(a);

        // 超级面板自动翻译：没配 AI 时开关灰掉（2026-10-30）
        bool configured = app.Ai.Status != AiStatus.NotConfigured;
        var autoSwitch = Switch(a.AutoTranslateSelection, v => SaveAi(x => x with { AutoTranslateSelection = v }));
        autoSwitch.IsEnabled = configured;
        autoSwitch.Opacity = configured ? 1 : 0.4;
        Group(Strings.Settings_Ai_Translation,
            Row(Strings.Settings_Ai_TargetLanguage, a.TranslateTarget == "auto" ? Strings.Settings_Ai_AutoLanguageDescription : null,
                Seg([("auto", Strings.Common_Auto), ("zh", Strings.Translate_LanguageChinese), ("en", Strings.Translate_LanguageEnglish), ("ja", Strings.Translate_LanguageJapanese)], a.TranslateTarget,
                    v => SaveAi(x => x with { TranslateTarget = v }))),
            TranslationModelRow(a),
            Row(Strings.Settings_Ai_AutoTranslate, configured ? Strings.Settings_Ai_AutoTranslateDescription : Strings.Settings_Ai_ConfigureFirst, autoSwitch));
        // 代理（042）：跟随系统 / 不走代理 / 自定义（http、socks5）；选「自定义」后填了地址才算数
        var mode = proxyCustomPending ? AiProxyMode.Custom : a.Proxy;
        var network = new List<FrameworkElement>
        {
            Row(Strings.Settings_Ai_Proxy, mode switch
                {
                    AiProxyMode.None => Strings.Settings_Ai_DirectDescription,
                    AiProxyMode.Custom => Strings.Settings_Ai_CustomProxyDescription,
                    _ => Strings.Settings_Ai_SystemProxyDescription,
                },
                Seg([("System", Strings.Settings_FollowSystem), ("None", Strings.Settings_Ai_Direct), ("Custom", Strings.Settings_Ai_CustomProxy)], mode.ToString(), v =>
                {
                    var m = Enum.Parse<AiProxyMode>(v);
                    proxyCustomPending = m == AiProxyMode.Custom && a.ProxyUrl == null;
                    if (!proxyCustomPending) SaveAi(x => x with { ProxyMode = m });
                    if (aiLast == "proxy") aiResult = null;
                    Later();
                })),
        };
        if (mode == AiProxyMode.Custom)
            network.Add(Row(Strings.Settings_Ai_ProxyAddress, Strings.Settings_Ai_ProxyPlaceholder,
                CommitField("http://127.0.0.1:7890", 260, a.ProxyUrl ?? "", v =>
                {
                    if (v.Length == 0) return Strings.Settings_Ai_ProxyRequired;
                    if (!AiSettings.ValidProxy(v)) return Strings.Settings_Ai_ProxyInvalid;
                    var error = Try(() => SaveAi(x => x with { ProxyUrl = v, ProxyMode = AiProxyMode.Custom }));
                    if (error == null) proxyCustomPending = false;
                    return error;
                })));
        var proxyTest = Button(aiBusy == "proxy" ? Strings.Settings_Ai_Testing : Strings.Common_Test, null, () => _ = RunAi("proxy", async t => { await app.AiSetup.TestProxyAsync(t); return Strings.Settings_Ai_ProxyConnected; }));
        Enable(proxyTest, aiBusy == null && !proxyCustomPending);
        network.Add(aiResult is { } pr && aiLast == "proxy" ? Row(Strings.Settings_Ai_TestProxy, null, Status(pr.Ok, pr.Text), proxyTest) : Row(Strings.Settings_Ai_TestProxy, Strings.Settings_Ai_TestProxyDescription, proxyTest));
        Group(Strings.Settings_Ai_Network, network.ToArray(),
            Strings.Settings_Ai_PrivacyDescription);
    }

    // API Key：只显示掩码；输入框只在要改的时候用，保存后清空
    FrameworkElement KeyRow(IAiSetup setup)
    {
        var box = new PasswordBox
        {
            PlaceholderText = setup.ApiKeyMask == null ? "sk-…" : Strings.Settings_Ai_ReplaceKey, Width = 220, Height = PaneTokens.ControlMd, Padding = new Thickness(10, 6, 10, 0),
            FontFamily = SmallStrongProto.FontFamily, FontSize = SmallStrongProto.FontSize,
        };
        void Save()
        {
            if (box.Password.Trim() is not { Length: > 0 } key) return;
            aiFieldError = Try(() => setup.SaveApiKey(key));
            aiResult = null;
            Later(0);
        }
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Save(); e.Handled = true; } };
        var wrap = new Border
        {
            Child = box, CornerRadius = new CornerRadius(((CornerRadius)Application.Current.Resources["PaneRadiusControl"]).TopLeft),
            Background = FillProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
        };
        var controls = new List<FrameworkElement> { wrap, Button(Strings.Common_Save, "check", Save) };
        if (setup.ApiKeyMask != null)
            controls.Add(IconButton("close", Strings.Settings_Ai_DeleteKey, () => { aiFieldError = Try(() => setup.SaveApiKey("")); Later(0); }));
        return Row("API Key", setup.ApiKeyMask is { } mask ? string.Format(Strings.Settings_Ai_KeySaved, (object?)(mask)) : Strings.Settings_Ai_KeyDescription, controls);
    }

    IEnumerable<FrameworkElement> AccountRows(IAiSetup setup)
    {
        if (aiBusy == "signin")
        {
            yield return Row(Strings.Settings_Ai_LoggingIn, Strings.Settings_Ai_LoginPendingDescription,
                Button(Strings.Common_Cancel, null, () => signIn?.Cancel()));
            yield break;
        }
        if (setup.Account is not { } account)
        {
            yield return Row(Strings.Settings_Ai_LoginTitle, Strings.Settings_Ai_LoginDescription,
                Button(Strings.Settings_Ai_Login, "login", () => _ = SignIn(setup)));
            if (aiResult is { Ok: false } r) yield return Row(Strings.Settings_Ai_LoginFailed, null, Status(false, r.Text));
            yield break;
        }
        // 套餐名官方不保证给：不知道时只说能不能用，不猜 Plus / Pro
        var plan = !account.CanUsePlan ? Strings.Settings_Ai_FreeAccount
            : account.PlanName is { Length: > 0 } name ? string.Format(Strings.Settings_Ai_Subscription, (object?)(name)) : Strings.Settings_Ai_SubscriptionUnknown;
        yield return Row(account.Email, plan, Button(Strings.Settings_Ai_Logout, "logout", () => _ = RunAi("signout", async t => { await setup.SignOutAsync(t); return null; })));
    }

    async Task SignIn(IAiSetup setup)
    {
        signIn?.Dispose();
        signIn = new CancellationTokenSource();
        await RunAi("signin", async _ => { await setup.SignInAsync(signIn.Token); return null; });
    }

    // 服务商（052 → 065，2026-11-06 用户定）：一排标签 = 内置的几家 + 用户添加的自定义服务商 + 「添加」。
    // 点哪个就切到哪个，每家各记各的地址、格式、模型和 key（key 由服务按服务商分开存）。内置的给「获取 key」
    FrameworkElement VendorRow(AiSettings a)
    {
        var current = a.CurrentEndpoint;
        var chips = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        Border VendorChip(string text, bool on)
        {
            var chip = Chip(text);
            chip.Height = PaneTokens.ControlSm;
            chip.CornerRadius = new CornerRadius(PaneTokens.ControlSm / 2);
            if (on) chip.BorderBrush = Accent();
            chips.Children.Add(chip);
            return chip;
        }
        foreach (var e in a.AllEndpoints)
        {
            var chip = VendorChip(e.Name, e.Id == current.Id);
            if (e.Id == current.Id) continue;
            chip.Tapped += (_, _) =>
            {
                aiFieldError = Try(() => SaveAi(x => x with { ApiEndpoint = e.Id }));
                aiModels = null; aiResult = null; deleteEndpointArmed = false;
                Later(0);
            };
        }
        // 添加：名字先给「自定义 N」，格式默认 OpenAI 兼容，地址空着，焦点直接给地址框
        if ((a.Endpoints ?? []).Count(e => AiPresets.Find(e.Id) is null) < 20)
        {
            var add = VendorChip(Strings.Settings_Ai_AddEndpoint, false);
            var label = (TextBlock)add.Child;
            add.Child = null;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(new PaneIcon { Glyph = "plus", Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = label.Foreground });
            content.Children.Add(label);
            add.Child = content;
            add.Tapped += (_, _) =>
            {
                var names = a.AllEndpoints.Select(e => e.Name).ToHashSet();
                int n = 1;
                while (names.Contains(string.Format(Strings.Settings_Ai_NewEndpointName, n))) n++;
                var id = "custom-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
                var created = new AiEndpoint(id, string.Format(Strings.Settings_Ai_NewEndpointName, n), "", AiApiFormat.OpenAi, null, null);
                aiFieldError = Try(() => SaveAi(x => x with { ApiEndpoint = id, Endpoints = [.. x.Endpoints ?? [], created] }));
                aiModels = null; aiResult = null; deleteEndpointArmed = false; focusEndpoint = aiFieldError == null;
                Later(0);
            };
        }
        var row = AiPresets.Find(current.Id) is { } preset
            ? Row(Strings.Settings_Ai_Vendor, Strings.Settings_Ai_VendorDescription, Button(Strings.Settings_Ai_GetKey, "external", () => OpenUrl(preset.KeyUrl)))
            : Row(Strings.Settings_Ai_Vendor, Strings.Settings_Ai_CustomVendorDescription);
        var stack = new StackPanel();
        stack.Children.Add(row);
        chips.Margin = new Thickness(14, 0, 14, 10);
        stack.Children.Add(chips);
        return stack;
    }

    // 当前服务商的地址：内置的只显示（要换地址就添加一个自定义的）；自定义的可以改名字、接口格式和地址
    IEnumerable<FrameworkElement> EndpointRows(AiSettings a)
    {
        var e = a.CurrentEndpoint;
        if (AiPresets.Find(e.Id) is not null)
        {
            var url = new TextBlock { Text = e.BaseUrl, FontFamily = SmallProto.FontFamily, FontSize = SmallProto.FontSize, Foreground = SmallProto.Foreground, VerticalAlignment = VerticalAlignment.Center, IsTextSelectionEnabled = true };
            yield return Row(Strings.Settings_Ai_Endpoint, Strings.Settings_Ai_PresetEndpointDescription, url);
            yield break;
        }
        yield return Row(Strings.Common_Name, Strings.Settings_Ai_EndpointNameDescription, CommitField(Strings.Common_Name, 220, e.Name, v =>
            v.Length is 0 or > 40 ? Strings.Settings_Ai_EndpointNameInvalid : Try(() => SaveEndpoint(x => x with { Name = v }))));
        yield return Row(Strings.Settings_Ai_Format, null, Seg([("OpenAi", Strings.Settings_Ai_FormatOpenAi), ("Anthropic", Strings.Settings_Ai_FormatAnthropic)], e.Format.ToString(), v =>
        {
            aiFieldError = Try(() => SaveEndpoint(x => x with { Format = Enum.Parse<AiApiFormat>(v) }));
            aiModels = null; aiResult = null;
            Later(0);
        }));
        bool anthropic = e.Format == AiApiFormat.Anthropic;
        yield return Row(Strings.Settings_Ai_Endpoint, anthropic ? Strings.Settings_Ai_EndpointAnthropicDescription : Strings.Settings_Ai_EndpointDescription,
            CommitField(anthropic ? "https://api.anthropic.com/v1" : "https://…/v1", 300, e.BaseUrl, v =>
            {
                v = v.TrimEnd('/');
                return AiSettings.ValidUrl(v) ? Try(() => SaveEndpoint(x => x with { BaseUrl = v })) : Strings.Settings_Ai_EndpointInvalid;
            }, focus: focusEndpoint));
        focusEndpoint = false;
    }

    // 删除自定义服务商：点两次（3 秒内）；删掉后切到它前面那一家，它的 key 由服务删掉
    FrameworkElement DeleteEndpointRow(AiSettings a)
    {
        var e = a.CurrentEndpoint;
        var button = Button(deleteEndpointArmed ? Strings.Settings_Ai_ConfirmDeleteEndpoint : Strings.Common_Delete, "trash", () =>
        {
            if (!deleteEndpointArmed) { deleteEndpointArmed = true; Render(); _ = DisarmDeleteEndpoint(); return; }
            deleteEndpointArmed = false;
            var all = a.AllEndpoints.ToList();
            var next = all[Math.Max(0, all.FindIndex(x => x.Id == e.Id) - 1)];
            aiFieldError = Try(() => SaveAi(x => x with { ApiEndpoint = next.Id, Endpoints = [.. (x.Endpoints ?? []).Where(y => y.Id != e.Id)] }));
            aiModels = null; aiResult = null;
            Later(0);
        });
        return Row(Strings.Settings_Ai_DeleteEndpoint, Strings.Settings_Ai_DeleteEndpointDescription, button);
    }

    async Task DisarmDeleteEndpoint()
    {
        await Task.Delay(3000);
        if (!deleteEndpointArmed) return;
        deleteEndpointArmed = false;
        if (current.Id == "ai") Render();
    }

    // 改当前服务商：内置服务商第一次改模型时才写进列表（名字、地址、格式仍以预设为准）
    void SaveEndpoint(Func<AiEndpoint, AiEndpoint> change) => SaveAi(x =>
    {
        var now = x.CurrentEndpoint;
        var list = (x.Endpoints ?? []).ToList();
        int i = list.FindIndex(e => e.Id == now.Id);
        if (i >= 0) list[i] = change(now); else list.Add(change(now));
        return x with { ApiEndpoint = now.Id, Endpoints = [.. list] };
    });

    // 模型：可以手填；拉取列表后，下面按输入过滤出最多 12 个可点的候选
    FrameworkElement ModelRow(AiSettings a, bool api)
    {
        string current = (api ? a.CurrentEndpoint.Model : a.ChatGptModel) ?? "";
        var box = Field(api ? Strings.Settings_Ai_ModelId : Strings.Settings_Ai_FirstModelDefault, 220);
        box.Text = current;
        var chips = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        void Commit(string value)
        {
            var v = value.Trim();
            aiFieldError = Try(() => { if (api) SaveEndpoint(x => x with { Model = v.Length == 0 ? null : v }); else SaveAi(x => x with { ChatGptModel = v.Length == 0 ? null : v }); });
        }
        void Fill()
        {
            chips.Children.Clear();
            if (aiModels is not { } models) return;
            var q = box.Text.Trim();
            foreach (var m in models.Where(m => q.Length == 0 || q == current || m.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || m.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(12))
            {
                var chip = Chip(m.DisplayName == m.Id ? m.Id : $"{m.DisplayName}（{m.Id}）");
                chip.Height = PaneTokens.ControlSm;
                chip.CornerRadius = new CornerRadius(13);
                if (m.Id == current) chip.BorderBrush = Accent();
                chip.Tapped += (_, _) => { Commit(m.Id); Later(0); };
                chips.Children.Add(chip);
            }
        }
        box.TextChanged += (_, _) => Fill();
        box.LostFocus += (_, _) => { if (box.Text.Trim() != current) { Commit(box.Text); Later(); } };
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Commit(box.Text); Later(0); e.Handled = true; } };
        Fill();
        var load = Button(aiBusy == "models" ? Strings.Settings_Ai_LoadingModels : Strings.Settings_Ai_FetchModels, "refresh", () => _ = RunAi("models", async t =>
        {
            aiModels = await app.Ai.ListModelsAsync(t);
            return aiModels.Count == 0 ? Strings.Settings_Ai_NoModels : null;
        }));
        Enable(load, aiBusy == null);
        var row = Row(Strings.Settings_Ai_Model, aiModels is { } list ? string.Format(Strings.Settings_Ai_ModelsCount, (object?)(list.Count)) : api ? Strings.Settings_Ai_ModelDescription : Strings.Settings_Ai_FirstModelDescription, Wrap(box), load);
        if (aiModels == null) return row;
        var stack = new StackPanel();
        stack.Children.Add(row);
        chips.Margin = new Thickness(14, 0, 14, 10);
        stack.Children.Add(chips);
        return stack;
    }

    // 启动器动作行（2026-10-30）：输入任何文字都出现的那一排。显示的在前、按顺序，可上移；关掉的排在后面。
    // 候选：翻译、问 AI、搜文件、其余 AI 预设动作、每个网页搜索站点
    List<string>? actionRowOrder;

    void RenderActionRow(LauncherSettings l)
    {
        var ai = app.Store.Current.Ai;
        var all = new List<(string Id, string Name, string Glyph)> { ("ai:translate", Strings.Common_Translate, "translate"), ("ai:ask", Strings.Launcher_AskAi, "ai"), ("files", Strings.Launcher_SearchFiles, "folder") };
        all.AddRange(ai.Actions.Where(a => a.Id != "translate").Select(a => ("ai:" + a.Id, a.Name, a.Glyph)));
        all.AddRange(l.SearchEngines.Select(e => ("web:" + e.Keyword, Strings.FormatName(Strings.Launcher_SearchWith, e.Name), "globe")));
        var shown = l.ActionRow.Where(id => all.Any(a => a.Id == id)).ToList();
        var ordered = shown.Select(id => all.First(a => a.Id == id)).Concat(all.Where(a => !shown.Contains(a.Id))).ToList();
        // issue 49：开关一拨行就跳到前面，连着点容易点错。页面开着时行的位置不动（只有「上移」改顺序），换分区或重开设置再按显示顺序排
        if (actionRowOrder is { } frozen)
            ordered = [.. frozen.Select(id => ordered.Find(a => a.Id == id)).Where(a => a.Id is not null), .. ordered.Where(a => !frozen.Contains(a.Id))];
        actionRowOrder = ordered.Select(a => a.Id).ToList();
        var rows = new List<FrameworkElement>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var (id, name, glyph) = ordered[i];
            bool on = shown.Contains(id);
            int pos = shown.IndexOf(id);
            var tools = new List<FrameworkElement>();
            if (on && pos > 0)
                tools.Add(IconButton("U", Strings.Common_MoveUp, () => { SaveLauncher(x => { var list = x.ActionRow.Where(v => all.Any(a => a.Id == v)).ToList(); (list[pos - 1], list[pos]) = (list[pos], list[pos - 1]); return x with { ActionRow = list.ToArray() }; }); actionRowOrder = null; Render(); }));
            tools.Add(Switch(on, v =>
            {
                SaveLauncher(x => x with { ActionRow = v ? [.. x.ActionRow.Where(a => a != id), id] : x.ActionRow.Where(a => a != id).ToArray() });
                Render();
            }));
            rows.Add(ListRow(glyph, name, on ? string.Format(pos < 9 ? Strings.Settings_Launcher_ActionPositionHotkey : Strings.Settings_Launcher_ActionPosition, pos + 1, pos + 1) : Strings.Settings_Launcher_ActionHidden, false, tools.ToArray()));
        }
        Group(Strings.Settings_Launcher_ActionRow, rows.ToArray(), Strings.Settings_Launcher_ActionRowDescription);
    }

    // 翻译用模型：单独指定一个快模型，空着 = 和主模型相同。候选用上面「拉取模型列表」拉到的
    // 第三方 API 的存在当前服务商里（065），ChatGPT 的存在 AiSettings.TranslationModel
    FrameworkElement TranslationModelRow(AiSettings a)
    {
        bool api = a.Provider == AiProvider.Api;
        string current = (api ? a.CurrentEndpoint.TranslationModel : a.TranslationModel) ?? "";
        var box = Field(Strings.Settings_Ai_SameAsMain, 220);
        box.Text = current;
        void Commit(string value)
        {
            var v = value.Trim();
            var model = v.Length == 0 ? null : v;
            aiFieldError = Try(() => { if (api) SaveEndpoint(x => x with { TranslationModel = model }); else SaveAi(x => x with { TranslationModel = model }); });
        }
        box.LostFocus += (_, _) => { if (box.Text.Trim() != current) { Commit(box.Text); Later(); } };
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Commit(box.Text); Later(0); e.Handled = true; } };
        var row = Row(Strings.Settings_Ai_TranslationModel, aiModels is null ? Strings.Settings_Ai_TranslationModelDescription : Strings.Settings_Ai_TranslationModelManualDescription, Wrap(box));
        if (aiModels is not { } models) return row;
        var chips = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6, Margin = new Thickness(14, 0, 14, 10) };
        var main = Chip(Strings.Settings_Ai_SameAsMain);
        main.Height = PaneTokens.ControlSm; main.CornerRadius = new CornerRadius(PaneTokens.ControlSm / 2);
        if (current.Length == 0) main.BorderBrush = Accent();
        main.Tapped += (_, _) => { Commit(""); Later(0); };
        chips.Children.Add(main);
        foreach (var m in models.Take(12))
        {
            var chip = Chip(m.DisplayName == m.Id ? m.Id : $"{m.DisplayName}（{m.Id}）");
            chip.Height = PaneTokens.ControlSm; chip.CornerRadius = new CornerRadius(PaneTokens.ControlSm / 2);
            if (m.Id == current) chip.BorderBrush = Accent();
            chip.Tapped += (_, _) => { Commit(m.Id); Later(0); };
            chips.Children.Add(chip);
        }
        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(chips);
        return stack;
    }

    // 预设动作：名称 + 提示词（多行，离开输入框时保存），可上移、删除（翻译是启动器固定入口，不能删），可添加
    void RenderAiActions(AiSettings a)
    {
        var list = new List<FrameworkElement>();
        for (int i = 0; i < a.Actions.Length; i++)
        {
            var act = a.Actions[i];
            int index = i;
            var name = Field(Strings.Common_Name, 120);
            name.Text = act.Name;
            name.LostFocus += (_, _) => { var v = name.Text.Trim(); if (v.Length > 0 && v != act.Name) aiFieldError = Try(() => SaveAction(index, x => x with { Name = v })); };
            var prompt = Field(Strings.Settings_Ai_ActionPrompt, 0);
            prompt.Width = double.NaN;
            prompt.Height = 92;
            prompt.AcceptsReturn = true;
            prompt.TextWrapping = TextWrapping.Wrap;
            prompt.Padding = new Thickness(10, 8, 10, 8);
            prompt.Text = act.PromptTemplate;
            prompt.LostFocus += (_, _) =>
            {
                var v = prompt.Text.Replace("\r\n", "\n").Replace('\r', '\n');
                if (v.Trim().Length == 0) { aiFieldError = Strings.Settings_Ai_ActionPromptRequired; Later(0); return; }
                if (v != act.PromptTemplate) aiFieldError = Try(() => SaveAction(index, x => x with { PromptTemplate = v }));
            };
            var head = new Grid { ColumnSpacing = 8 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new PaneIcon { Glyph = act.Glyph, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Foreground = IconProto.Foreground });
            var nameBox = Wrap(name);
            Grid.SetColumn(nameBox, 1);
            head.Children.Add(nameBox);
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (index > 0) tools.Children.Add(IconButton("U", Strings.Common_MoveUp, () => { MoveAction(index); Render(); }));
            if (act.Id != "translate")
                tools.Children.Add(IconButton("close", Strings.Settings_Ai_DeleteAction, () => { SaveAi(x => x with { Actions = x.Actions.Where((_, j) => j != index).ToArray() }); Render(); }));
            Grid.SetColumn(tools, 3);
            head.Children.Add(tools);
            var block = new StackPanel { Spacing = 8, Padding = new Thickness(14, 10, 14, 12) };
            block.Children.Add(head);
            block.Children.Add(Wrap(prompt));
            list.Add(block);
        }
        list.Add(AddRow(Button(Strings.Settings_Ai_AddAction, "plus", () =>
        {
            SaveAi(x => x with { Actions = [.. x.Actions, new AiActionSettings("custom-" + DateTime.Now.ToString("yyyyMMddHHmmss"), "新动作", "ai", "对下面的内容……\n\n{text}")] }); // 不翻译：用户数据
            Render();
        })));
        Group(Strings.Settings_Ai_Actions, list.ToArray(), Strings.Settings_Ai_ActionsDescription);
    }

    void SaveAction(int index, Func<AiActionSettings, AiActionSettings> change) =>
        SaveAi(x => x with { Actions = x.Actions.Select((v, j) => j == index ? change(v) : v).ToArray() });

    void MoveAction(int index) => SaveAi(x =>
    {
        var list = x.Actions.ToList();
        (list[index - 1], list[index]) = (list[index], list[index - 1]);
        return x with { Actions = list.ToArray() };
    });

    // 输入框：离开或回车时提交；commit 返回 null 表示保存成功，否则是要显示的错误
    Border CommitField(string placeholder, double width, string value, Func<string, string?> commit, bool focus = false)
    {
        var box = Field(placeholder, width);
        box.Text = value;
        if (focus) box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.StartBringIntoView(); };
        void Commit() { var v = box.Text.Trim(); if (v == value) return; aiFieldError = commit(v); Later(0); }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Commit(); e.Handled = true; } };
        return Wrap(box);
    }

    // 联网操作：同一时间一个；结果写进 aiResult，完成后刷新（还在 AI 分区才刷新）
    async Task RunAi(string what, Func<CancellationToken, Task<string?>> work)
    {
        if (aiBusy != null) return;
        aiBusy = what;
        aiLast = what;
        aiResult = null;
        Render();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(what == "signin" ? 6 : 1));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, what == "signin" && signIn != null ? signIn.Token : CancellationToken.None);
            var text = await work(linked.Token);
            if (text != null) aiResult = (what != "models" || aiModels is { Count: > 0 }, text);
        }
        catch (OperationCanceledException) { }
        catch (AiException e) { aiResult = (false, e.Message); }
        catch (Exception e) { aiResult = (false, App.FailureReason(e) ?? Strings.Common_UnexpectedProblem); }
        finally { aiBusy = null; }
        if (current.Id == "ai") Render();
    }

    static string? Try(Action save)
    {
        try { save(); return null; }
        catch (Exception e) { return e is ArgumentException ? Strings.Settings_SaveInvalid : App.FailureReason(e) ?? Strings.Settings_SaveFailed; }
    }

    void SaveAi(Func<AiSettings, AiSettings> change) =>
        app.Store.Save(app.Store.Current with { Ai = change(app.Store.Current.Ai) });

    // 任何设置保存都会让 AI 服务发 Changed；立刻重绘会把刚拨的开关换成新控件，滑块动画被截断成跳变（2026-10-31），
    // 所以等开关动画（PaneDurNormal）播完再重绘
    void OnAiChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { if (current.Id == "ai" && aiBusy == null) Later(); });
}
