namespace DeepSeek_v4_for_VisualStudio.View.Hosts
{
    /// <summary>
    /// Diff 宿主抽象。
    /// 不同宿主类型决定 Diff Viewer 的挂载位置（浮动窗口 / Tool Window / Document Tab / 编辑器内嵌）。
    /// </summary>
    public interface IDiffHost
    {
        /// <summary>显示指定 Session 的 Diff 视图</summary>
        void Show(Services.InlineDiffSession session);

        /// <summary>激活宿主窗口（置于前台）</summary>
        void Activate();

        /// <summary>关闭宿主窗口并释放资源</summary>
        void Close();
    }
}
