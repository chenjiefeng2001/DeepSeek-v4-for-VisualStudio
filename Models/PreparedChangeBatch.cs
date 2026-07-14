using System;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// 多文件变更提案批次。
    /// 一个 Batch 内的所有文件作为一个原子单元处理：
    /// 全部预览通过 → 全部提交，任一冲突 → 全部不提交。
    /// </summary>
    public sealed class PreparedChangeBatch
    {
        public string BatchId { get; set; } = Guid.NewGuid().ToString("N");
        public IReadOnlyList<PreparedChangeSet> Changes { get; set; }
            = Array.Empty<PreparedChangeSet>();
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? StepDescription { get; set; }
    }
}
