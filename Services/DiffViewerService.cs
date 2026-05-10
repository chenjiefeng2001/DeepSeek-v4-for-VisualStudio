using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// VS SDK 原生 Diff 查看器服务。
    /// 使用 <see cref="IDifferenceBufferFactoryService"/> + <see cref="IWpfDifferenceViewerFactoryService"/>
    /// 创建 VS 内置的差异对比视图（支持内联/并排模式、自动红绿着色、滚动同步）。
    /// 对标方案三：完全依赖 VS SDK <c>Microsoft.VisualStudio.Text.Differencing</c> 命名空间。
    /// </summary>
    public class DiffViewerService : IDisposable
    {
        #region Singleton

        private static DiffViewerService? _instance;
        private static readonly object _instanceLock = new();

        public static DiffViewerService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new DiffViewerService();
                    }
                }
                return _instance;
            }
        }

        #endregion

        #region Fields

        private readonly Dictionary<string, DiffViewerSession> _sessions = new();
        private readonly object _sessionsLock = new();

        private IComponentModel? _componentModel;
        private IDifferenceBufferFactoryService? _bufferFactory;
        private IWpfDifferenceViewerFactoryService? _viewerFactory;
        private ITextBufferFactoryService? _textBufferFactory;
        private IContentTypeRegistryService? _contentTypeRegistry;
        private IEditorOptionsFactoryService? _editorOptionsFactory;
        private ITextDocumentFactoryService? _textDocumentFactory;

        #endregion

        #region MEF Service Resolution

        /// <summary>
        /// 延迟解析 MEF 服务（避免在包初始化阶段因 MEF 未就绪而失败）。
        /// </summary>
        private void EnsureServices()
        {
            if (_bufferFactory != null && _viewerFactory != null)
                return;

            ThreadHelper.ThrowIfNotOnUIThread();

            _componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
            if (_componentModel == null)
                throw new InvalidOperationException("IComponentModel 不可用，MEF 容器未初始化。");

            var exportProvider = _componentModel.DefaultExportProvider;

            // 获取 IDifferenceBufferFactoryService（优先 v3 → v2 → v1）
            _bufferFactory = exportProvider.GetExport<IDifferenceBufferFactoryService>()?.Value
                ?? exportProvider.GetExport<IDifferenceBufferFactoryService2>()?.Value
                ?? (IDifferenceBufferFactoryService?)exportProvider.GetExport<IDifferenceBufferFactoryService3>()?.Value;

            _viewerFactory = exportProvider.GetExport<IWpfDifferenceViewerFactoryService>()?.Value;

            _textBufferFactory = exportProvider.GetExport<ITextBufferFactoryService>()?.Value;

            _contentTypeRegistry = exportProvider.GetExport<IContentTypeRegistryService>()?.Value;

            _editorOptionsFactory = exportProvider.GetExport<IEditorOptionsFactoryService>()?.Value;

            _textDocumentFactory = exportProvider.GetExport<ITextDocumentFactoryService>()?.Value;

            if (_bufferFactory == null)
                throw new InvalidOperationException("无法获取 IDifferenceBufferFactoryService。");
            if (_viewerFactory == null)
                throw new InvalidOperationException("无法获取 IWpfDifferenceViewerFactoryService。");
            if (_textBufferFactory == null)
                throw new InvalidOperationException("无法获取 ITextBufferFactoryService。");
            if (_contentTypeRegistry == null)
                throw new InvalidOperationException("无法获取 IContentTypeRegistryService。");
        }

        #endregion

        #region Public API - Diff Viewer

        /// <summary>
        /// 为指定的新旧代码创建 IDifferenceViewer，自动着色。
        /// 返回 <see cref="IWpfDifferenceViewer"/>，其 <c>VisualElement</c> 可嵌入任意 WPF 容器。
        /// </summary>
        /// <param name="oldContent">修改前的原始代码</param>
        /// <param name="newContent">AI 生成的新代码</param>
        /// <param name="contentType">内容类型（默认 "code"）</param>
        /// <param name="viewMode">查看模式（Inline 或 SideBySide）</param>
        /// <returns>WPF 差异查看器实例</returns>
        public IWpfDifferenceViewer CreateDiffViewer(
            string oldContent,
            string newContent,
            string contentType = "code",
            DifferenceViewMode viewMode = DifferenceViewMode.Inline)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsureServices();

            IContentType ct = _contentTypeRegistry!.GetContentType(contentType);

            // 创建左右两个 ITextBuffer（优先使用文件后端 buffer，避免第三方 margin 崩溃）
            ITextBuffer leftBuffer;
            ITextBuffer rightBuffer;
            string? tempLeftFile = null;
            string? tempRightFile = null;

            try
            {
                if (_textDocumentFactory != null)
                {
                    // 使用临时文件创建带 ITextDocument 的 buffer（兼容 FileEncoding 等第三方扩展）
                    // 注意：ITextDocument 不能 dispose，否则 buffer 会丢失文件关联
                    tempLeftFile = WriteTempFile(oldContent ?? string.Empty);
                    tempRightFile = WriteTempFile(newContent ?? string.Empty);

                    var leftDoc = _textDocumentFactory.CreateAndLoadTextDocument(tempLeftFile, ct);
                    var rightDoc = _textDocumentFactory.CreateAndLoadTextDocument(tempRightFile, ct);
                    leftBuffer = leftDoc.TextBuffer;
                    rightBuffer = rightDoc.TextBuffer;

                    return CreateDiffViewer(leftBuffer, rightBuffer, viewMode,
                        tempLeftFile, tempRightFile);
                }
                else
                {
                    // 回退：纯内存 buffer
                    leftBuffer = _textBufferFactory!.CreateTextBuffer(oldContent ?? string.Empty, ct);
                    rightBuffer = _textBufferFactory.CreateTextBuffer(newContent ?? string.Empty, ct);

                    return CreateDiffViewer(leftBuffer, rightBuffer, viewMode, null, null);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[DiffViewer] 创建文件后端 buffer 失败，回退内存 buffer: {ex.Message}");

                // 回退：使用纯内存 buffer
                leftBuffer = _textBufferFactory!.CreateTextBuffer(oldContent ?? string.Empty, ct);
                rightBuffer = _textBufferFactory.CreateTextBuffer(newContent ?? string.Empty, ct);

                return CreateDiffViewer(leftBuffer, rightBuffer, viewMode, null, null);
            }
        }

        /// <summary>
        /// 基于已有的左右 <see cref="ITextBuffer"/> 创建差异查看器。
        /// </summary>
        private IWpfDifferenceViewer CreateDiffViewer(
            ITextBuffer leftBuffer,
            ITextBuffer rightBuffer,
            DifferenceViewMode viewMode,
            string? tempLeftFile,
            string? tempRightFile)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // 2. 配置 diff 选项
            var diffOptions = new StringDifferenceOptions
            {
                DifferenceType = StringDifferenceTypes.Line | StringDifferenceTypes.Word,
                IgnoreTrimWhiteSpace = false,
                WordSplitBehavior = WordSplitBehavior.Default,
            };

            // 3. 创建 IDifferenceBuffer
            IDifferenceBuffer diffBuffer = _bufferFactory!.CreateDifferenceBuffer(leftBuffer, rightBuffer);
            diffBuffer.DifferenceOptions = diffOptions;

            // 4. 创建编辑器选项
            IEditorOptions editorOptions = _editorOptionsFactory!.GetOptions(diffBuffer.InlineBuffer);

            // 5. 创建 IWpfDifferenceViewer（内置的文本视图可能触发第三方 margin 初始化）
            IWpfDifferenceViewer viewer;
            try
            {
                viewer = _viewerFactory!.CreateDifferenceView(diffBuffer, editorOptions);
            }
            catch (Exception ex)
            {
                Logger.Error($"[DiffViewer] CreateDifferenceView 失败: {ex.Message}", ex);
                throw new InvalidOperationException(
                    "无法创建差异查看器。如果安装了 FileEncoding 扩展，请暂时禁用。", ex);
            }

            // 6. 设置查看模式（可能触发第三方 margin → 用 try-catch 防护）
            try
            {
                if (viewer is IDifferenceViewer3 viewer3)
                    viewer3.ViewMode = viewMode;
                else if (viewer is IDifferenceViewer2 viewer2)
                    viewer2.ViewMode = viewMode;
                else
                    viewer.ViewMode = viewMode;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[DiffViewer] 设置 ViewMode 失败（第三方扩展冲突），使用默认模式: {ex.Message}");
                // 不抛出，使用默认模式继续
            }

            // 7. 注册临时文件清理
            if (tempLeftFile != null || tempRightFile != null)
            {
                viewer.Closed += (s, e) =>
                {
                    TryDeleteTempFile(tempLeftFile);
                    TryDeleteTempFile(tempRightFile);
                };
            }

            Logger.Info($"[DiffViewer] 创建差异查看器 (mode={viewMode}, " +
                $"leftLines={leftBuffer.CurrentSnapshot.LineCount}, " +
                $"rightLines={rightBuffer.CurrentSnapshot.LineCount})");

            return viewer;
        }

        /// <summary>
        /// 为已有编辑器 buffer 创建差异查看器会话。
        /// 将当前 buffer 内容视为"新"，传入的 originalContent 视为"旧"。
        /// </summary>
        /// <param name="sessionKey">唯一标识此会话的键（如文件路径）</param>
        /// <param name="originalContent">修改前的原始代码</param>
        /// <param name="currentBuffer">编辑器当前 buffer（包含新代码）</param>
        /// <param name="viewMode">查看模式</param>
        /// <returns>创建的 WPF 差异查看器</returns>
        public IWpfDifferenceViewer CreateDiffViewerForBuffer(
            string sessionKey,
            string originalContent,
            ITextBuffer currentBuffer,
            DifferenceViewMode viewMode = DifferenceViewMode.Inline)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsureServices();

            IContentType ct = currentBuffer.ContentType;

            // 左侧 buffer：用临时文件后端（兼容第三方 margin）
            ITextBuffer leftBuffer;
            string? tempLeftFile = null;

            if (_textDocumentFactory != null)
            {
                tempLeftFile = WriteTempFile(originalContent ?? string.Empty);
                var leftDoc = _textDocumentFactory.CreateAndLoadTextDocument(tempLeftFile, ct);
                leftBuffer = leftDoc.TextBuffer;
            }
            else
            {
                leftBuffer = _textBufferFactory!.CreateTextBuffer(originalContent ?? string.Empty, ct);
            }

            // 右侧 buffer：当前编辑器 buffer（已有 ITextDocument）
            IWpfDifferenceViewer viewer = CreateDiffViewer(leftBuffer, currentBuffer, viewMode, tempLeftFile, null);

            var session = new DiffViewerSession
            {
                SessionKey = sessionKey,
                LeftBuffer = leftBuffer,
                RightBuffer = currentBuffer,
                Viewer = viewer,
                OriginalContent = originalContent,
            };

            lock (_sessionsLock)
            {
                _sessions[sessionKey] = session;
            }

            return viewer;
        }

        /// <summary>
        /// 关闭指定键的 diff 查看器会话。
        /// </summary>
        public void CloseSession(string sessionKey)
        {
            DiffViewerSession? session;
            lock (_sessionsLock)
            {
                if (!_sessions.TryGetValue(sessionKey, out session))
                    return;
                _sessions.Remove(sessionKey);
            }

            try
            {
                if (!session.Viewer.IsClosed)
                    session.Viewer.Close();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[DiffViewer] 关闭会话异常: {ex.Message}");
            }

            Logger.Info($"[DiffViewer] 已关闭会话: {sessionKey}");
        }

        /// <summary>
        /// 获取指定键的活跃会话。
        /// </summary>
        public DiffViewerSession? GetSession(string sessionKey)
        {
            lock (_sessionsLock)
            {
                return _sessions.TryGetValue(sessionKey, out var session) ? session : null;
            }
        }

        /// <summary>
        /// 关闭所有会话。
        /// </summary>
        public void CloseAllSessions()
        {
            List<DiffViewerSession> sessions;
            lock (_sessionsLock)
            {
                sessions = new List<DiffViewerSession>(_sessions.Values);
                _sessions.Clear();
            }

            foreach (var session in sessions)
            {
                try
                {
                    if (!session.Viewer.IsClosed)
                        session.Viewer.Close();
                }
                catch { /* ignore */ }
            }

            Logger.Info($"[DiffViewer] 已关闭所有会话 ({sessions.Count} 个)");
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// 将内容写入临时文件并返回路径（用于创建 <see cref="ITextDocument"/> 后端 buffer）。
        /// </summary>
        private static string WriteTempFile(string content)
        {
            string tempPath = Path.GetTempFileName();
            File.WriteAllText(tempPath, content, System.Text.Encoding.UTF8);
            return tempPath;
        }

        /// <summary>
        /// 尝试删除临时文件（忽略删除失败）。
        /// </summary>
        private static void TryDeleteTempFile(string? tempPath)
        {
            if (string.IsNullOrEmpty(tempPath)) return;
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[DiffViewer] 清理临时文件失败: {tempPath} — {ex.Message}");
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            CloseAllSessions();
            _instance = null;
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Diff 查看器会话。
    /// </summary>
    public class DiffViewerSession
    {
        /// <summary>唯一标识此会话的键。</summary>
        public string SessionKey { get; set; } = string.Empty;

        /// <summary>左侧文本缓冲区（旧内容）。</summary>
        public ITextBuffer LeftBuffer { get; set; } = null!;

        /// <summary>右侧文本缓冲区（新内容）。</summary>
        public ITextBuffer RightBuffer { get; set; } = null!;

        /// <summary>WPF 差异查看器实例。</summary>
        public IWpfDifferenceViewer Viewer { get; set; } = null!;

        /// <summary>原始内容（用于撤销）。</summary>
        public string OriginalContent { get; set; } = string.Empty;
    }

    #endregion
}
