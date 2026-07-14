using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Services.Editing
{
    /// <summary>
    /// Agent 多步编辑的内存暂存文件系统。
    ///
    /// 解决的问题：
    /// Agent 可能连续调用多个编辑工具，后续工具需要看到前一个工具产生的修改。
    /// 如果第一步不写盘，第二步读取磁盘就会读到旧内容。
    ///
    /// 使用方式：
    /// 1. Agent 开始时创建 Workspace。
    /// 2. 所有编辑工具通过 Workspace 读取/写入，不直接操作磁盘。
    /// 3. Agent 完成后调用 <see cref="ToPreparedChangeBatch"/> 生成变更 Batch。
    /// 4. 用户确认后由 Coordinator 提交，用户撤销则丢弃 Workspace。
    ///
    /// 线程安全：所有公开方法通过 _lock 保护。
    /// </summary>
    public sealed class StagedEditWorkspace
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, StagedFile> _stagedFiles
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前暂存的文件数</summary>
        public int StagedCount
        {
            get { lock (_lock) return _stagedFiles.Count; }
        }

        /// <summary>
        /// 读取文件内容。优先返回暂存版本，无暂存时从磁盘读取并登记 Baseline。
        /// </summary>
        public string ReadFile(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);

            lock (_lock)
            {
                if (_stagedFiles.TryGetValue(normalizedPath, out var staged))
                    return staged.CurrentContent;

                // 首次读取：从磁盘加载并登记 Baseline
                string baselineContent = File.Exists(normalizedPath)
                    ? File.ReadAllText(normalizedPath)
                    : string.Empty;

                var baselineHash = !string.IsNullOrEmpty(baselineContent)
                    ? ComputeSha256(baselineContent)
                    : string.Empty;

                var baselineWriteTime = File.Exists(normalizedPath)
                    ? File.GetLastWriteTimeUtc(normalizedPath)
                    : (DateTime?)null;

                _stagedFiles[normalizedPath] = new StagedFile
                {
                    FilePath = normalizedPath,
                    BaselineContent = baselineContent,
                    BaselineHash = baselineHash,
                    BaselineLastWriteTimeUtc = baselineWriteTime,
                    CurrentContent = baselineContent,
                    Operation = ProposedFileOperation.Modify,
                };

                return baselineContent;
            }
        }

        /// <summary>
        /// 写入暂存内容（不写盘）。
        /// 首次写入时自动登记文件。
        /// </summary>
        public void WriteFile(string filePath, string newContent)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("文件路径不能为空", nameof(filePath));

            var normalizedPath = NormalizePath(filePath);

            lock (_lock)
            {
                if (_stagedFiles.TryGetValue(normalizedPath, out var staged))
                {
                    staged.CurrentContent = newContent ?? string.Empty;
                }
                else
                {
                    // 新文件：Baseline 为空
                    _stagedFiles[normalizedPath] = new StagedFile
                    {
                        FilePath = normalizedPath,
                        BaselineContent = string.Empty,
                        BaselineHash = string.Empty,
                        BaselineLastWriteTimeUtc = null,
                        CurrentContent = newContent ?? string.Empty,
                        Operation = ProposedFileOperation.Add,
                    };
                }

                Logger.Info($"[StagedWorkspace] 暂存: {Path.GetFileName(normalizedPath)} " +
                    $"({_stagedFiles[normalizedPath].CurrentContent.Length} chars)");
            }
        }

        /// <summary>
        /// 删除文件（暂存删除，不立即操作磁盘）。
        /// </summary>
        public void DeleteFile(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);

            lock (_lock)
            {
                if (_stagedFiles.TryGetValue(normalizedPath, out var staged))
                {
                    staged.Operation = ProposedFileOperation.Delete;
                    staged.CurrentContent = string.Empty;
                }
                else
                {
                    _stagedFiles[normalizedPath] = new StagedFile
                    {
                        FilePath = normalizedPath,
                        BaselineContent = File.Exists(normalizedPath)
                            ? File.ReadAllText(normalizedPath) : string.Empty,
                        CurrentContent = string.Empty,
                        Operation = ProposedFileOperation.Delete,
                    };
                }
            }
        }

        /// <summary>
        /// 获取当前暂存的完整内容（用于工具链中后续步骤读取）。
        /// 无暂存时返回 null（调用方需自行从磁盘读取）。
        /// </summary>
        public string? GetStagedContent(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);

            lock (_lock)
            {
                return _stagedFiles.TryGetValue(normalizedPath, out var staged)
                    ? staged.CurrentContent
                    : null;
            }
        }

        /// <summary>
        /// 将所有暂存变更转换为可提交的 Batch。
        /// 仅包含有实际修改的文件（Baseline != Current）。
        /// </summary>
        public PreparedChangeBatch ToPreparedChangeBatch()
        {
            lock (_lock)
            {
                var changes = _stagedFiles.Values
                    .Where(f => f.BaselineContent != f.CurrentContent || f.Operation != ProposedFileOperation.Modify)
                    .Select(f => new PreparedChangeSet
                    {
                        FilePath = f.FilePath,
                        Operation = f.Operation,
                        BaselineText = f.BaselineContent,
                        BaselineHash = f.BaselineHash,
                        BaselineLastWriteTimeUtc = f.BaselineLastWriteTimeUtc,
                        ProposedText = f.CurrentContent,
                    })
                    .ToList();

                return new PreparedChangeBatch { Changes = changes };
            }
        }

        /// <summary>
        /// 丢弃所有暂存内容（用户撤销后调用）。
        /// </summary>
        public void Discard()
        {
            lock (_lock)
            {
                _stagedFiles.Clear();
            }
            Logger.Info("[StagedWorkspace] 已丢弃所有暂存内容");
        }

        private static string NormalizePath(string path)
            => Path.GetFullPath(path.Trim());

        private static string ComputeSha256(string content)
        {
            using var sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// 单个暂存文件的内部状态。
        /// </summary>
        private sealed class StagedFile
        {
            public string FilePath { get; set; } = string.Empty;
            public string BaselineContent { get; set; } = string.Empty;
            public string BaselineHash { get; set; } = string.Empty;
            public DateTime? BaselineLastWriteTimeUtc { get; set; }
            public string CurrentContent { get; set; } = string.Empty;
            public ProposedFileOperation Operation { get; set; } = ProposedFileOperation.Modify;
        }
    }
}
