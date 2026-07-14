using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Editing
{
    /// <summary>
    /// 已打开文档的提交目标。
    /// 通过 <see cref="ITextEdit"/> + <see cref="ITextUndoTransaction"/> 写入编辑器缓冲区，
    /// 支持一步 Ctrl+Z 撤销。
    /// </summary>
    public sealed class OpenBufferCommitTarget : IProposalCommitTarget
    {
        private readonly ITextBuffer _sourceBuffer;
        private readonly ITextSnapshot _baselineSnapshot;
        private readonly ITextUndoHistoryRegistry _undoHistoryRegistry;

        // 用于回滚：提交前的内容快照
        private string? _preCommitContent;

        public OpenBufferCommitTarget(
            ITextBuffer sourceBuffer,
            ITextSnapshot baselineSnapshot,
            ITextUndoHistoryRegistry undoHistoryRegistry)
        {
            _sourceBuffer = sourceBuffer ?? throw new ArgumentNullException(nameof(sourceBuffer));
            _baselineSnapshot = baselineSnapshot ?? throw new ArgumentNullException(nameof(baselineSnapshot));
            _undoHistoryRegistry = undoHistoryRegistry ?? throw new ArgumentNullException(nameof(undoHistoryRegistry));
        }

        public Task<PreflightResult> PreflightAsync(
            PreparedChangeSet change, CancellationToken cancellationToken)
        {
            // 快照比较可在任意线程进行
            var currentSnapshot = _sourceBuffer.CurrentSnapshot;

            // 同一版本 → 无冲突
            if (currentSnapshot.Version.VersionNumber == _baselineSnapshot.Version.VersionNumber)
                return Task.FromResult(PreflightResult.Ok());

            // 版本不同但内容哈希相同 → 允许
            if (string.Equals(currentSnapshot.GetText(), _baselineSnapshot.GetText(), StringComparison.Ordinal))
                return Task.FromResult(PreflightResult.Ok());

            return Task.FromResult(PreflightResult.Fail(
                "文档在预览期间已被修改。请重新生成提案或手动合并。",
                ConflictLevel.ContentChanged));
        }

        public async Task<ApplyResult> CommitAsync(
            PreparedChangeSet change, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            try
            {
                // 保存提交前内容（用于回滚）
                _preCommitContent = _sourceBuffer.CurrentSnapshot.GetText();

                var history = _undoHistoryRegistry.RegisterHistory(_sourceBuffer);
                using var transaction = history.CreateTransaction("Apply AI Edit");
                using var edit = _sourceBuffer.CreateEdit();

                if (change.TextChanges.Count > 0)
                {
                    // 结构化修改：从后向前应用
                    ApplyChangesFromEndToStart(edit, change.TextChanges);
                }
                else
                {
                    // 整文件替换
                    var snapshot = _sourceBuffer.CurrentSnapshot;
                    if (snapshot.Length > 0)
                        edit.Replace(0, snapshot.Length, change.ProposedText);
                    else
                        edit.Insert(0, change.ProposedText);
                }

                ITextSnapshot appliedSnapshot = edit.Apply();

                if (appliedSnapshot == null)
                    return ApplyResult.Failed(change.FilePath, "ITextEdit.Apply() 失败");

                transaction.Complete();

                // 如果需要立即保存
                if (change.SaveBehavior == ProposalSaveBehavior.SaveImmediately)
                {
                    if (_sourceBuffer.Properties.TryGetProperty(
                        typeof(ITextDocument), out ITextDocument textDoc))
                    {
                        await Task.Run(() => textDoc.Save(), cancellationToken);
                    }
                }

                Logger.Info($"[OpenBuffer] 已提交: {System.IO.Path.GetFileName(change.FilePath)}");
                return ApplyResult.Ok(change.FilePath);
            }
            catch (Exception ex)
            {
                Logger.Error($"[OpenBuffer] 提交失败: {change.FilePath} — {ex.Message}", ex);
                return ApplyResult.Failed(change.FilePath, ex.Message);
            }
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_preCommitContent == null) return Task.CompletedTask;

                using var edit = _sourceBuffer.CreateEdit();
                var snapshot = _sourceBuffer.CurrentSnapshot;
                if (snapshot.Length > 0)
                    edit.Replace(0, snapshot.Length, _preCommitContent);
                else
                    edit.Insert(0, _preCommitContent);
                edit.Apply();

                Logger.Info($"[OpenBuffer] 已回滚 Buffer");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[OpenBuffer] 回滚失败: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 从后向前应用结构化修改，避免前面的修改影响后续修改的位置。
        /// </summary>
        private static void ApplyChangesFromEndToStart(
            ITextEdit edit, System.Collections.Generic.IReadOnlyList<ProposedTextChange> changes)
        {
            // 按 Offset 降序排序（从后向前）
            var sorted = new List<ProposedTextChange>(changes);
            sorted.Sort((a, b) => b.Offset.CompareTo(a.Offset));

            foreach (var ch in sorted)
            {
                if (ch.Offset < 0 || ch.Offset > edit.Snapshot.Length)
                    continue;

                int length = Math.Min(ch.Length, edit.Snapshot.Length - ch.Offset);

                if (length > 0)
                {
                    if (!string.IsNullOrEmpty(ch.NewText))
                        edit.Replace(ch.Offset, length, ch.NewText);
                    else
                        edit.Delete(ch.Offset, length);
                }
                else if (!string.IsNullOrEmpty(ch.NewText))
                {
                    edit.Insert(ch.Offset, ch.NewText);
                }
            }
        }
    }
}
