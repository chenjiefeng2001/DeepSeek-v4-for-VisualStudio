using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// DeepSeek Chat 主控件，对标共享项目 ucChat。
    /// 宿主 WebView2（Chromium），采用增量渲染模式：
    /// - 首次加载使用 NavigateToString 构建完整页面
    /// - 后续消息通过 ExecuteScriptAsync 调用 JS 增量追加
    /// - 流式输出时通过 BuildStreamingUpdateJs 实时更新 DOM，消除全页刷新闪烁
    /// </summary>
    public partial class DeepSeekChatControl : System.Windows.Controls.UserControl, IDisposable
    {
        #region Constants

        private const string WelcomeMessage =
            "你好！我是 DeepSeek Chat，你的 AI 编程助手。\n\n" +
            "我可以帮你：\n- 解释代码\n- 修复 Bug\n- 重构代码\n- 生成测试\n- 回答技术问题\n\n开始提问吧！";

        private const string ApiKeyMissingMessage =
            "⚠️ **未配置 API 密钥**\n\n" +
            "请通过菜单 **工具 → 选项 → DeepSeek Chat** 配置你的 DeepSeek API 密钥。\n\n" +
            "获取密钥：https://platform.deepseek.com/api_keys";

        /// <summary>
        /// 流式更新间隔（字符数），每累积这么多字符触发一次 DOM 更新。
        /// </summary>
        private const int StreamRenderInterval = 15;

        #endregion

        #region Properties

        private DeepSeek_v4_for_VisualStudioPackage? _package;
        private DeepSeekOptionsPage? _options;
        private DeepSeekApiService? _apiService;
        private CancellationTokenSource? _currentStreamingCts;
        private string? _solutionPath;

        private readonly List<ChatMessage> _messages = new();
        private readonly List<ChatApiMessage> _conversationHistory = new();
        private bool _isGenerating;

        // ── 增量渲染状态（对标 Turbo ucChat） ──
        private bool _browserInitialized;
        private int _lastRenderedMessagesLength;
        private readonly StringBuilder _messagesHtml = new();

        #endregion

        #region Constructors

        /// <summary>
        /// 初始化控件。
        /// </summary>
        public DeepSeekChatControl()
        {
            InitializeComponent();

            // 初始化模型和推理强度下拉框
            ModelComboBox.ItemsSource = new[] { "deepseek-v4-pro", "deepseek-v4-flash" };
            ModelComboBox.SelectedIndex = 0;

            EffortComboBox.ItemsSource = new[] { "high", "max" };
            EffortComboBox.SelectedIndex = 0;

            ThinkingCheckBox.IsChecked = true;

            // 注册 WebView2 事件
            ChatWebView.CoreWebView2InitializationCompleted += ChatWebView_CoreWebView2InitializationCompleted;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 工具窗口创建完成后调用，传入 Package 引用。
        /// 对标 TerminalWindowTurbo.OnCreate() → StartControl()。
        /// </summary>
        public void StartControl(DeepSeek_v4_for_VisualStudioPackage package)
        {
            _package = package;
            _options = package.Options;

            InitializeApiService();
            _ = ResolveSolutionPathAsync();
            _ = LoadAndShowAsync();
        }

        #endregion

        #region Private Methods - Initialization

        private void InitializeApiService()
        {
            if (_options == null || string.IsNullOrEmpty(_options.ApiKey))
                return;

            _apiService?.Dispose();
            _apiService = new DeepSeekApiService(_options.ApiKey, _options.SelectedModel);
            _apiService.ConfigureThinking(_options.IsThinkingEnabled, _options.ReasoningEffort);
            Logger.Info("API 服务初始化成功");
        }

        private async Task ResolveSolutionPathAsync()
        {
            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = (EnvDTE.DTE)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE));
                _solutionPath = dte?.Solution?.FullName;
                if (!string.IsNullOrEmpty(_solutionPath))
                    Logger.Info($"检测到解决方案: {_solutionPath}");
                else
                    Logger.Info("未检测到已打开的解决方案，使用默认存储");
            }
            catch (Exception ex)
            {
                Logger.Error("解析解决方案路径失败", ex);
                _solutionPath = null;
            }
        }

        private async Task LoadAndShowAsync()
        {
            _messagesHtml.Clear();
            _lastRenderedMessagesLength = 0;

            // 加载对话历史
            var loaded = ChatPersistenceService.Load(_solutionPath);
            if (loaded != null && loaded.Count > 0)
            {
                Logger.Info($"[Render] LoadConversation: 从磁盘加载了 {loaded.Count} 条消息");
                _messages.Clear();
                _conversationHistory.Clear();

                foreach (var msg in loaded)
                {
                    msg.IsStreaming = false;
                    _messages.Add(msg);

                    if (msg.Role is "user" or "assistant")
                    {
                        _conversationHistory.Add(new ChatApiMessage
                        {
                            Role = msg.Role,
                            Content = msg.Content ?? string.Empty,
                        });
                    }
                }
            }

            // 没有消息则显示欢迎语
            if (_messages.Count == 0)
            {
                bool hasApiKey = _options != null && !string.IsNullOrEmpty(_options.ApiKey);
                string welcomeContent = hasApiKey ? WelcomeMessage : ApiKeyMissingMessage;

                var welcomeMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = welcomeContent,
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                _messages.Add(welcomeMsg);
                Logger.Info(hasApiKey ? "[Render] 添加欢迎语" : "[Render] 添加欢迎语 + API密钥缺失警告");
            }

            await InitializeWebViewAsync();
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                Logger.Info("[Render] 开始初始化 WebView2 CoreWebView2 环境");
                var env = await CoreWebView2Environment.CreateAsync(
                    null,
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DeepSeekVS", "WebView2"));
                await ChatWebView.EnsureCoreWebView2Async(env);
                Logger.Info("[Render] CoreWebView2 环境初始化成功");
            }
            catch (Exception ex)
            {
                Logger.Error("[Render] WebView2 初始化失败", ex);
                StatusLabel.Text = $"WebView2 初始化失败: {ex.Message}";
            }
        }

        #endregion

        #region Private Methods - Rendering

        /// <summary>
        /// 增量更新浏览器内容。
        /// 对标 ucChat.UpdateBrowser()：首次使用 NavigateToString，
        /// 后续通过 ExecuteScriptAsync 调用 window.__appendMessageHtml 增量追加。
        /// </summary>
        private async void UpdateBrowser()
        {
            if (ChatWebView.CoreWebView2 == null)
                return;

            try
            {
                string allMessages = _messagesHtml.ToString();

                // ── 增量更新路径 ──
                if (_browserInitialized && allMessages.Length > _lastRenderedMessagesLength)
                {
                    string delta = allMessages.Substring(_lastRenderedMessagesLength);
                    string jsFragment = System.Text.Json.JsonSerializer.Serialize(delta);

                    try
                    {
                        string script = $"window.__appendMessageHtml({jsFragment});";
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(script);
                        _lastRenderedMessagesLength = allMessages.Length;
                        return;
                    }
                    catch
                    {
                        // 增量更新失败时回退到全量刷新
                    }
                }

                // ── 全量刷新路径 ──
                string html = ChatHtmlService.BuildInitialPage(_messages);
                ChatWebView.CoreWebView2.NavigateToString(html);
                _browserInitialized = true;
                _lastRenderedMessagesLength = allMessages.Length;
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] UpdateBrowser 异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 构建消息 HTML 片段并追加到 _messagesHtml，然后更新浏览器。
        /// </summary>
        private void AddMessagesHtml(string role, string content, string? reasoningContent = null)
        {
            if (role == "user")
            {
                _messagesHtml.Append(ChatHtmlService.BuildUserMessageHtml(content));
            }
            else
            {
                var tempMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = content,
                    ReasoningContent = reasoningContent ?? string.Empty,
                    IsStreaming = false,
                };
                _messagesHtml.Append(ChatHtmlService.BuildAssistantMessageHtml(tempMsg, _messages.Count - 1));
            }
        }

        /// <summary>
        /// CoreWebView2 初始化完成回调。
        /// </summary>
        private void ChatWebView_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                Logger.Info("[Render] CoreWebView2InitializationCompleted: 成功");
                ChatWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                // 构建初始 HTML 内容
                RebuildMessagesHtml();
                Dispatcher.Invoke(() => UpdateBrowser());
            }
            else
            {
                Logger.Error($"[Render] CoreWebView2 初始化失败: {e.InitializationException?.Message}", e.InitializationException);
                Dispatcher.Invoke(() =>
                {
                    StatusLabel.Text = $"WebView2 初始化失败: {e.InitializationException?.Message}";
                });
            }
        }

        /// <summary>
        /// 根据 _messages 列表重建 _messagesHtml。
        /// </summary>
        private void RebuildMessagesHtml()
        {
            _messagesHtml.Clear();
            for (int i = 0; i < _messages.Count; i++)
            {
                var msg = _messages[i];
                if (msg.Role == "user")
                {
                    _messagesHtml.Append(ChatHtmlService.BuildUserMessageHtml(msg.Content ?? string.Empty));
                }
                else
                {
                    _messagesHtml.Append(ChatHtmlService.BuildAssistantMessageHtml(msg, i));
                }
            }
            _lastRenderedMessagesLength = 0;
        }

        #endregion

        #region Private Methods - API Interaction

        private async void SendMessageAsync()
        {
            if (_isGenerating) return;

            var userText = InputTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(userText))
                return;

            // 校验 API 密钥
            if (_options == null || string.IsNullOrEmpty(_options.ApiKey))
            {
                var warningMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = ApiKeyMissingMessage,
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                _messages.Add(warningMsg);
                AddMessagesHtml("assistant", ApiKeyMissingMessage);
                UpdateBrowser();
                StatusLabel.Text = "⚠️ 请先配置 API 密钥 (工具 → 选项 → DeepSeek Chat)";
                return;
            }

            // 热重载 API 服务
            InitializeApiService();
            if (_apiService == null) return;

            InputTextBox.Text = string.Empty;

            // ── 添加用户消息 ──
            var userMsg = new ChatMessage
            {
                Role = "user",
                Content = userText,
                Timestamp = DateTime.Now,
            };
            _messages.Add(userMsg);
            _conversationHistory.Add(new ChatApiMessage { Role = "user", Content = userText });

            // ── 创建助手消息占位 ──
            var assistantMsg = new ChatMessage
            {
                Role = "assistant",
                Content = string.Empty,
                ReasoningContent = string.Empty,
                Timestamp = DateTime.Now,
                IsStreaming = true,
                IsRendered = false,
            };
            _messages.Add(assistantMsg);
            int assistantMsgIndex = _messages.Count - 1;

            // ── 批量构建 HTML（用户消息 + 助手占位），仅调用一次 UpdateBrowser 避免竞态重复渲染 ──
            AddMessagesHtml("user", userText);
            AddMessagesHtml("assistant", string.Empty);
            UpdateBrowser();

            _isGenerating = true;
            UpdateButtonsState();
            StatusLabel.Text = "DeepSeek 思考中…";

            _currentStreamingCts?.Cancel();
            _currentStreamingCts?.Dispose();
            _currentStreamingCts = new CancellationTokenSource();

            try
            {
                var requestMessages = BuildRequestMessages();
                var reasoningBuffer = new StringBuilder();
                var contentBuffer = new StringBuilder();
                int streamRenderTick = 0;
                int lastReasoningLength = 0;

                await foreach (var chunk in _apiService.ChatStreamAsync(requestMessages, _currentStreamingCts.Token))
                {
                    if (chunk.StartsWith("[THINKING]"))
                    {
                        var thinking = chunk.Substring(10);
                        reasoningBuffer.Append(thinking);
                        StatusLabel.Text = "DeepSeek 深度思考中…";

                        // 定期更新思考面板
                        if (reasoningBuffer.Length - lastReasoningLength >= 80)
                        {
                            assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                            lastReasoningLength = reasoningBuffer.Length;
                            await UpdateStreamingMessageAsync(assistantMsgIndex,
                                contentBuffer.ToString(),
                                reasoningBuffer.ToString(),
                                isComplete: false);
                        }
                    }
                    else
                    {
                        if (reasoningBuffer.Length > 0 && lastReasoningLength < reasoningBuffer.Length)
                        {
                            assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                            lastReasoningLength = reasoningBuffer.Length;
                        }

                        contentBuffer.Append(chunk);
                        streamRenderTick += chunk.Length;
                        StatusLabel.Text = "DeepSeek 回复中...";

                        if (streamRenderTick >= StreamRenderInterval)
                        {
                            streamRenderTick = 0;
                            assistantMsg.Content = contentBuffer.ToString();
                            await UpdateStreamingMessageAsync(assistantMsgIndex,
                                contentBuffer.ToString(),
                                reasoningBuffer.ToString(),
                                isComplete: false);
                        }
                    }
                }

                // ── 流式完成：渲染最终 Markdown ──
                assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                assistantMsg.Content = contentBuffer.ToString();
                assistantMsg.IsStreaming = false;

                Logger.Info($"[Render] 流式结束: 内容长度={contentBuffer.Length}, 思考长度={reasoningBuffer.Length}");

                string finalJs = ChatHtmlService.BuildFinalRenderJs(
                    assistantMsgIndex,
                    contentBuffer.ToString(),
                    reasoningBuffer.ToString());

                await ChatWebView.CoreWebView2.ExecuteScriptAsync(finalJs);

                _conversationHistory.Add(new ChatApiMessage { Role = "assistant", Content = contentBuffer.ToString() });

                // 后台持久化
                var capturedMsg = assistantMsg;
                var capturedMessages = _messages.ToList();
                _ = Task.Run(() =>
                {
                    capturedMsg.HtmlContent = "rendered";
                    capturedMsg.IsRendered = true;
                    ChatPersistenceService.Save(_solutionPath, capturedMessages);
                });
            }
            catch (OperationCanceledException)
            {
                Logger.Info("[Render] 用户停止生成");
                assistantMsg.Content += "\n\n*[已停止]*";
                assistantMsg.IsStreaming = false;
                await UpdateStreamingMessageAsync(assistantMsgIndex,
                    assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] API 出错", ex);
                assistantMsg.Content = $"抱歉，发生了错误，请重试。\n\n```\n{ex.Message}\n```";
                assistantMsg.IsStreaming = false;
                await UpdateStreamingMessageAsync(assistantMsgIndex,
                    assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
            }
            finally
            {
                assistantMsg.IsStreaming = false;
                _isGenerating = false;
                StatusLabel.Text = string.Empty;
                _currentStreamingCts?.Dispose();
                _currentStreamingCts = null;
                UpdateButtonsState();
            }
        }

        /// <summary>
        /// 通过 JS 增量更新流式消息的 DOM 内容。
        /// </summary>
        private async Task UpdateStreamingMessageAsync(int messageIndex, string content, string reasoningContent, bool isComplete)
        {
            if (ChatWebView.CoreWebView2 == null) return;

            try
            {
                string js = ChatHtmlService.BuildStreamingUpdateJs(messageIndex, content, reasoningContent, isComplete);
                await ChatWebView.CoreWebView2.ExecuteScriptAsync(js);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] UpdateStreamingMessage 异常: {ex.Message}", ex);
            }
        }

        private List<ChatApiMessage> BuildRequestMessages()
        {
            var messages = new List<ChatApiMessage>();

            if (!string.IsNullOrWhiteSpace(_options?.SystemPrompt))
            {
                messages.Add(new ChatApiMessage { Role = "system", Content = _options.SystemPrompt });
            }

            messages.AddRange(_conversationHistory.Select(m => new ChatApiMessage
            {
                Role = m.Role,
                Content = m.Content,
            }));

            return messages;
        }

        private void StopGeneration()
        {
            _currentStreamingCts?.Cancel();
        }

        private async void ClearConversationAsync()
        {
            if (_isGenerating)
            {
                _currentStreamingCts?.Cancel();
                _isGenerating = false;
                UpdateButtonsState();
            }

            _messages.Clear();
            _conversationHistory.Clear();
            _messagesHtml.Clear();
            _lastRenderedMessagesLength = 0;
            ChatPersistenceService.Delete(_solutionPath);

            var welcomeMsg = new ChatMessage
            {
                Role = "assistant",
                Content = WelcomeMessage,
                Timestamp = DateTime.Now,
                IsRendered = true,
            };
            _messages.Add(welcomeMsg);

            RebuildMessagesHtml();
            _browserInitialized = false;
            UpdateBrowser();
            Logger.Info("清空对话完成");
        }

        #endregion

        #region Private Methods - Helpers

        private void UpdateButtonsState()
        {
            SendButton.IsEnabled = !_isGenerating;
            StopButton.Visibility = _isGenerating ? Visibility.Visible : Visibility.Collapsed;
            SendButton.Visibility = _isGenerating ? Visibility.Collapsed : Visibility.Visible;
            InputTextBox.IsReadOnly = _isGenerating;
        }

        #endregion

        #region Event Handlers - Input

        /// <summary>
        /// 输入框键盘事件：Enter 直接发送消息，Ctrl+Enter 插入换行。
        /// </summary>
        private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    // Ctrl+Enter: 插入换行
                    e.Handled = false;
                    return;
                }

                // 普通 Enter: 发送消息
                e.Handled = true;
                SendMessageAsync();
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessageAsync();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopGeneration();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClearConversationAsync();
        }

        private void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            // 停止当前生成
            if (_isGenerating)
            {
                _currentStreamingCts?.Cancel();
                _isGenerating = false;
                UpdateButtonsState();
            }

            // 保存当前对话
            if (_messages.Count > 1)
            {
                ChatPersistenceService.Save(_solutionPath, _messages);
            }

            // 清空并开始新对话
            _messages.Clear();
            _conversationHistory.Clear();
            _messagesHtml.Clear();
            _lastRenderedMessagesLength = 0;

            var welcomeMsg = new ChatMessage
            {
                Role = "assistant",
                Content = WelcomeMessage,
                Timestamp = DateTime.Now,
                IsRendered = true,
            };
            _messages.Add(welcomeMsg);

            RebuildMessagesHtml();
            _browserInitialized = false;
            UpdateBrowser();

            InputTextBox.Focus();
            Logger.Info("开始新对话");
        }

        private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_apiService != null && ModelComboBox.SelectedItem is string model)
            {
                _apiService.UpdateModel(model);
                Logger.Info($"模型切换为: {model}");
            }
        }

        private void ThinkingCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_apiService != null)
            {
                bool enabled = ThinkingCheckBox.IsChecked == true;
                string effort = EffortComboBox.SelectedItem as string ?? "high";
                _apiService.ConfigureThinking(enabled, effort);
                Logger.Info($"思考模式: {(enabled ? "启用" : "禁用")}, 强度: {effort}");
            }
        }

        private void EffortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_apiService != null && EffortComboBox.SelectedItem is string effort)
            {
                bool enabled = ThinkingCheckBox.IsChecked == true;
                _apiService.ConfigureThinking(enabled, effort);
                Logger.Info($"推理强度切换为: {effort}");
            }
        }

        #endregion

        #region Event Handlers - WebView2

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(message)) return;

                var obj = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(message);
                if (obj.TryGetProperty("type", out var typeProp))
                {
                    string type = typeProp.GetString() ?? string.Empty;
                    if (type == "applyCode")
                    {
                        string code = obj.TryGetProperty("code", out var codeProp)
                            ? codeProp.GetString() ?? string.Empty : string.Empty;
                        ApplyCodeToActiveDocument(code);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"WebMessage 处理异常: {ex.Message}", ex);
            }
        }

        private void ApplyCodeToActiveDocument(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;

            _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    var dte = (EnvDTE.DTE)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE));
                    var doc = dte?.ActiveDocument;
                    if (doc != null)
                    {
                        var textDoc = (EnvDTE.TextDocument)doc.Object("TextDocument");
                        var editPoint = textDoc.StartPoint.CreateEditPoint();
                        editPoint.ReplaceText(textDoc.EndPoint, code, 0);
                        Logger.Info("代码已应用到活动文档");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"应用代码失败: {ex.Message}", ex);
                }
            });
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// 释放资源，保存对话。
        /// </summary>
        public void Dispose()
        {
            _currentStreamingCts?.Cancel();
            _currentStreamingCts?.Dispose();
            _apiService?.Dispose();

            if (_messages.Count > 0)
            {
                ChatPersistenceService.Save(_solutionPath, _messages);
            }

            Logger.Info("DeepSeekChatControl 已释放");
        }

        #endregion
    }
}
