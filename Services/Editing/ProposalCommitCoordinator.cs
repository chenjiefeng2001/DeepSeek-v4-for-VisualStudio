using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Editing
{
    /// <summary>
    /// 唯一提交协调器。
    /// 所有单文件和批量「保留」操作必须通过此协调器。
    ///
    /// 流程：
    /// 1. 先对整个 Batch 执行 Preflight。
    /// 2. 任一文件冲突时，默认一个都不提交。
    /// 3. 全部 Preflight 通过后才开始 Backup 和 Commit。
    /// 4. 中途失败时对已提交目标执行 best-effort rollback。
    /// </summary>
    public sealed class ProposalCommitCoordinator
    {
        /// <summary>
        /// 提交单个 Session 的变更。
        /// 调用方通常是 InlineDiffSession.CommitAsync()。
        /// </summary>
        public async Task<ApplyResult> CommitSingleAsync(
            InlineDiffSession session, CancellationToken cancellationToken)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));

            // 1. 预检
            var preflight = await session.CommitTarget.PreflightAsync(
                session.Change, cancellationToken);

            if (!preflight.CanProceed)
            {
                Logger.Warn($"[Coordinator] 预检失败: {session.Change.FilePath} — {preflight.Reason}");
                return ApplyResult.Conflict(session.Change.FilePath,
                    preflight.Reason ?? "预检失败");
            }

            // 2. 提交
            return await session.CommitTarget.CommitAsync(
                session.Change, cancellationToken);
        }

        /// <summary>
        /// 批量提交。先全量预检，全部通过后才执行写入。
        /// 任一文件冲突时返回 Rejected，不执行任何写入。
        /// </summary>
        public async Task<BatchApplyResult> CommitBatchAsync(
            PreparedChangeBatch batch, CancellationToken cancellationToken)
        {
            if (batch == null)
                throw new ArgumentNullException(nameof(batch));

            if (batch.Changes.Count == 0)
                return BatchApplyResult.AllOk(Array.Empty<ApplyResult>());

            // ── 阶段 1：全量预检 ──
            var preflightResults = new List<(PreparedChangeSet Change, PreflightResult Result)>();
            var failedPreflights = new List<string>();

            foreach (var change in batch.Changes)
            {
                // 为每个 change 创建对应的 CommitTarget
                var target = CreateCommitTarget(change);
                var preflight = await target.PreflightAsync(change, cancellationToken);

                preflightResults.Add((change, preflight));

                if (!preflight.CanProceed)
                {
                    failedPreflights.Add(
                        $"{System.IO.Path.GetFileName(change.FilePath)}: {preflight.Reason}");
                }
            }

            // ── 任一失败 → 全部拒绝 ──
            if (failedPreflights.Count > 0)
            {
                Logger.Warn($"[Coordinator] Batch 预检失败 ({failedPreflights.Count}/{batch.Changes.Count}):\n" +
                    string.Join("\n", failedPreflights));

                return BatchApplyResult.Rejected(
                    $"以下文件预检失败，所有文件均未提交:\n{string.Join("\n", failedPreflights)}");
            }

            // ── 阶段 2：顺序提交（含回滚保护）──
            var results = new List<ApplyResult>();
            var committedTargets = new List<IProposalCommitTarget>();

            try
            {
                foreach (var (change, _) in preflightResults)
                {
                    var target = CreateCommitTarget(change);
                    var result = await target.CommitAsync(change, cancellationToken);

                    results.Add(result);

                    if (result.Success)
                    {
                        committedTargets.Add(target);
                    }
                    else
                    {
                        // ── 提交失败 → 回滚已提交的 ──
                        Logger.Warn($"[Coordinator] 提交失败: {change.FilePath}，开始回滚已提交的 {committedTargets.Count} 个文件");

                        foreach (var committed in committedTargets)
                        {
                            try { await committed.RollbackAsync(cancellationToken); }
                            catch (Exception ex) { Logger.Warn($"[Coordinator] 回滚失败: {ex.Message}"); }
                        }

                        return BatchApplyResult.Partial(results,
                            $"提交 {System.IO.Path.GetFileName(change.FilePath)} 失败，已回滚所有已提交文件");
                    }
                }

                Logger.Info($"[Coordinator] Batch 提交完成: {results.Count} 文件全部成功");
                return BatchApplyResult.AllOk(results);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Coordinator] Batch 提交异常: {ex.Message}", ex);

                // ── 异常回滚 ──
                foreach (var committed in committedTargets)
                {
                    try { await committed.RollbackAsync(cancellationToken); }
                    catch { /* ignore */ }
                }

                return BatchApplyResult.Partial(results, $"提交异常，已回滚: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据 PreparedChangeSet 创建对应的 CommitTarget。
        /// 注意：此方法是简化版，实际使用时需要根据场景（已打开/未打开/新文件）选择正确的 Target。
        /// 完整实现中应通过 DI 或工厂获取 Target。
        /// </summary>
        private static IProposalCommitTarget CreateCommitTarget(PreparedChangeSet change)
        {
            return change.Operation switch
            {
                ProposedFileOperation.Add => new NewFileCommitTarget(),
                _ => new FileCommitTarget(),
            };
        }
    }
}
