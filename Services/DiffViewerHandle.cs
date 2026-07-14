using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.IO;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 托管只读 Diff Viewer 及其关联的所有缓冲区、临时文件和事件订阅。
    /// 调用方必须通过 <see cref="Dispose"/> 释放所有资源，不能只关闭 WPF Window。
    /// </summary>
    public sealed class DiffViewerHandle : IDisposable
    {
        /// <summary>冻结的原始内容缓冲区（只读显示用）</summary>
        public ITextBuffer BaselineBuffer { get; }

        /// <summary>建议内容缓冲区（只读显示用）</summary>
        public ITextBuffer ProposalBuffer { get; }

        /// <summary>差异缓冲区</summary>
        public IDifferenceBuffer DifferenceBuffer { get; }

        /// <summary>WPF 差异查看器（VisualElement 可嵌入任意 WPF 容器）</summary>
        public IWpfDifferenceViewer Viewer { get; }

        // 临时文件路径（仅当使用 ITextDocument 后端 buffer 时不为 null）
        private readonly string? _tempBaselineFile;
        private readonly string? _tempProposalFile;

        private bool _disposed;

        public DiffViewerHandle(
            ITextBuffer baselineBuffer,
            ITextBuffer proposalBuffer,
            IDifferenceBuffer differenceBuffer,
            IWpfDifferenceViewer viewer,
            string? tempBaselineFile = null,
            string? tempProposalFile = null)
        {
            BaselineBuffer = baselineBuffer ?? throw new ArgumentNullException(nameof(baselineBuffer));
            ProposalBuffer = proposalBuffer ?? throw new ArgumentNullException(nameof(proposalBuffer));
            DifferenceBuffer = differenceBuffer ?? throw new ArgumentNullException(nameof(differenceBuffer));
            Viewer = viewer ?? throw new ArgumentNullException(nameof(viewer));

            _tempBaselineFile = tempBaselineFile;
            _tempProposalFile = tempProposalFile;

            Viewer.Closed += OnViewerClosed;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Viewer.Closed -= OnViewerClosed;

            try
            {
                if (!Viewer.IsClosed)
                    Viewer.Close();
            }
            catch (Exception) { /* ignore */ }

            TryDeleteTempFile(_tempBaselineFile);
            TryDeleteTempFile(_tempProposalFile);
        }

        private void OnViewerClosed(object? sender, EventArgs e)
        {
            Dispose();
        }

        private static void TryDeleteTempFile(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { /* ignore */ }
        }
    }
}
