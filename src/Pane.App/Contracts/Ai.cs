// AI：界面和 AI 服务之间的接口（设计负责人定义，工程实现）。依据 docs/design.md「AI」一节的草案。
// 范围：翻译、单轮提问、对文字的预设动作；不保存对话历史。界面开发期间用 DesignData/AiSample。
namespace Pane.Contracts;

public enum AiStatus { NotConfigured, Ready, NeedsSignIn }

/// <summary>预设动作：一条提示词模板，变量只有 {text} 和 {lang}。Glyph 是 Lucide 图标名。</summary>
public sealed record AiAction(string Id, string Name, string Glyph, string PromptTemplate);

/// <param name="ActionId">预设动作 Id；单轮提问用 "ask"。</param>
/// <param name="Question">单轮提问的问题；预设动作时为 null。</param>
public sealed record AiRequest(string ActionId, string Text, string? TargetLanguage, string? Question);

public sealed record AiModel(string Id, string DisplayName);

public interface IAiService
{
    AiStatus Status { get; }
    event EventHandler? StatusChanged;

    /// <summary>当前使用的模型显示名，结果卡片标题行右侧显示；未配置时为 null。</summary>
    string? CurrentModelName { get; }

    /// <summary>预设动作（翻译、总结、润色、解释……），按设置里的顺序。</summary>
    IReadOnlyList<AiAction> Actions { get; }

    /// <summary>流式返回文本片段；取消即中止请求。失败抛下面的带类型异常。</summary>
    IAsyncEnumerable<string> RunAsync(AiRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellationToken);

    // ---- 2026-10-29 加：二级窗口（翻译、AI 对话）。默认实现只为了在工程实现前能编译，docs/specs/021 里替换 ----

    /// <summary>
    /// 翻译用的模型显示名（设置 → AI 里单独指定的快模型；没指定时和 <see cref="CurrentModelName"/> 一样）。
    /// ActionId 为 "translate" 的请求一律用这个模型。翻译窗口底部、超级面板自动翻译的标题行显示它。
    /// </summary>
    string? TranslationModelName => CurrentModelName;

    /// <summary>当前模型的 Id（对话窗口的模型下拉框据此标出选中项）；未配置时为 null。</summary>
    string? CurrentModelId => null;

    /// <summary>
    /// 多轮对话（AI 对话窗口）：每次带上整段对话，最后一条是用户的新问题；回答流式返回，取消即中止。
    /// modelId 为 null 用当前模型，否则用 <see cref="ListModelsAsync"/> 里的某个 Id（只对这次请求生效，不改设置）。
    /// 不保存任何历史；失败抛和 RunAsync 一样的带类型异常。
    /// </summary>
    IAsyncEnumerable<string> ChatAsync(IReadOnlyList<AiChatMessage> messages, string? modelId, CancellationToken cancellationToken)
        => throw new NotSupportedException("AI 对话还没接上"); // 不翻译：异常
}

public enum AiChatRole { User, Assistant }

/// <summary>对话里的一条消息。只有文字，不带附件。</summary>
public sealed record AiChatMessage(AiChatRole Role, string Text);

/// <summary>ChatGPT 账号登录后的信息。CanUsePlan 为 false 表示免费账号：能登录，但用不了套餐额度，设置页要明确提示。</summary>
public sealed record AiAccount(string Email, string? PlanName, bool CanUsePlan);

/// <summary>
/// 设置页「AI」用的配置操作（接入方式、地址、模型、动作这些普通设置走 SettingsStore 的 AiSettings；
/// 这里只放凭据和需要联网的操作）。凭据用 DPAPI 加密存本机，任何时候都不返回明文。
/// </summary>
public interface IAiSetup
{
    /// <summary>已保存的 API Key 的掩码（如「sk-ab…wxyz」）；没保存为 null。</summary>
    string? ApiKeyMask { get; }

    /// <summary>保存 API Key（加密）；空字符串表示删除。</summary>
    void SaveApiKey(string key);

    /// <summary>用当前设置发一个极小的请求，确认地址、Key / 登录、模型都能用。失败抛 AiException。</summary>
    Task TestAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 用当前代理设置连一下当前服务（042）：API 方式连接口地址，ChatGPT 方式连 chatgpt.com；3 秒超时，不发送用户数据和 Key。
    /// 失败抛 AiNetworkException，消息是给用户看的中文原因（连不上代理 / 代理拒绝 / 超时 / 目标连不上）。默认实现退回「测试连接」。
    /// </summary>
    Task TestProxyAsync(CancellationToken cancellationToken) => TestAsync(cancellationToken);

    /// <summary>ChatGPT 账号；没登录为 null。</summary>
    AiAccount? Account { get; }

    /// <summary>打开系统浏览器授权，等本机回调完成登录。取消即放弃（不留下半个登录状态）。</summary>
    Task SignInAsync(CancellationToken cancellationToken);

    /// <summary>退出登录：删掉本机保存的登录凭据（能撤销的令牌顺带撤销）。</summary>
    Task SignOutAsync(CancellationToken cancellationToken);

    /// <summary>Key、登录状态变化。可能在后台线程触发。</summary>
    event EventHandler? Changed;
}

/// <summary>失败类别（界面据此显示对应文案和按钮：重试 / 重新登录 / 去设置）。</summary>
public abstract class AiException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class AiNetworkException(string message, Exception? inner = null) : AiException(message, inner);
public sealed class AiCredentialException(string message) : AiException(message);
public sealed class AiSignInRequiredException(string message) : AiException(message);
public sealed class AiQuotaException(string message) : AiException(message);
