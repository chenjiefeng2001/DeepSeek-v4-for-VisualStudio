using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;

namespace DeepSeek_v4_for_VisualStudio.Commands
{
    /// <summary>
    /// 在 "视图 → 其他窗口" 中显示 DeepSeek Chat 工具窗口的命令。
    /// </summary>
    internal sealed class ShowChatWindowCommand
    {
        /// <summary>
        /// 命令 ID（与 VSCommandTable.vsct 中的 cmdidShowChatWindow 一致）。
        /// </summary>
        public const int CommandId = 0x0100;

        /// <summary>
        /// 命令集 GUID（与 VSCommandTable.vsct 中的 guidDeepSeekCmdSet 一致）。
        /// </summary>
        public static readonly Guid CommandSet = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

        /// <summary>
        /// VS Package 引用。
        /// </summary>
        private readonly AsyncPackage _package;

        /// <summary>
        /// 初始化命令并注册到菜单服务。
        /// </summary>
        private ShowChatWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandId = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandId);
            commandService.AddCommand(menuItem);
        }

        /// <summary>
        /// 命令单例。
        /// </summary>
        public static ShowChatWindowCommand Instance { get; private set; }

        /// <summary>
        /// 初始化命令（由 Package.InitializeAsync 调用）。
        /// </summary>
        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new ShowChatWindowCommand(package, commandService);
        }

        /// <summary>
        /// 点击菜单时打开 DeepSeek Chat 工具窗口。
        /// </summary>
        private void Execute(object sender, EventArgs e)
        {
            _ = _package.JoinableTaskFactory.RunAsync(async delegate
            {
                await _package.ShowToolWindowAsync(
                    typeof(View.DeepSeekChatWindowPane),
                    0,
                    create: true,
                    cancellationToken: _package.DisposalToken);
            });
        }
    }
}
