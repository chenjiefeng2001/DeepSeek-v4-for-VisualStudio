using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Collections.Generic;
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

        /// <summary>用户点击「撤销某块」时的回调（参数：块索引）</summary>
        public Action<int>? OnRevertHunk { get; set; }

        /// <summary>当前显示的 hunks</summary>
        private IReadOnlyList<DiffHunkInfo> _hunks = Array.Empty<DiffHunkInfo>();

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

            // 解除 host 自身的旧 child
            DiffViewerHost.Child = null;

            var veil = viewer.VisualElement;

            // 若该元素已有逻辑父级（例如之前被 attach 到另一窗口/host），先从中解除
            // WPF 不允许同一元素同时是多个元素的逻辑子元素，必须 detach 前一处
            TryDetachFromParent(veil);

            DiffViewerHost.Child = veil;

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

        /// <summary>
        /// 设置并显示逐块撤销的变更块列表。
        /// </summary>
        /// <param name="hunks">变更块列表</param>
        /// <param name="filePath">文件路径（用于显示文件名）</param>
        public void SetHunks(IReadOnlyList<DiffHunkInfo> hunks, string? filePath = null)
        {
            _hunks = hunks ?? Array.Empty<DiffHunkInfo>();

            if (_hunks.Count == 0)
            {
                HunkListBorder.Visibility = Visibility.Collapsed;
                HunkList.Items.Clear();
                return;
            }

            HunkListBorder.Visibility = Visibility.Visible;
            HunkList.Items.Clear();

            string fileName = System.IO.Path.GetFileName(filePath ?? string.Empty);
            HunkListHeader.Text = $"{fileName} — {LocalizationService.Instance["diff.hunkListHeader"]} ({_hunks.Count})";

            for (int i = 0; i < _hunks.Count; i++)
            {
                HunkList.Items.Add(CreateHunkItem(i, _hunks[i]));
            }
        }

        /// <summary>
        /// 创建单个 hunk 的展示项（含「撤销此块」/「保留此块」按钮）。
        /// </summary>
        private UIElement CreateHunkItem(int index, DiffHunkInfo hunk)
        {
            var panel = new DockPanel
            {
                Margin = new Thickness(2, 2, 2, 0),
                LastChildFill = true,
            };

            // 撤销 / 保留按钮
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 8, 0),
            };
            DockPanel.SetDock(btnPanel, Dock.Right);

            var revertBtn = new Button
            {
                Content = LocalizationService.Instance["diff.revertHunk"],
                Background = new SolidColorBrush(Color.FromRgb(0x8B, 0x2E, 0x2E)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0x40, 0x40)),
                BorderThickness = new Thickness(1),
                MinWidth = 70,
                FontSize = 11,
                Padding = new Thickness(6, 2, 6, 2),
                Tag = index,
                IsEnabled = !hunk.IsReverted,
            };
            revertBtn.Click += (s, e) =>
            {
                int idx = (int)((Button)s!).Tag;
                OnRevertHunk?.Invoke(idx);
            };
            btnPanel.Children.Add(revertBtn);

            // 块描述文本
            string typeLabel = hunk.IsPureInsert
                ? LocalizationService.Instance["diff.hunkTypeInsert"]
                : hunk.IsPureDelete
                    ? LocalizationService.Instance["diff.hunkTypeDelete"]
                    : LocalizationService.Instance["diff.hunkTypeModify"];

            string location = hunk.NewStartLine >= 0
                ? $"L{hunk.NewStartLine + 1}-{hunk.NewStartLine + hunk.NewLineCount}"
                : $"L{hunk.OldStartLine + 1}-{hunk.OldStartLine + hunk.OldLineCount}";

            var text = new TextBlock
            {
                Text = $"[{index + 1}] {typeLabel}  {location}" + (hunk.IsReverted ? "  🔄" : ""),
                Foreground = hunk.IsReverted
                    ? new SolidColorBrush(Color.FromRgb(0x6A, 0xA0, 0x5A))
                    : new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            panel.Children.Add(btnPanel);
            panel.Children.Add(text);

            var container = new Border
            {
                Child = panel,
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3E)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 3, 4, 3),
            };

            return container;
        }

        /// <summary>
        /// 刷新块列表（撤销某块后更新状态）。
        /// </summary>
        public void RefreshHunks(IReadOnlyList<DiffHunkInfo> hunks, string? filePath = null)
        {
            SetHunks(hunks, filePath);
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

        /// <summary>
        /// 若元素已有逻辑/可视化父级，则从父级中解除。WPF 中同一元素不能是多个宿主的孩子。
        /// </summary>
        private static void TryDetachFromParent(System.Windows.UIElement element)
        {
            try
            {
                if (element == null) return;

                var logicalParent = System.Windows.LogicalTreeHelper.GetParent(element);
                if (logicalParent is ContentControl cc && ReferenceEquals(cc.Content, element))
                {
                    cc.Content = null;
                    return;
                }
                if (logicalParent is Panel panel)
                {
                    panel.Children.Remove(element);
                    return;
                }
                if (logicalParent is Decorator decorator && ReferenceEquals(decorator.Child, element))
                {
                    decorator.Child = null;
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[DiffHost] 解除元素父级失败: {ex.Message}");
            }
        }

        #endregion
    }
}
