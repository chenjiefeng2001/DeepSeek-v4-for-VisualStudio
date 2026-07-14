using System;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    // ========================================================================
    // InlineDiff 提案模型 — prepare-preview-commit 流程的数据载体
    // ========================================================================

    /// <summary>
    /// 建议文件操作类型。
    /// </summary>
    public enum ProposedFileOperation
    {
        Modify,
        Add,
        Delete,
    }

    /// <summary>
    /// 保存行为策略。
    /// </summary>
    public enum ProposalSaveBehavior
    {
        KeepDocumentDirty,
        SaveImmediately,
    }

    /// <summary>
    /// 单次结构化文本修改描述。
    /// </summary>
    public sealed class ProposedTextChange
    {
        public int Offset { get; set; }
        public int Length { get; set; }
        public string NewText { get; set; } = string.Empty;
        public string? MatchedText { get; set; }

        public override string ToString()
            => $"@{Offset} del={Length} ins={NewText.Length}chars";
    }

    /// <summary>
    /// 单文件变更提案。
    /// </summary>
    public sealed class PreparedChangeSet
    {
        public string ChangeId { get; set; } = Guid.NewGuid().ToString("N");
        public string FilePath { get; set; } = string.Empty;
        public ProposedFileOperation Operation { get; set; } = ProposedFileOperation.Modify;
        public string BaselineText { get; set; } = string.Empty;
        public string BaselineHash { get; set; } = string.Empty;
        public DateTime? BaselineLastWriteTimeUtc { get; set; }
        public string ProposedText { get; set; } = string.Empty;
        public IReadOnlyList<ProposedTextChange> TextChanges { get; set; }
            = Array.Empty<ProposedTextChange>();
        public string ContentTypeName { get; set; } = "code";
        public ProposalSaveBehavior SaveBehavior { get; set; } = ProposalSaveBehavior.KeepDocumentDirty;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
