using System;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Services.Editing
{
    /// <summary>
    /// 预检结果：提交前对单个文件进行的可行性检查结果。
    /// </summary>
    public sealed class PreflightResult
    {
        public bool CanProceed { get; set; }
        public string? Reason { get; set; }
        public ConflictLevel Conflict { get; set; } = ConflictLevel.None;

        public static PreflightResult Ok() => new PreflightResult { CanProceed = true };

        public static PreflightResult Fail(string reason, ConflictLevel level = ConflictLevel.ContentChanged)
            => new PreflightResult { CanProceed = false, Reason = reason, Conflict = level };
    }

    /// <summary>
    /// 冲突级别。
    /// </summary>
    public enum ConflictLevel
    {
        /// <summary>无冲突</summary>
        None = 0,

        /// <summary>文件内容已变更</summary>
        ContentChanged = 1,

        /// <summary>文件已被删除</summary>
        FileDeleted = 2,

        /// <summary>文件被重命名/移动</summary>
        FileMoved = 3,

        /// <summary>已有另一个活跃 Session 操作同一文件</summary>
        DuplicateSession = 4,
    }

    /// <summary>
    /// 单文件提交结果。
    /// </summary>
    public sealed class ApplyResult
    {
        public bool Success { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }
        public bool IsConflict { get; set; }

        public static ApplyResult Ok(string filePath) => new ApplyResult { Success = true, FilePath = filePath };

        public static ApplyResult Failed(string filePath, string error)
            => new ApplyResult { Success = false, FilePath = filePath, ErrorMessage = error };

        public static ApplyResult Conflict(string filePath, string reason)
            => new ApplyResult { Success = false, FilePath = filePath, ErrorMessage = reason, IsConflict = true };
    }

    /// <summary>
    /// 多文件批次提交结果。
    /// </summary>
    public sealed class BatchApplyResult
    {
        public bool AllSucceeded { get; set; }
        public IReadOnlyList<ApplyResult> FileResults { get; set; } = Array.Empty<ApplyResult>();
        public string? BatchErrorMessage { get; set; }

        public static BatchApplyResult AllOk(IReadOnlyList<ApplyResult> results)
            => new BatchApplyResult { AllSucceeded = true, FileResults = results };

        public static BatchApplyResult Partial(IReadOnlyList<ApplyResult> results, string? error = null)
            => new BatchApplyResult { AllSucceeded = false, FileResults = results, BatchErrorMessage = error };

        public static BatchApplyResult Rejected(string reason)
            => new BatchApplyResult { AllSucceeded = false, BatchErrorMessage = reason };
    }
}
