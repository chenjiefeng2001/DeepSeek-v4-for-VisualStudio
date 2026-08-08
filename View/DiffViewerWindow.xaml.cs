using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.ComponentModel;
using System.Windows;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// VS SDK 原生差异对比浮窗。
    /// 内部委托给 <see cref="InlineDiffHostControl"/>，自身仅作为 Window 壳。
    /// </summary>
    public partial class DiffViewerWindow : Window
    {
        private readonly Action? _onAccept;
        private readonly Action? _onUndo;
        private bool _isClosing;
        private readonly string _oldContent;
        private readonly string _newContent;

        public DiffViewerWindow(
            string oldContent,
            string newContent,
            string? title = null,
            Action? onAccept = null,
            Action? onUndo = null)
        {
            InitializeComponent();

            _oldContent = oldContent ?? string.Empty;
            _newContent = newContent ?? string.Empty;
            _onAccept = onAccept;
            _onUndo = onUndo;

            if (!string.IsNullOrEmpty(title))
                Title = $"{LocalizationService.Instance["diff.windowTitle"]} — {title}";

            DiffHost.OnAccept = () => { try { _onAccept?.Invoke(); } catch { } CloseWindow(); };
            DiffHost.OnUndo = () => { try { _onUndo?.Invoke(); } catch { } CloseWindow(); };

            Loaded += OnLoaded;
        }

        public void SetViewerHandle(DiffViewerHandle handle)
        {
            Loaded -= OnLoaded;
            DiffHost.SetViewerHandle(handle);
        }

        public void PerformUndo() { try { _onUndo?.Invoke(); } catch { } }
        public void PerformAccept() { try { _onAccept?.Invoke(); } catch { } }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            try
            {
                var viewer = DiffViewerService.Instance.CreateDiffViewer(
                    _oldContent, _newContent,
                    viewMode: Microsoft.VisualStudio.Text.Differencing.DifferenceViewMode.Inline);
                DiffHost.SetViewer(viewer);
            }
            catch (Exception ex)
            {
                Logger.Error($"[DiffViewerWindow] 创建查看器失败: {ex.Message}", ex);
                DiffHost.SetStatusText(string.Format(
                    LocalizationService.Instance["diff.createFailed"], ex.Message));
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_isClosing) return;
            _isClosing = true;
        }

        private void CloseWindow()
        {
            _isClosing = true;
            Close();
        }
    }
}
