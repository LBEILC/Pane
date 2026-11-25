// 设置窗口：账号（设计负责人，2026-11-24）。照原型 prototype/settings.html#account，规则见 docs/cloud.md「账号」「个人资料、头像、铭牌」，
// 接口见 Contracts/Cloud.cs（IAccountService）。只在「Pane 云」能力启用时出现（开卖前隐藏）。
// 没登录：登录 / 注册（两步）/ 忘记密码三个表单；会话失效：提示 + 登录表单（邮箱预填）。
// 已登录：资料（头像、昵称、创始会员铭牌、公开昵称开关）、订阅（档位、流量、云端图片、会员 QQ 群、试用、兑换码、购买）、设备、安全、退出和注销。
// 付款先用浏览器打开爱发电付款页（二维码以后补），每 3 秒查一次订单，最多 10 分钟。
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pane.Contracts;
using Pane.Localization;
using Pane.Views.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Pane.Views;

public sealed partial class SettingsView
{
    // ================= 账号 =================

    const string TermsVersion = "2026-11-24", PrivacyVersion = "2026-11-24";   // = 协议生效日期，同官网 site/assets/account.js
    const string TermsUrl = "https://pane.lubeiluchen.com/legal.html?doc=terms";
    const string FoundersUrl = "https://pane.lubeiluchen.com/founders.html";   // 创始会员墙

    // 表单的字，输入时只记下来，不重建界面（重建会丢焦点）
    string acEmail = "", acPw = "", acPw2 = "", acCode = "", acNick = "", acRec = "", acOld = "", acRedeem = "";
    bool acAgreeTerms, acAgreeCross, acCodeSent, acRecSaved, acFounderConfirm;
    string? acForm;         // 没登录时展开的表单：login / register / forgot
    int acRegStep = 1;
    string? acOpen;         // 已登录时展开的：password / forgot / delete
    string? acArmed;        // 要点两次的按钮
    string? acError;        // 表单下面的一行错误
    string? acBusy;         // 正在进行的操作（按钮显示「正在…」、其他按钮先禁用）
    string? acRecCode;      // 刚生成、只显示这一次的恢复码
    DeviceLimitHit? acLimit;
    IReadOnlyList<CloudDevice>? acDevices;
    CloudPlans? acPlans;
    CloudMemberGroup? acGroup;
    string? acGroupError;   // "subscription_required" 时只给一句；"not_configured" 时整行不出
    bool acChoosing;        // 正在选档位
    CloudOrder? acOrder;    // 正在等付款的订单
    string? acOrderNote;    // 订单失效、金额不足、超时
    CancellationTokenSource? acPoll;
    IAccountService? acHooked;
    bool acLoaded, acPayPreview, acFormPreview;

    IAccountService? Account => app.Account;

    void RenderAccount()
    {
        if (Account is not { } account) return;
        if (!ReferenceEquals(acHooked, account))
        {
            if (acHooked != null) acHooked.Changed -= OnAccountChanged;
            account.Changed += OnAccountChanged;
            acHooked = account;
            acLoaded = false;
        }
        if (acNotice is { } notice) Group(null, Row("", null, Status(true, notice)));
        // 设计预览（只在演示模式）：PANE_DESIGN_ACCOUNT_FORM = login / register / register2 / forgot / forgot2，直接展开对应表单截图核对
        if (!acFormPreview && Environment.GetEnvironmentVariable("PANE_DEMO") == "1" && Environment.GetEnvironmentVariable("PANE_DESIGN_ACCOUNT_FORM") is { Length: > 0 } form)
        {
            acFormPreview = true;
            acForm = form.TrimEnd('2');
            acRegStep = form == "register2" ? 2 : 1;
            acCodeSent = form == "forgot2";
            acEmail = "name@example.com";
            if (form is "register2" or "forgot2") acError = Strings.Settings_Account_PasswordMismatch;
        }
        if (account.State == AccountState.SignedIn) RenderSignedIn(account); else RenderSignedOut(account);
    }

