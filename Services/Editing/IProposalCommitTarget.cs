using DeepSeek_v4_for_VisualStudio.Models;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Editing
{
    /// <summary>
    /// 提交目标抽象。
    /// 不同场景使用不同实现：
    /// - 文档已在 VS 编辑器中打开 → <see cref="OpenBufferCommitTarget"/>
    /// - 文档存在于磁盘但未打开 → <see cref="FileCommitTarget"/>
    /// - 新建文件 → <see cref="NewFileCommitTarget"/>
    ///
    /// 所有提交必须经过此接口，不允许绕过。
    /// </summary>
    public interface IProposalCommitTarget
    {
        /// <summary>
        /// 提交前预检。检查目标是否仍处于可提交状态（未被外部修改等）。
        /// 此方法不应修改任何文件或缓冲区。
        /// </summary>
        Task<PreflightResult> PreflightAsync(
            PreparedChangeSet change,
            CancellationToken cancellationToken);

        /// <summary>
        /// 执行提交。仅应在 <see cref="PreflightAsync"/> 返回 <see cref="PreflightResult.CanProceed"/> 后调用。
        /// 实现负责：创建备份 → 写入 → 验证 → 清理/回滚。
        /// </summary>
        Task<ApplyResult> CommitAsync(
            PreparedChangeSet change,
            CancellationToken cancellationToken);

        /// <summary>
        /// 回滚已提交的变更（best-effort）。
        /// 用于 Batch 提交中某个文件失败时需要回退已提交的文件。
        /// </summary>
        Task RollbackAsync(CancellationToken cancellationToken);
    }
}
