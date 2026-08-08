using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// Inline Diff 宿主控件。
    /// 包含工具栏（模式切换 / 导航 / 保留撤销）和 Diff Viewer 宿主区域。
    /// 可嵌入 Window、ToolWindow、DocumentTab 等任意 WPF 容器。
    /// </summary>
    public partial class InlineDiffHostControl : UserControl
    {
        #region Fields

        private IWpfDifferenceViewer? _viewer;

        /// <summary>用户点击「保留」时的回调</summary>
        public Action? OnAccept { get; set; }

        /// <summary>用户点击「撤销」时的回调</summary>
        public Action? OnUndo { get; set; }

        #endregion

        #region Constructor

        public InlineDiffHostControl()
        {
            InitializeComponent();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 直接注入预创建的 <see cref="IWpfDifferenceViewer"/>。
        /// </summary>
        public void SetViewer(IWpfDifferenceViewer viewer)
        {
            _viewer = viewer ?? throw new ArgumentNullException(nameof(viewer));
            DiffViewerHost.Child = viewer.VisualElement;

            // 订阅差异变化事件以更新统计
            _viewer.DifferenceBuffer.SnapshotDifferenceChanged += OnSnapshotDifferenceChanged;
            UpdateStats();
        }

        /// <summary>
        /// 通过预创建的 <see cref="DiffViewerHandle"/> 注入 Viewer。
        /// </summary>
        public void SetViewerHandle(DiffViewerHandle handle)
        {
            if (handle == null) throw new ArgumentNullException(nameof(handle));
            SetViewer(handle.Viewer);
        }

        /// <summary>
        /// 更新底部状态栏文本。
        /// </summary>
        public void SetStatusText(string text)
        {
            BottomStatusLabel.Text = text;
        }

        #endregion

        #region Button Handlers

        private void InlineModeButton_Click(object sender, RoutedEventArgs e)
        {
            SetViewMode(DifferenceViewMode.Inline);
            InlineModeButton.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78));
            InlineModeButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x6A, 0x9A));
            SideBySideModeButton.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            SideBySideModeButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        }

        private void SideBySideModeButton_Click(object sender, RoutedEventArgs e)
        {
            SetViewMode(DifferenceViewMode.SideBySide);
            SideBySideModeButton.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78));
            SideBySideModeButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x6A, 0x9A));
            InlineModeButton.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            InlineModeButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        }

        private void PrevDiffButton_Click(object sender, RoutedEventArgs e)
        {
            try { _viewer?.ScrollToPreviousChange(wrap: true); }
            catch (Exception ex) { Logger.Warn($"[DiffHost] 导航失败: {ex.Message}"); }
        }

        private void NextDiffButton_Click(object sender, RoutedEventArgs e)
        {
            try { _viewer?.ScrollToNextChange(wrap: true); }
            catch (Exception ex) { Logger.Warn($"[DiffHost] 导航失败: {ex.Message}"); }
        }

        private void AcceptButton_Click(object sender, RoutedEventArgs e)
        {
            AcceptButton.IsEnabled = false;
            UndoButton.IsEnabled = false;
            try { OnAccept?.Invoke(); }
            catch (Exception ex) { Logger.Error($"[DiffHost] 保留回调异常: {ex.Message}", ex); }
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            AcceptButton.IsEnabled = false;
            UndoButton.IsEnabled = false;
            try { OnUndo?.Invoke(); }
            catch (Exception ex) { Logger.Error($"[DiffHost] 撤销回调异常: {ex.Message}", ex); }
        }

        #endregion

        #region Private Methods

        private void SetViewMode(DifferenceViewMode mode)
        {
            if (_viewer == null) return;

            ThreadHelper.ThrowIfNotOnUIThread();

            if (_viewer is IDifferenceViewer3 v3)
                v3.ViewMode = mode;
            else if (_viewer is IDifferenceViewer2 v2)
                v2.ViewMode = mode;
            else
                _viewer.ViewMode = mode;
        }

        private void UpdateStats()
        {
            try
            {
                if (_viewer == null || _viewer.IsClosed) return;

                var diff = _viewer.DifferenceBuffer.CurrentSnapshotDifference;
                if (diff == null) return;

                int addedCount = 0;
                int removedCount = 0;

                foreach (var lineDiff in diff.LineDifferences)
                {
                    switch (lineDiff.DifferenceType)
                    {
                        case DifferenceType.Add:
                            addedCount += lineDiff.Right.Length;
                            break;
                        case DifferenceType.Remove:
                            removedCount += lineDiff.Left.Length;
                            break;
                    }
                }

                StatsLabel.Text = $"+{addedCount} 行新增  -{removedCount} 行删除";
            }
            catch
            {
                StatsLabel.Text = LocalizationService.Instance["status.diffCalculating"];
            }
        }

        private void OnSnapshotDifferenceChanged(object? sender, SnapshotDifferenceChangeEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(UpdateStats));
        }

        #endregion
    }
}
