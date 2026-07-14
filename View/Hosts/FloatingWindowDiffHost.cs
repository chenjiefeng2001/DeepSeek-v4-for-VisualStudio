using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System.Windows;

namespace DeepSeek_v4_for_VisualStudio.View.Hosts
{
    /// <summary>
    /// 浮动窗口 Diff 宿主。
    /// 复用现有的 <see cref="DiffViewerWindow"/> 作为第一阶段稳定兜底方案。
    /// 后续可替换为 ToolWindowHost 或 DocumentTabHost。
    /// </summary>
    public sealed class FloatingWindowDiffHost : IDiffHost
    {
        private DiffViewerWindow? _window;

        public void Show(InlineDiffSession session)
        {
            if (_window != null)
            {
                _window.Close();
                _window = null;
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
            _window.SetViewerHandle(session.ViewerHandle);

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
        }
    }
}
