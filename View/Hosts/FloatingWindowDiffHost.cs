using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System.Linq;
using System.Windows;

namespace DeepSeek_v4_for_VisualStudio.View.Hosts
{
    /// <summary>
    /// 浮动窗口 Diff 宿主。
    /// 复用现有的 <see cref="DiffViewerWindow"/> 作为第一阶段稳定兜底方案。
    /// 后续可替换为 ToolWindowHost 或 DocumentTabHost。
    /// 支持逐块撤销（通过 workspace 的 hunks）。
    /// </summary>
    public sealed class FloatingWindowDiffHost : IDiffHost
    {
        private DiffViewerWindow? _window;
        private DiffViewerHandle? _currentHandle;

        public void Show(InlineDiffSession session)
        {
            if (_window != null)
            {
                _window.Close();
                _window = null;
            }

            // 确保上一个 handle 已被 Dispose，避免 VisualElement 父级冲突
            if (_currentHandle != null && !ReferenceEquals(_currentHandle, session.ViewerHandle))
            {
                try { _currentHandle.Dispose(); } catch { }
            }

            _window = new DiffViewerWindow(
                session.Change.BaselineText,
                session.Change.ProposedText,
                System.IO.Path.GetFileName(session.Change.FilePath),
                onAccept: async () =>
                {
                    await session.CommitAsync(System.Threading.CancellationToken.None);
                },
                onUndo: () =>
                {
                    session.Dismiss();
                });

            // 使用 Session 提供的 Viewer 替换窗口默认创建的 Viewer
            _currentHandle = session.ViewerHandle;
            _window.SetViewerHandle(session.ViewerHandle);

            // ── 逐块撤销：加载 hunks + 绑定回调 ──
            if (session.Workspace != null)
            {
                var hunks = session.Workspace.GetHunks(session.Change.FilePath);

                _window.SetHunks(hunks, session.Change.FilePath, hunkIndex =>
                {
                    // ── 撤销单块 ──
                    bool ok = session.Workspace!.RestoreSingleHunk(
                        session.Change.FilePath, hunkIndex);

                    if (ok)
                    {
                        // 刷新块列表（更新撤销状态）
                        var updated = session.Workspace.GetHunks(session.Change.FilePath);
                        _window.RefreshHunks(updated, session.Change.FilePath);

                        // ── 同步刷新 Diff Viewer（重新基于新内容）──
                        // 写穿模式下磁盘已改，重新创建只读 preview 显示恢复后的状态
                        string updatedContent = session.Workspace.ReadFile(session.Change.FilePath);
                        var oldHandle = session.ViewerHandle;
                        session.ReplaceViewerHandle(
                            DiffViewerService.Instance.CreateReadOnlyPreview(
                                session.Change.BaselineText, updatedContent, session.Change.ContentTypeName));
                        _currentHandle = session.ViewerHandle;
                        _window.ReplaceViewer(session.ViewerHandle, session);

                        try { oldHandle.Dispose(); } catch { }

                        Logger.Info($"[FloatingHost] 已撤销块 [{hunkIndex}]: {System.IO.Path.GetFileName(session.Change.FilePath)}");
                    }
                });
            }

            _window.Show();
            Logger.Info($"[FloatingHost] Diff 窗口已显示: {session.SessionId.Substring(0, 8)}");
        }

        public void Activate()
        {
            _window?.Activate();
        }

        public void Close()
        {
            if (_window != null)
            {
                _window.Close();
                _window = null;
            }

            if (_currentHandle != null)
            {
                try { _currentHandle.Dispose(); } catch { }
                _currentHandle = null;
            }
        }
    }
}
