using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 工具窗口窗格，对标共享项目 TerminalWindowTurbo。
    /// 宿主 DeepSeekChatControl (WPF UserControl with WebView2)。
    /// </summary>
    [Guid("8F3A9C2D-1E5B-4F6A-9C8D-2E3F5A7B1D4E")]
    public class DeepSeekChatWindowPane : ToolWindowPane
    {
        /// <summary>
        /// 初始化工具窗口。
        /// </summary>
        public DeepSeekChatWindowPane() : base(null)
        {
            this.Caption = "DeepSeek Chat";
            this.Content = new DeepSeekChatControl();
        }

        /// <summary>
        /// 窗口创建完成，将 Package 引用传入 UserControl。
        /// </summary>
        protected override void OnCreate()
        {
            base.OnCreate();
            if (Content is DeepSeekChatControl control)
            {
                control.StartControl((DeepSeek_v4_for_VisualStudioPackage)Package);
            }
        }

        /// <summary>
        /// 窗口销毁时释放资源。
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && Content is IDisposable disposable)
            {
                disposable.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
