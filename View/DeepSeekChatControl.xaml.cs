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
    /// 直接宿主 WebView2（Chromium），使用 NavigateToString 渲染聊天内容。
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
        private bool _webViewInitialized;

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

            // 初始化 API 服务
            InitializeApiService();

            // 解析解决方案路径
            _ = ResolveSolutionPathAsync();

            // 加载对话历史
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

            // 没有消息则显示欢迎语（含 API 密钥检查提示）
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

            // 初始化 WebView2 环境并渲染
            await InitializeWebViewAsync();
        }

        /// <summary>
        /// 初始化 WebView2 并首次渲染。
        /// 对标 ucChat.UpdateBrowser() 中 EnsureCoreWebView2Async 的逻辑。
        /// </summary>
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
                return;
            }
        }

        #endregion

        #region Private Methods - Rendering

        /// <summary>
        /// 对标 ucChat.UpdateBrowser() + NavigateToString。
        /// </summary>
        private void RenderMessages(bool isStreaming = false)
        {
            if (ChatWebView.CoreWebView2 == null)
                return;

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var msgs = _messages.ToList();
                int userCount = msgs.Count(m => m.Role == "user");
                int asstCount = msgs.Count(m => m.Role == "assistant");
                Logger.Info($"[Render] RenderMessages: {msgs.Count} msgs (user={userCount}, asst={asstCount}), streaming={isStreaming}");

                string html = ChatHtmlService.BuildChatHtml(msgs, isStreaming);
                Logger.Info($"[Render] HTML 构建完成: {html.Length} 字符");

                ChatWebView.CoreWebView2.NavigateToString(html);
                sw.Stop();
                Logger.Info($"[Render] NavigateToString 完成, 耗时={sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] RenderMessages 异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// CoreWebView2 初始化完成回调。
        /// 对标 ucChat 在 EnsureCoreWebView2Async 之后的首次渲染 + WebMessageReceived 注册。
        /// </summary>
        private void ChatWebView_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                Logger.Info("[Render] CoreWebView2InitializationCompleted: 成功");
                ChatWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                // 首次渲染
                Dispatcher.Invoke(() => RenderMessages());
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

        #endregion

        #region Private Methods - API Interaction

        private async void SendMessageAsync()
        {
            if (_isGenerating) return;

            var userText = InputTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(userText))
                return;

            // 校验 API 密钥：未配置时在聊天窗口显示提醒
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
                RenderMessages();
                StatusLabel.Text = "⚠️ 请先配置 API 密钥 (工具 → 选项 → DeepSeek Chat)";
                Logger.Info("[Render] API密钥缺失，已显示提醒");
                return;
            }

            // 热重载 API 服务（API key 可能已更改）
            InitializeApiService();
            if (_apiService == null) return;

            InputTextBox.Text = string.Empty;

            // 添加用户消息
            var userMsg = new ChatMessage
            {
                Role = "user",
                Content = userText,
                Timestamp = DateTime.Now,
            };
            _messages.Add(userMsg);
            _conversationHistory.Add(new ChatApiMessage { Role = "user", Content = userText });
            RenderMessages();

            // 创建助手消息占位
            var assistantMsg = new ChatMessage
            {
                Role = "assistant",
                Content = string.Empty,
                Timestamp = DateTime.Now,
                IsStreaming = true,
                IsRendered = false,
            };
            _messages.Add(assistantMsg);

            _isGenerating = true;
            UpdateButtonsState();

            _currentStreamingCts?.Cancel();
            _currentStreamingCts?.Dispose();
            _currentStreamingCts = new CancellationTokenSource();

            try
            {
                var requestMessages = BuildRequestMessages();
                var reasoningBuffer = new StringBuilder();
                int streamRenderTick = 0;
                int streamRenderCount = 0;
                const int ReasoningFlushInterval = 50;
                const int StreamRenderInterval = 30;

                RenderMessages(isStreaming: true);

                await foreach (var chunk in _apiService.ChatStreamAsync(requestMessages, _currentStreamingCts.Token))
                {
                    if (chunk.StartsWith("[THINKING]"))
                    {
                        var thinking = chunk.Substring(10);
                        reasoningBuffer.Append(thinking);
                        StatusLabel.Text = "DeepSeek 深度思考中…";

                        if (reasoningBuffer.Length >= ReasoningFlushInterval)
                        {
                            assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                            reasoningBuffer.Clear();
                        }
                    }
                    else
                    {
                        if (reasoningBuffer.Length > 0)
                        {
                            assistantMsg.ReasoningContent += reasoningBuffer.ToString();
                            reasoningBuffer.Clear();
                        }

                        assistantMsg.Content += chunk;
                        streamRenderTick += chunk.Length;
                        StatusLabel.Text = "DeepSeek 回复中...";

                        if (streamRenderTick >= StreamRenderInterval)
                        {
                            streamRenderTick = 0;
                            streamRenderCount++;
                            RenderMessages(isStreaming: true);
                        }
                    }
                }

                if (reasoningBuffer.Length > 0)
                    assistantMsg.ReasoningContent += reasoningBuffer.ToString();

                assistantMsg.IsStreaming = false;
                Logger.Info($"[Render] 流式结束: {streamRenderCount} 次增量渲染, 内容长度={assistantMsg.Content.Length}");
                RenderMessages(isStreaming: false);

                _conversationHistory.Add(new ChatApiMessage { Role = "assistant", Content = assistantMsg.Content });

                // 后台持久化
                var capturedMsg = assistantMsg;
                _ = Task.Run(() =>
                {
                    capturedMsg.HtmlContent = MarkdownRenderService.ConvertToHtml(capturedMsg.Content, _messages.IndexOf(capturedMsg));
                    capturedMsg.IsRendered = true;
                    ChatPersistenceService.Save(_solutionPath, _messages.ToList());
                });
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"[Render] 用户停止生成");
                assistantMsg.Content += "\n\n*[已停止]*";
                assistantMsg.IsStreaming = false;
                RenderMessages(isStreaming: false);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] API 出错", ex);
                assistantMsg.Content = $"抱歉，发生了错误，请重试。\n\n```\n{ex.Message}\n```";
                assistantMsg.IsStreaming = false;
                RenderMessages(isStreaming: false);
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

        private void ClearConversation()
        {
            if (_isGenerating)
            {
                _currentStreamingCts?.Cancel();
                _isGenerating = false;
                UpdateButtonsState();
            }

            _messages.Clear();
            _conversationHistory.Clear();
            ChatPersistenceService.Delete(_solutionPath);

            // 重新添加欢迎语
            var welcomeMsg = new ChatMessage
            {
                Role = "assistant",
                Content = WelcomeMessage,
                Timestamp = DateTime.Now,
                IsRendered = true,
            };
            _messages.Add(welcomeMsg);

            RenderMessages();
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

        #region Event Handlers - WebView2

        /// <summary>
        /// 处理 WebView2 JS → C# 消息。
        /// 对标 ucChat.CoreWebView2_WebMessageReceived。
        /// </summary>
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
                Logger.Error($"WebMessageReceived 处理失败: {ex.Message}", ex);
            }
        }

        private void ApplyCodeToActiveDocument(string code)
        {
            if (string.IsNullOrEmpty(code)) return;
            Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    var dte = (EnvDTE.DTE)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE));
                    var doc = dte?.ActiveDocument;
                    if (doc == null)
                    {
                        Logger.Error("没有活动文档可插入代码");
                        return;
                    }

                    var selection = doc.Selection as EnvDTE.TextSelection;
                    selection?.Insert(code);
                    Logger.Info("代码已插入编辑器");
                }
                catch (Exception ex)
                {
                    Logger.Error($"插入代码失败: {ex.Message}", ex);
                }
            });
        }

        #endregion

        #region Event Handlers - UI Controls

        private void SendButton_Click(object sender, RoutedEventArgs e) => SendMessageAsync();

        private void StopButton_Click(object sender, RoutedEventArgs e) => StopGeneration();

        private void ClearButton_Click(object sender, RoutedEventArgs e) => ClearConversation();

        private void InputTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Enter 发送（不按 Shift）
            if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                e.Handled = true;
                SendMessageAsync();
            }
        }

        private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_apiService != null && ModelComboBox.SelectedItem is string model)
            {
                _apiService.UpdateModel(model);
                Logger.Info($"模型切换至: {model}");
            }
        }

        private void ThinkingCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_apiService != null && ThinkingCheckBox.IsChecked.HasValue)
            {
                bool enabled = ThinkingCheckBox.IsChecked.Value;
                string effort = EffortComboBox.SelectedItem as string ?? "high";
                _apiService.ConfigureThinking(enabled, effort);
                EffortComboBox.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"深度思考: {enabled}, 推理强度: {effort}");
            }
        }

        private void EffortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_apiService != null && EffortComboBox.SelectedItem is string effort && ThinkingCheckBox.IsChecked.HasValue)
            {
                _apiService.ConfigureThinking(ThinkingCheckBox.IsChecked.Value, effort);
                Logger.Info($"推理强度切换: {effort}");
            }
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// 释放资源。
        /// </summary>
        public void Dispose()
        {
            Logger.Info("DeepSeekChatControl Dispose");

            // 保存对话
            ChatPersistenceService.Save(_solutionPath, _messages.ToList());

            _currentStreamingCts?.Cancel();
            _currentStreamingCts?.Dispose();
            _apiService?.Dispose();

            ChatWebView.CoreWebView2InitializationCompleted -= ChatWebView_CoreWebView2InitializationCompleted;
            if (ChatWebView.CoreWebView2 != null)
                ChatWebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
        }

        #endregion
    }
}