    void OnAccountChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { if (current.Id == "account") Later(0); });

    // ---------- 小工具 ----------

    TextBox AcField(string value, string placeholder, double width, Action<string> changed, bool mono = false)
    {
        var box = Field(placeholder, width);
        box.Text = value;
        if (mono) box.FontFamily = (FontFamily)Application.Current.Resources["PaneFontMono"];
        box.TextChanged += (_, _) => changed(box.Text);
        return box;
    }

    Border AcPassword(string value, string placeholder, double width, Action<string> changed, Action? enter = null)
    {
        var box = new PasswordBox
        {
            Password = value, PlaceholderText = placeholder, Width = width, Height = PaneTokens.ControlMd, Padding = new Thickness(10, 6, 10, 0),
            FontFamily = SmallStrongProto.FontFamily, FontSize = SmallStrongProto.FontSize,
        };
        box.PasswordChanged += (_, _) => changed(box.Password);
        if (enter != null) box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { enter(); e.Handled = true; } };
        return new Border
        {
            Child = box, CornerRadius = new CornerRadius(((CornerRadius)Application.Current.Resources["PaneRadiusControl"]).TopLeft),
            Background = FillProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
        };
    }

    Button AcPrimary(string text, Action click)
    {
        var b = RowPrimary(text, click);
        Enable(b, acBusy == null);
        return b;
    }

    // 行内主按钮：主按钮的主题色底和白字，尺寸同行内按钮（28 高、控件圆角、小字号）；PanePrimaryButton 本身是窗口底部的 40 高胶囊
    static Button RowPrimary(string text, Action click)
    {
        var res = Application.Current.Resources;
        var b = new Button
        {
            Style = (Style)res["PanePrimaryButton"], MinHeight = PaneTokens.ControlSm, Height = PaneTokens.ControlSm, Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(((CornerRadius)res["PaneRadiusControl"]).TopLeft), FontSize = (double)res["PaneTextSm"], Content = text,
        };
        b.Click += (_, _) => click();
        return b;
    }

    Button AcButton(string text, string? glyph, Action click)
    {
        var b = Button(text, glyph, click);
        Enable(b, acBusy == null);
        return b;
    }

    // 要点两次：第一次换成「确认…」，3 秒内不点就还原
    Button AcArmed(string key, string text, string armedText, string? glyph, Action confirmed)
    {
        bool armed = acArmed == key;
        return AcButton(armed ? armedText : text, glyph, () =>
        {
            if (acArmed == key) { acArmed = null; confirmed(); return; }
            acArmed = key; Later(0);
            DispatcherQueue.TryEnqueue(async () => { await Task.Delay(3000); if (acArmed == key) { acArmed = null; Later(0); } });
        });
    }

    FrameworkElement AcCheck(string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox { IsChecked = value, Content = Text(text, SmallProto), MinWidth = 0, Padding = new Thickness(6, 0, 0, 0) };
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    // 表单下面一行错误：靠左，和上面各行的文字对齐（不放在控件列里）
    FrameworkElement? AcErrorRow() => acError is { } e ? new Border { Padding = new Thickness(14, 10, 14, 10), Child = Status(false, e) } : null;

    static string AcErrorText(CloudResult r) => r.Error switch
    {
        null => "",
        "network" => Strings.Cloud_Error_Network,
        "bad_credentials" => Strings.Settings_Account_BadCredentials,
        "bad_code" => r.AttemptsLeft is { } left ? string.Format(Strings.Cloud_Error_BadCodeLeft, left) : Strings.Settings_Account_BadCode,
        "code_expired" => Strings.Cloud_Error_CodeExpired,
        "email_taken" => Strings.Cloud_Error_EmailTaken,
        "rate_limited" => r.RetryAfter is { } s ? string.Format(Strings.Cloud_Error_RateLimited, AcWait(s)) : Strings.Cloud_Error_RateLimitedShort,
        "locked" => string.Format(Strings.Cloud_Error_Locked, AcWait(r.RetryAfter ?? 900)),
        "mail_quota" or "unavailable" when r.Error == "mail_quota" => Strings.Cloud_Error_MailQuota,
        "unavailable" => Strings.Cloud_Error_Unavailable,
        "banned" => Strings.Cloud_Error_Banned,
        "bad_nickname" => Strings.Cloud_Error_BadNickname,
        "already_lifetime" => Strings.Cloud_Error_AlreadyLifetime,
        "code_used" => Strings.Cloud_Error_CodeUsed,
        "trial_used_account" => Strings.Cloud_Error_TrialUsedAccount,
        "trial_used_device" => Strings.Cloud_Error_TrialUsedDevice,
        "trial_closed" => Strings.Cloud_Error_TrialClosed,
        "key_mismatch" => Strings.Cloud_Error_KeyMismatch,
        "no_recovery" => Strings.Cloud_Error_NoRecovery,
        "bad_recovery" => Strings.Cloud_Error_BadRecovery,
        "client_outdated" => Strings.Cloud_Error_ClientOutdated,
        "device_limit" => string.Format(Strings.Settings_Account_DeviceLimit, 5),
        var code => string.Format(Strings.Cloud_Error_Other, code),
    };

    static string AcWait(int seconds) => seconds < 60 ? $"{seconds} 秒" : seconds < 3600 ? $"{(seconds + 59) / 60} 分钟" : $"{(seconds + 3599) / 3600} 小时";

    static string AcDate(DateTimeOffset? t) => t is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd") : "—";

    static string AcSize(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : $"{bytes >> 20} MB";

    static string AcAgo(DateTimeOffset t)
    {
        var s = (DateTimeOffset.Now - t).TotalSeconds;
        return s < 120 ? "刚刚" : s < 3600 ? $"{(int)(s / 60)} 分钟前" : s < 86400 ? $"{(int)(s / 3600)} 小时前" : $"{(int)(s / 86400)} 天前";
    }

    static string AcTier(SubscriptionTier? t) => t switch
    {
        SubscriptionTier.Month => Strings.Settings_Account_TierMonth, SubscriptionTier.Year => Strings.Settings_Account_TierYear,
        SubscriptionTier.Lifetime => Strings.Settings_Account_TierLifetime, SubscriptionTier.Trial => Strings.Settings_Account_TierTrial, _ => "",
    };

    /// <summary>跑一个账号操作：按钮显示忙、结束后显示错误或成功提示，再重建界面。</summary>
    async Task AcRun(string key, Func<CancellationToken, Task<CloudResult>> action, Action? ok = null, string? done = null)
    {
        if (acBusy != null) return;
        acBusy = key; acError = null; Later(0);
        CloudResult result;
        try { result = await action(CancellationToken.None); }
        catch (Exception) { result = new CloudResult("network"); }
        acBusy = null;
        if (result.Ok) { ok?.Invoke(); if (done != null) ShowNotice(done); }
        else if (result is CloudResult<DeviceLimitHit> { Value: { } hit }) acLimit = hit;
        else acError = AcErrorText(result);
        Later(0);
    }

    // 成功提示：分区顶上一行，3 秒后消失（设置窗口没有全局提示条）
    string? acNotice;
    void ShowNotice(string text)
    {
        acNotice = text; Later(0);
        DispatcherQueue.TryEnqueue(async () => { await Task.Delay(3000); if (acNotice == text) { acNotice = null; Later(0); } });
    }

    static void AcCopy(string text)
    {
        var data = new DataPackage(); data.SetText(text);
        try { Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); } catch (Exception) { }
    }

    // ---------- 头像和铭牌 ----------

    FrameworkElement AcAvatar(CloudProfile p, double size = 44)
    {
        bool founder = p.Badges.Any(b => b.Id == "founder");
        var face = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Accent() };
        if (p.AvatarPath is { } path && File.Exists(path))
            face.Child = new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path)), Stretch = Stretch.UniformToFill };
        else
            face.Child = new TextBlock { Text = p.Nickname.Length > 0 ? char.IsSurrogate(p.Nickname[0]) ? p.Nickname[..2] : p.Nickname[..1] : "?",
                Foreground = new SolidColorBrush(Colors.White), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (!founder) return face;
        // 创始会员：外隔 2 一道 2 宽的金属环（docs/design.md「创始会员铭牌 → 头像环」），环不占布局
        var host = new Grid { Width = size, Height = size };
        host.Children.Add(face);
        host.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = size + 8, Height = size + 8, Margin = new Thickness(-4), StrokeThickness = 2,
            Stroke = (Brush)Application.Current.Resources["PaneFounderRingBrush"], IsHitTestVisible = false,
        });
        return host;
    }

    // 小铭牌 20 高：外框金属边 1；左格珐琅底「{award}创始会员」，右格金属面「No.007」
    static FrameworkElement AcBadge(CloudBadge b)
    {
        var res = Application.Current.Resources;
        var white = new SolidColorBrush(Windows.UI.Color.FromArgb(242, 255, 255, 255));
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Padding = new Thickness(6, 0, 7, 0), VerticalAlignment = VerticalAlignment.Stretch };
        name.Children.Add(new PaneIcon { Glyph = "award", Width = 13, Height = 13, Foreground = white, VerticalAlignment = VerticalAlignment.Center });
        name.Children.Add(new TextBlock { Text = Strings.Settings_Account_Founder, Foreground = white, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 80, VerticalAlignment = VerticalAlignment.Center });
        var left = new Border { Background = (Brush)res["PaneFounderEnamelBrush"], CornerRadius = new CornerRadius(5, 0, 0, 5), Child = name };
        var ink = new SolidColorBrush((Windows.UI.Color)res["PaneFounderInkColor"]);
        var no = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = ink, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 12, CharacterSpacing = 60 };
        no.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "No.", FontSize = 9.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(158, 0x27, 0x31, 0x5c)) });
        no.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = (b.Number ?? 0).ToString("000") });
        var right = new Border { Background = (Brush)res["PaneFounderMetalBrush"], CornerRadius = new CornerRadius(0, 5, 5, 0), Padding = new Thickness(7, 0, 8, 0), Child = no };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(left); row.Children.Add(right);
        var plate = new Border
        {
            Height = 20, Padding = new Thickness(1), CornerRadius = new CornerRadius(6), Background = (Brush)res["PaneFounderBezelBrush"], Child = row,
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(plate, string.Format(Strings.Settings_Account_FounderTip, (b.Number ?? 0).ToString("000")));
        return plate;
    }

    FrameworkElement AcMeter(long used, long total)
    {
        double pct = total > 0 ? Math.Clamp(used / (double)total, 0, 1) : 0;
        var track = new Grid { Width = 120, Height = 4, CornerRadius = new CornerRadius(2), Background = FillProto.BorderBrush, VerticalAlignment = VerticalAlignment.Center };
        track.Children.Add(new Border { Width = 120 * pct, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Background = Accent() });
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        panel.Children.Add(track);
        panel.Children.Add(new TextBlock { Text = $"{AcSize(used)} / {AcSize(total)}", MinWidth = 96, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = SmallProto.FontFamily, FontSize = SmallProto.FontSize, Foreground = SmallProto.Foreground });
        return panel;
    }

    // ---------- 没登录 ----------

    void RenderSignedOut(IAccountService account)
    {
        if (account.State == AccountState.NeedsSignIn)
        {
            if (acForm == null) { acForm = "login"; if (acEmail.Length == 0) acEmail = account.LastEmail ?? ""; }
            Group(null, Row(Strings.Settings_Account_NeedsSignIn, Strings.Settings_Account_NeedsSignInDescription));
        }
        else
            Group(null, Row(Strings.Settings_Account_SignedOutTitle, Strings.Settings_Account_SignedOutDescription,
                AcButton(Strings.Settings_Account_Login, "login", () => { acForm = "login"; acError = null; Later(0); }),
                AcButton(Strings.Settings_Account_Register, null, () => { acForm = "register"; acRegStep = 1; acError = null; Later(0); })));

        if (acLimit is { } hit) { RenderDeviceLimit(account, hit); return; }

        var rows = new List<FrameworkElement>();
        if (acForm == "login")
        {
            void Login()
            {
                if (!AcEmailOk()) return;
                _ = AcRun("login", async t => (CloudResult)await account.SignInAsync(acEmail.Trim(), acPw, t), () => { acPw = ""; acForm = null; });
            }
            rows.Add(Row(Strings.Settings_Account_Email, null, Wrap(AcField(acEmail, "name@example.com", 260, v => acEmail = v))));
            rows.Add(Row(Strings.Settings_Account_Password, null, AcPassword(acPw, "", 260, v => acPw = v, Login)));
            if (AcErrorRow() is { } e) rows.Add(e);
            rows.Add(Row("", null, AcButton(Strings.Settings_Account_ForgotPassword, null, () => { acForm = "forgot"; acCodeSent = false; acError = null; Later(0); }),
                AcPrimary(acBusy == "login" ? Strings.Settings_Account_LoggingIn : Strings.Settings_Account_Login, Login)));
            Group(Strings.Settings_Account_Login, rows.ToArray());
        }
        else if (acForm == "register")
        {
            if (acRegStep == 1)
            {
                rows.Add(Row(Strings.Settings_Account_Email, null, Wrap(AcField(acEmail, "name@example.com", 260, v => acEmail = v))));
                var checks = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 4) };
                checks.Children.Add(AcCheck(Strings.Settings_Account_AgreeTerms, acAgreeTerms, v => acAgreeTerms = v));
                checks.Children.Add(AcCheck(Strings.Settings_Account_AgreeCrossBorder, acAgreeCross, v => acAgreeCross = v));
                rows.Add(checks);
                if (AcErrorRow() is { } e) rows.Add(e);
                rows.Add(Row("", null, AcButton(Strings.Settings_Account_ReadTerms, "external", () => OpenUrl(TermsUrl)),
                    AcPrimary(Strings.Settings_Account_SendCode, () =>
                    {
                        if (!AcEmailOk()) return;
                        if (!acAgreeTerms || !acAgreeCross) { acError = Strings.Settings_Account_AgreeRequired; Later(0); return; }
                        _ = AcRun("send", t => account.SendCodeAsync(acEmail.Trim(), CodePurpose.Register, t), () => acRegStep = 2);
                    })));
                Group($"{Strings.Settings_Account_Register} · {string.Format(Strings.Settings_Account_RegisterStep, 1)}", rows.ToArray());
            }
            else
            {
                rows.Add(Row(Strings.Settings_Account_Code, string.Format(Strings.Settings_Account_CodeSent, acEmail.Trim()),
                    Wrap(AcField(acCode, "123456", 120, v => acCode = v)),
                    AcButton(Strings.Settings_Account_Resend, null, () => _ = AcRun("send", t => account.SendCodeAsync(acEmail.Trim(), CodePurpose.Register, t)))));
                rows.Add(Row(Strings.Settings_Account_Nickname, null, Wrap(AcField(acNick, Strings.Settings_Account_NicknamePlaceholder, 260, v => acNick = v))));
                rows.Add(Row(Strings.Settings_Account_Password, null, AcPassword(acPw, Strings.Settings_Account_PasswordPlaceholder, 260, v => acPw = v)));
                rows.Add(Row(Strings.Settings_Account_ConfirmPassword, null, AcPassword(acPw2, "", 260, v => acPw2 = v)));
                if (AcErrorRow() is { } e) rows.Add(e);
                rows.Add(Row("", null, AcButton(Strings.Settings_Account_ChangeEmail, null, () => { acRegStep = 1; acError = null; Later(0); }),
                    AcPrimary(Strings.Settings_Account_Register, () =>
                    {
                        if (!AcCodeOk() || !AcNewPasswordOk()) return;
                        var nick = acNick.Trim();
                        _ = AcRun("register", async t => (CloudResult)await account.RegisterAsync(acEmail.Trim(), acCode.Trim(), acPw, nick.Length > 0 ? nick : null,
                            new CloudConsents(TermsVersion, PrivacyVersion, true), t),
                            () => { acPw = acPw2 = acCode = ""; acForm = null; acRegStep = 1; }, Strings.Settings_Account_Registered);
                    })));
                Group($"{Strings.Settings_Account_Register} · {string.Format(Strings.Settings_Account_RegisterStep, 2)}", rows.ToArray(), Strings.Settings_Account_PasswordNote);
            }
        }
        else if (acForm == "forgot")
        {
            rows.Add(Row(Strings.Settings_Account_Email, acCodeSent ? string.Format(Strings.Settings_Account_CodeSent, acEmail.Trim()) : null,
                acCodeSent ? [Wrap(AcField(acEmail, "name@example.com", 260, v => acEmail = v))]
                : [Wrap(AcField(acEmail, "name@example.com", 260, v => acEmail = v)),
                   AcButton(Strings.Settings_Account_SendCode, null, () => { if (AcEmailOk()) _ = AcRun("send", t => account.SendCodeAsync(acEmail.Trim(), CodePurpose.Reset, t), () => acCodeSent = true); })]));
            if (acCodeSent)
            {
                rows.Add(Row(Strings.Settings_Account_Code, null, Wrap(AcField(acCode, "123456", 120, v => acCode = v)),
                    AcButton(Strings.Settings_Account_Resend, null, () => _ = AcRun("send", t => account.SendCodeAsync(acEmail.Trim(), CodePurpose.Reset, t)))));
                rows.Add(Row(Strings.Settings_Account_NewPassword, null, AcPassword(acPw, Strings.Settings_Account_PasswordPlaceholder, 260, v => acPw = v)));
                rows.Add(Row(Strings.Settings_Account_ConfirmNewPassword, null, AcPassword(acPw2, "", 260, v => acPw2 = v)));
                rows.Add(Row(Strings.Settings_Account_RecoveryCode, null, Wrap(AcField(acRec, Strings.Settings_Account_RecoveryCodeOptional, 300, v => acRec = v, mono: true))));
                if (AcErrorRow() is { } e) rows.Add(e);
                // 本机有数据密钥（会话失效但没退出）→ LocalKey；填了恢复码 → RecoveryCode；都没有 → Fresh（要点两次，说明会清空云端）
                var mode = account.HasLocalKey ? ResetMode.LocalKey : acRec.Trim().Length > 0 ? ResetMode.RecoveryCode : ResetMode.Fresh;
                void Reset() => _ = AcRun("reset", async t => (CloudResult)await account.ResetPasswordAsync(acEmail.Trim(), acCode.Trim(), acPw, mode, acRec.Trim().Length > 0 ? acRec : null, t),
                    () => { acPw = acPw2 = acCode = acRec = ""; acForm = null; acCodeSent = false; },
                    mode == ResetMode.Fresh ? Strings.Settings_Account_ResetDone : Strings.Settings_Account_ResetKept);
                rows.Add(Row("", mode == ResetMode.Fresh && acArmed == "fresh" ? Strings.Settings_Account_ResetFreshConfirm : null,
                    AcButton(Strings.Common_Cancel, null, () => { acForm = "login"; acError = null; Later(0); }),
                    mode == ResetMode.Fresh
                        ? AcArmed("fresh", Strings.Settings_Account_ResetPassword, Strings.Settings_Account_ResetPassword, null, () => { if (AcCodeOk() && AcNewPasswordOk()) Reset(); })
                        : AcPrimary(Strings.Settings_Account_ResetPassword, () => { if (AcCodeOk() && AcNewPasswordOk()) Reset(); })));
            }
            Group(Strings.Settings_Account_ResetPassword, rows.ToArray(), Strings.Settings_Account_ForgotOutDescription);
        }
    }

    // 登录 / 注册 / 重设时设备已满：列出设备，选一台移除后接着登录
    void RenderDeviceLimit(IAccountService account, DeviceLimitHit hit)
    {
        var rows = hit.Devices.Select(d => ListRow("monitor", d.Name, string.Format(Strings.Settings_Account_LastSeen, AcAgo(d.LastSeenAt)), false,
            AcButton(Strings.Settings_Account_RemoveAndLogin, null, () => _ = AcRun("limit", t => account.RemoveDeviceAndSignInAsync(d.Id, t), () => { acLimit = null; acForm = null; acPw = ""; })))).ToList();
        if (AcErrorRow() is { } e) rows.Add(e);
        rows.Add(Row("", null, AcButton(Strings.Common_Cancel, null, () => { acLimit = null; Later(0); })));
        Group(string.Format(Strings.Settings_Account_DeviceLimit, hit.Devices.Count), rows.ToArray());
    }

    bool AcEmailOk()
    {
        var v = acEmail.Trim();
        bool ok = v.Length <= 254 && System.Text.RegularExpressions.Regex.IsMatch(v, @"^[^\s@]+@[^\s@]+\.[^\s@]+$");
        if (!ok) { acError = Strings.Settings_Account_BadEmail; Later(0); }
        return ok;
    }
    bool AcCodeOk()
    {
        bool ok = System.Text.RegularExpressions.Regex.IsMatch(acCode.Trim(), @"^\d{6}$");
        if (!ok) { acError = Strings.Settings_Account_CodeRule; Later(0); }
        return ok;
    }
    bool AcNewPasswordOk()
    {
        int n = System.Globalization.StringInfo.ParseCombiningCharacters(acPw).Length;
        acError = n is < 8 or > 64 ? Strings.Settings_Account_PasswordRule : acPw != acPw2 ? Strings.Settings_Account_PasswordMismatch : null;
        if (acError != null) Later(0);
        return acError == null;
    }

    // ---------- 已登录 ----------

    void RenderSignedIn(IAccountService account)
    {
        var p = account.Profile!;
        if (!acLoaded)
        {
            acLoaded = true;
            _ = account.RefreshAsync(CancellationToken.None);
            _ = AcLoadDevices(account);
            _ = AcLoadGroup(account);
        }
        var founder = p.Badges.FirstOrDefault(b => b.Id == "founder");
        // 设计预览（只在演示模式）：PANE_DESIGN_PAY=1 打开时直接下一笔年卡订单，截图核对付款二维码
        if (!acPayPreview && Environment.GetEnvironmentVariable("PANE_DEMO") == "1" && Environment.GetEnvironmentVariable("PANE_DESIGN_PAY") == "1")
        {
            acPayPreview = true;
            _ = AcBuy(account, new CloudPlan("pane_year", SubscriptionTier.Year, "69.90", 366, 2L << 30));
        }

        // 资料：头像 + 昵称 + 铭牌 + 邮箱；创始会员多一行公开开关
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(Text(p.Nickname, LabelProto));
        if (founder != null) name.Children.Add(AcBadge(founder));
        var who = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(name);
        who.Children.Add(Text(p.Email, DescProto));
        var head = new Grid { ColumnSpacing = 12, MinHeight = 72, Padding = new Thickness(18, 12, 14, 12) };   // 右同设置行（14）；左多 4，让金属环外沿和下面各行的文字对齐；上下多留给环
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var avatar = AcAvatar(p); avatar.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(avatar);
        Grid.SetColumn(who, 1); head.Children.Add(who);
        var edit = AcButton(Strings.Settings_Account_EditProfile, "external", () => OpenUrl(account.AccountCenterUrl + "#profile"));
        edit.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(edit, 2); head.Children.Add(edit);
        var profile = new List<FrameworkElement> { head };
        if (founder != null)
        {
            profile.Add(Row(Strings.Settings_Account_FounderPublic,
                founder.Hidden ? Strings.Settings_Account_FounderHidden : Strings.Settings_Account_FounderPublicDescription,
                AcButton(Strings.Settings_Account_FounderPage, "external", () => OpenUrl(FoundersUrl)),
                Switch(founder.Public, on =>
                {
                    if (on) { acFounderConfirm = true; Later(0); return; }   // 打开前先确认（下一行）
                    _ = AcRun("public", t => account.SetFounderPublicAsync(false, t), null, Strings.Settings_Account_FounderPublicOff);
                })));
            if (acFounderConfirm && !founder.Public)
                profile.Add(Row("", string.Format(Strings.Settings_Account_FounderPublicConfirm, p.Nickname),
                    AcButton(Strings.Common_Cancel, null, () => { acFounderConfirm = false; Later(0); }),
                    AcPrimary(Strings.Settings_Account_FounderPublicConfirmButton, () =>
                        _ = AcRun("public", t => account.SetFounderPublicAsync(true, t), () => acFounderConfirm = false, Strings.Settings_Account_FounderPublicOn))));
        }
        Group(null, profile.ToArray(), Strings.Settings_Account_EditProfileDescription);

        RenderSubscription(account, p);
        RenderBuy(account);
        RenderDevices(account, p);
        RenderSecurity(account, p);

        // 退出、注销
        var end = new List<FrameworkElement>
        {
            Row(Strings.Settings_Account_Logout, Strings.Settings_Account_LogoutDescription,
                AcArmed("logout", Strings.Settings_Account_Logout, Strings.Settings_Account_ConfirmLogout, "logout", () => _ = AcRun("logout", t => account.SignOutAsync(t), AcReset))),
            Row(Strings.Settings_Account_Delete, Strings.Settings_Account_DeleteDescription,
                AcButton(acOpen == "delete" ? Strings.Common_Collapse : Strings.Settings_Account_Delete, null, () => { acOpen = acOpen == "delete" ? null : "delete"; acError = null; Later(0); })),
        };
        if (acOpen == "delete")
        {
            end.Add(Row(Strings.Settings_Account_CurrentPassword, null, AcPassword(acOld, "", 220, v => acOld = v),
                AcArmed("delete", Strings.Settings_Account_Delete, Strings.Settings_Account_ConfirmDelete, "trash", () =>
                {
                    if (acOld.Length == 0) { acError = Strings.Settings_Account_DeleteNeedPassword; Later(0); return; }
                    _ = AcRun("delete", t => account.DeleteAccountAsync(acOld, t), AcReset, Strings.Settings_Account_Deleted);
                })));
            if (AcErrorRow() is { } e) end.Add(e);
        }
        Group(null, end.ToArray());
    }

    void AcReset()
    {
        acOpen = null; acArmed = null; acError = null; acOld = acPw = acPw2 = acCode = acRec = ""; acRecCode = null;
        acDevices = null; acGroup = null; acGroupError = null; acOrder = null; acChoosing = false; acPoll?.Cancel(); acLoaded = false;
    }

    void RenderSubscription(IAccountService account, CloudProfile p)
    {
        var s = p.Subscription;
        var rows = new List<FrameworkElement>();
        if (s.Status is SubscriptionStatus.Active or SubscriptionStatus.Trial)
            rows.Add(Row(AcTier(s.Tier), s.Lifetime ? Strings.Settings_Account_LifetimeNote : string.Format(Strings.Settings_Account_ExpiresAt, AcDate(s.ExpiresAt)),
                s.Lifetime ? [] : [AcButton(Strings.Settings_Account_Renew, null, () => AcChoose(account))]));
        else if (s.Status == SubscriptionStatus.Expired)
            rows.Add(Row(Strings.Settings_Account_Expired, string.Format(Strings.Settings_Account_ExpiredDescription, AcDate(s.RetainUntil)),
                AcButton(Strings.Settings_Account_Renew, null, () => AcChoose(account))));
        else
            rows.Add(Row(Strings.Settings_Account_NoSubscription, Strings.Settings_Account_NoSubscriptionDescription,
                AcButton(Strings.Settings_Account_Buy, null, () => AcChoose(account))));
        if (s.TrialAvailable && s.Status == SubscriptionStatus.None)
            rows.Add(Row(Strings.Settings_Account_Trial, Strings.Settings_Account_TrialDescription,
                AcPrimary(Strings.Settings_Account_TrialGet, () => _ = AcRun("trial", t => account.ClaimTrialAsync(t), null, Strings.Settings_Account_TrialDone))));
        if (s.Quota is { } q)
            rows.Add(Row(Strings.Settings_Account_Traffic, string.Format(Strings.Settings_Account_TrafficDescription, q.ResetsAt.ToLocalTime().ToString("M 月 d 日")), AcMeter(q.UsedBytes, q.LimitBytes)));
        if (s.Storage is { } st)
            rows.Add(Row(Strings.Settings_Account_Storage, Strings.Settings_Account_StorageDescription, AcMeter(st.UsedBytes, st.LimitBytes)));
        // 会员 QQ 群：没配群号整行不出；不是正式会员只说明
        if (acGroup is { } g)
            rows.Add(Row(Strings.Settings_Account_MemberGroup,
                $"{Strings.Settings_Account_MemberGroupDescription}\n{string.Format(Strings.Settings_Account_MemberGroupNumber, g.Number)} · {string.Format(Strings.Settings_Account_MemberGroupCode, g.Code)}",
                g.Url is { } url
                    ? [AcButton(Strings.Settings_Account_MemberGroupCopyCode, "copy", () => { AcCopy(g.Code); ShowNotice(Strings.Settings_Account_Copied); }), AcButton(Strings.Settings_Account_MemberGroupJoin, "external", () => OpenUrl(url))]
                    : [AcButton(Strings.Settings_Account_MemberGroupCopyCode, "copy", () => { AcCopy(g.Code); ShowNotice(Strings.Settings_Account_Copied); })]));
        else if (acGroupError == "subscription_required" && s.Status != SubscriptionStatus.None)
            rows.Add(Row(Strings.Settings_Account_MemberGroup, Strings.Settings_Account_MemberGroupLocked));
        rows.Add(Row(Strings.Settings_Account_Redeem, Strings.Settings_Account_RedeemDescription,
            Wrap(AcField(acRedeem, "PANE-XXXX-XXXX-XXXX", 200, v => acRedeem = v, mono: true)),
            AcButton(Strings.Settings_Account_RedeemButton, null, () =>
            {
                if (acRedeem.Trim().Length == 0) return;
                _ = AcRun("redeem", t => account.RedeemAsync(acRedeem.Trim(), t), () => { acRedeem = ""; _ = AcLoadGroup(account); }, Strings.Settings_Account_RedeemDone);
            })));
        if (acOpen == null && acError != null && acForm == null && acBusy == null && AcErrorRow() is { } e) rows.Add(e);
        Group(Strings.Settings_Account_Subscription, rows.ToArray());
    }

    async Task AcLoadDevices(IAccountService account)
    {
        var r = await account.GetDevicesAsync(CancellationToken.None);
        if (r.Ok) { acDevices = r.Value; Later(0); }
    }

    async Task AcLoadGroup(IAccountService account)
    {
        var r = await account.GetMemberGroupAsync(CancellationToken.None);
        acGroup = r.Ok ? r.Value : null;
        acGroupError = r.Error;
        Later(0);
    }

    // ---------- 购买 ----------

    void AcChoose(IAccountService account)
    {
        acChoosing = true; acOrder = null; acOrderNote = null; Later(0);
        if (acPlans == null) _ = Task.Run(async () =>
        {
            var r = await account.GetPlansAsync(CancellationToken.None);
            if (r.Ok) { acPlans = r.Value; DispatcherQueue.TryEnqueue(() => Later(0)); }
        });
    }

    void RenderBuy(IAccountService account)
    {
        if (acOrder is { } order)
        {
            // 照原型：左边付款二维码（手机扫码在爱发电付款），右边金额、说明、等待状态和按钮
            var pay = new Grid { ColumnSpacing = 18, Padding = new Thickness(14, 14, 14, 14) };
            pay.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pay.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (order.PayUrl is { } link && AcQr(link) is { } qr) pay.Children.Add(qr);
            var side = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            side.Children.Add(Text(string.Format(Strings.Settings_Account_PayTitle, order.Amount), LabelProto));
            side.Children.Add(new TextBlock { Text = acOrderNote ?? Strings.Settings_Account_PayDescription, TextWrapping = TextWrapping.Wrap, LineHeight = 18,
                FontFamily = DescProto.FontFamily, FontSize = DescProto.FontSize, Foreground = DescProto.Foreground });
            if (acOrderNote == null)
            {
                // 等待中：小转圈 + 一句，不用对勾（对勾读起来像已经付好了）
                var wait = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 4) };
                wait.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center });
                wait.Children.Add(Text(Strings.Settings_Account_PayWaiting, SmallProto));
                side.Children.Add(wait);
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (order.PayUrl is { } url) buttons.Children.Add(AcButton(Strings.Settings_Account_PayInBrowser, "external", () => OpenUrl(url)));
            buttons.Children.Add(AcButton(Strings.Common_Cancel, null, () => { acPoll?.Cancel(); acOrder = null; acChoosing = false; Later(0); }));
            side.Children.Add(buttons);
            Grid.SetColumn(side, 1);
            pay.Children.Add(side);
            Group(Strings.Settings_Account_Buy, pay);
            return;
        }
        if (!acChoosing) return;
        var rows = new List<FrameworkElement>();
        if (acPlans is { } plans)
            foreach (var plan in plans.Plans)
            {
                string desc = plan.Tier switch
                {
                    SubscriptionTier.Month => Strings.Settings_Account_PlanMonth, SubscriptionTier.Year => Strings.Settings_Account_PlanYear,
                    _ => Strings.Settings_Account_PlanLifetime,
                };
                var buy = plan.Tier == SubscriptionTier.Year ? AcPrimary(Strings.Settings_Account_Buy, () => _ = AcBuy(account, plan)) : AcButton(Strings.Settings_Account_Buy, null, () => _ = AcBuy(account, plan));
                rows.Add(Row($"{AcTier(plan.Tier)}  ¥{plan.Price}", desc, buy));
            }
        if (AcErrorRow() is { } e) rows.Add(e);
        rows.Add(Row("", null, AcButton(Strings.Common_Cancel, null, () => { acChoosing = false; acError = null; Later(0); })));
        Group(Strings.Settings_Account_ChoosePlan, rows.ToArray(), Strings.Settings_Account_ChoosePlanNote);
    }

    async Task AcBuy(IAccountService account, CloudPlan plan)
    {
        if (acBusy != null) return;
        acBusy = "buy"; acError = null; Later(0);
        var r = await account.CreateOrderAsync(plan.Id, CancellationToken.None);
        acBusy = null;
        if (!r.Ok || r.Value is not { } order) { acError = AcErrorText(r); Later(0); return; }
        acOrder = order; acChoosing = false; acOrderNote = null; Later(0);
        acPoll?.Cancel();
        var cts = acPoll = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(3000, cts.Token);
                var o = await account.GetOrderAsync(order.OrderNo, cts.Token);
                if (!o.Ok || o.Value is not { } now) continue;   // 网络抖动，下次再查
                if (now.Status == CloudOrderStatus.Paid) { acOrder = null; ShowNotice(string.Format(Strings.Settings_Account_PayDone, AcTier(plan.Tier))); _ = AcLoadGroup(account); Later(0); return; }
                if (now.Status == CloudOrderStatus.Underpaid) { acOrderNote = Strings.Settings_Account_PayUnderpaid; Later(0); return; }
                if (now.Status == CloudOrderStatus.Expired) { acOrderNote = Strings.Settings_Account_PayExpired; Later(0); return; }
            }
        }
        catch (OperationCanceledException) { }
        if (acOrder != null && acOrderNote == null && cts.Token.IsCancellationRequested && ReferenceEquals(acPoll, cts)) { acOrderNote = Strings.Settings_Account_PayTimeout; Later(0); }
    }

    static FrameworkElement? AcQr(string text)
    {
        bool[,] m;
        try { m = Services.QrCode.Encode(text); } catch (ArgumentException) { return null; }
        int n = m.GetLength(0);
        const double size = 148;
        double cell = size / (n + 8);
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
                if (m[r, c]) group.Children.Add(new RectangleGeometry { Rect = new Windows.Foundation.Rect((c + 4) * cell, (r + 4) * cell, cell + 0.15, cell + 0.15) });
        var path = new Microsoft.UI.Xaml.Shapes.Path { Data = group, Fill = new SolidColorBrush(Colors.Black) };
        var canvas = new Canvas { Width = size, Height = size };
        canvas.Children.Add(path);
        var box = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Colors.White), Child = canvas, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, Strings.Settings_Account_PayTitle);
        return box;
    }

    // ---------- 设备 ----------

    void RenderDevices(IAccountService account, CloudProfile p)
    {
        var list = acDevices ?? [];
        var rows = list.Select(d =>
        {
            FrameworkElement label = d.Current ? AcLabelWithChip(d.Name, Strings.Settings_Account_ThisDevice) : Text(d.Name, LabelProto);
            return d.Current
                ? ListRow("monitor", label, string.Format(Strings.Settings_Account_LastSeen, AcAgo(d.LastSeenAt)), false)
                : ListRow("monitor", label, string.Format(Strings.Settings_Account_LastSeen, AcAgo(d.LastSeenAt)), false,
                    IconButton("close", Strings.Settings_Account_RemoveDevice, () => _ = AcRun("device", t => account.RemoveDeviceAsync(d.Id, t), () => _ = AcLoadDevices(account))));
        }).ToArray();
        Group(Strings.Settings_Account_Devices, rows, string.Format(Strings.Settings_Account_DevicesNote, list.Count == 0 ? p.DeviceCount : list.Count, p.DeviceLimit));
    }

    FrameworkElement AcLabelWithChip(string text, string chip)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(Text(text, LabelProto));
        panel.Children.Add(Chip(chip));
        return panel;
    }

    // ---------- 安全 ----------

    void RenderSecurity(IAccountService account, CloudProfile p)
    {
        var rows = new List<FrameworkElement>
        {
            Row(Strings.Settings_Account_ChangePassword, Strings.Settings_Account_ChangePasswordDescription,
                AcButton(acOpen == "password" ? Strings.Common_Collapse : Strings.Common_Change, null, () => { acOpen = acOpen == "password" ? null : "password"; acError = null; Later(0); })),
        };
        if (acOpen == "password")
        {
            rows.Add(Row(Strings.Settings_Account_CurrentPassword, null, AcPassword(acOld, "", 220, v => acOld = v)));
            rows.Add(Row(Strings.Settings_Account_NewPassword, null, AcPassword(acPw, Strings.Settings_Account_PasswordPlaceholder, 220, v => acPw = v)));
            rows.Add(Row(Strings.Settings_Account_ConfirmNewPassword, null, AcPassword(acPw2, "", 220, v => acPw2 = v)));
            if (AcErrorRow() is { } e) rows.Add(e);
            rows.Add(Row("", null, AcPrimary(Strings.Common_Save, () =>
            {
                if (!AcNewPasswordOk()) return;
                _ = AcRun("password", t => account.ChangePasswordAsync(acOld, acPw, t), () => { acOpen = null; acOld = acPw = acPw2 = ""; }, Strings.Settings_Account_PasswordChanged);
            })));
        }
        // 已登录时忘记密码：本机有数据密钥，重设后云端数据保留
        rows.Add(Row(Strings.Settings_Account_ForgotPassword, Strings.Settings_Account_ForgotInDescription,
            AcButton(acOpen == "forgot" ? Strings.Common_Collapse : Strings.Settings_Account_ResetPassword, null, () => { acOpen = acOpen == "forgot" ? null : "forgot"; acCodeSent = false; acError = null; Later(0); })));
        if (acOpen == "forgot")
        {
            if (!acCodeSent)
                rows.Add(Row(p.Email, null, AcPrimary(Strings.Settings_Account_SendCode, () => _ = AcRun("send", t => account.SendCodeAsync(p.Email, CodePurpose.Reset, t), () => acCodeSent = true))));
            else
            {
                rows.Add(Row(Strings.Settings_Account_Code, string.Format(Strings.Settings_Account_CodeSent, p.Email), Wrap(AcField(acCode, "123456", 120, v => acCode = v)),
                    AcButton(Strings.Settings_Account_Resend, null, () => _ = AcRun("send", t => account.SendCodeAsync(p.Email, CodePurpose.Reset, t)))));
                rows.Add(Row(Strings.Settings_Account_NewPassword, null, AcPassword(acPw, Strings.Settings_Account_PasswordPlaceholder, 220, v => acPw = v)));
                rows.Add(Row(Strings.Settings_Account_ConfirmNewPassword, null, AcPassword(acPw2, "", 220, v => acPw2 = v)));
                if (AcErrorRow() is { } e) rows.Add(e);
                rows.Add(Row("", null, AcPrimary(Strings.Settings_Account_ResetPassword, () =>
                {
                    if (!AcCodeOk() || !AcNewPasswordOk()) return;
                    _ = AcRun("reset", async t => (CloudResult)await account.ResetPasswordAsync(p.Email, acCode.Trim(), acPw, ResetMode.LocalKey, null, t),
                        () => { acOpen = null; acCodeSent = false; acCode = acPw = acPw2 = ""; }, Strings.Settings_Account_ResetKept);
                })));
            }
        }
        // 恢复码
        var recDesc = acArmed == "rec-gen" ? Strings.Settings_Account_RecoveryRegenerateWarning : acArmed == "rec-del" ? Strings.Settings_Account_RecoveryDeleteWarning
            : p.RecoveryCreatedAt is { } at ? string.Format(Strings.Settings_Account_RecoverySet, AcDate(at)) : Strings.Settings_Account_RecoveryNone;
        void Generate() => _ = AcRunValue(t => account.CreateRecoveryCodeAsync(t), code => { acRecCode = code; acRecSaved = false; });
        rows.Add(Row(Strings.Settings_Account_RecoveryCode, recDesc, acRecCode != null ? []
            : p.RecoveryCreatedAt != null
                ? [AcArmed("rec-gen", Strings.Common_Regenerate, Strings.Settings_Account_RecoveryConfirmRegenerate, "refresh", Generate),
                   AcArmed("rec-del", Strings.Settings_Account_RecoveryDelete, Strings.Settings_Account_RecoveryConfirmDelete, "trash",
                       () => _ = AcRun("rec-del", t => account.DeleteRecoveryCodeAsync(t), null, Strings.Settings_Account_RecoveryDeleted))]
                : [AcButton(Strings.Settings_Account_RecoveryGenerate, "lock", Generate)]));
        if (acRecCode is { } shown) rows.Add(RecoveryPanel(shown));
        Group(Strings.Settings_Account_Security, rows.ToArray());
    }

    async Task AcRunValue<T>(Func<CancellationToken, Task<CloudResult<T>>> action, Action<T> ok)
    {
        if (acBusy != null) return;
        acBusy = "value"; acError = null; Later(0);
        CloudResult<T> r;
        try { r = await action(CancellationToken.None); } catch (Exception) { r = new CloudResult<T>(default, "network"); }
        acBusy = null;
        if (r.Ok && r.Value is { } v) ok(v); else acError = AcErrorText(r);
        Later(0);
    }

    // 只显示一次的恢复码：大字等宽，前后各 4 组；复制、存成文本文件，勾选「已妥善保存」后才能点完成
    FrameworkElement RecoveryPanel(string code)
    {
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(0, 6, 0, 10) };
        panel.Children.Add(Text(Strings.Settings_Account_RecoveryShowTitle, LabelProto));
        panel.Children.Add(Text(Strings.Settings_Account_RecoveryShowDescription, DescProto));
        var compact = code.Replace("-", "");
        string Groups(string s) => string.Join("-", Enumerable.Range(0, s.Length / 4).Select(i => s.Substring(i * 4, 4)));
        var big = new TextBlock
        {
            Text = compact.Length == 32 ? $"{Groups(compact[..16])}\n{Groups(compact[16..])}" : code, FontSize = 20, LineHeight = 30, CharacterSpacing = 60,
            FontFamily = (FontFamily)Application.Current.Resources["PaneFontMono"], Foreground = LabelProto.Foreground, IsTextSelectionEnabled = true,
        };
        panel.Children.Add(big);
        var actions = new Grid { ColumnSpacing = 8 };
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) actions.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        var copy = Button(Strings.Common_Copy, "copy", () => { AcCopy(code); ShowNotice(Strings.Settings_Account_Copied); });
        var save = Button(Strings.Settings_Account_RecoverySaveFile, "download", () => _ = AcSaveRecovery(code));
        var saved = AcCheck(Strings.Settings_Account_RecoverySaved, acRecSaved, v => { acRecSaved = v; Later(0); });
        var done = AcPrimary(Strings.Common_Done, () => { acRecCode = null; ShowNotice(Strings.Settings_Account_RecoveryCreated); Later(0); });
        Enable(done, acRecSaved);
        Grid.SetColumn(save, 1); Grid.SetColumn(saved, 3); Grid.SetColumn(done, 4);
        saved.VerticalAlignment = VerticalAlignment.Center;
        actions.Children.Add(copy); actions.Children.Add(save); actions.Children.Add(saved); actions.Children.Add(done);
        panel.Children.Add(actions);
        return panel;
    }

    async Task AcSaveRecovery(string code)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "Pane 恢复码" };
        picker.FileTypeChoices.Add("Text", [".txt"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        await Windows.Storage.FileIO.WriteTextAsync(file, $"Pane 恢复码\r\n{code}\r\n\r\n忘记密码时，在重设密码页填写它可保留云端数据。不要发给任何人。\r\n");
    }
}
