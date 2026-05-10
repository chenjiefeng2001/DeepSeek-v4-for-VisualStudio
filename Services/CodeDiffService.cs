using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 代码差异比较服务 — 用于生成 +/- 变化视图。
    /// 实现简化的行级 LCS diff 算法，生成 unified diff 格式和 HTML diff 视图。
    /// </summary>
    public static class CodeDiffService
    {
        #region Public Methods

        /// <summary>
        /// 计算两段文本的行级差异。
        /// </summary>
        /// <param name="oldText">原始文本</param>
        /// <param name="newText">新文本</param>
        /// <returns>差异行列表</returns>
        public static List<DiffLine> ComputeDiff(string oldText, string newText)
        {
            var oldLines = SplitLines(oldText);
            var newLines = SplitLines(newText);
            var result = new List<DiffLine>();

            // 使用简化的 LCS 算法
            var lcs = ComputeLcs(oldLines, newLines);

            int oldIdx = 0, newIdx = 0, lcsIdx = 0;

            while (oldIdx < oldLines.Count || newIdx < newLines.Count)
            {
                if (lcsIdx < lcs.Count)
                {
                    // 跳过 old 中不在 LCS 中的行（删除）
                    while (oldIdx < oldLines.Count &&
                           (oldIdx >= oldLines.Count || lcsIdx >= lcs.Count ||
                            oldLines[oldIdx] != lcs[lcsIdx]))
                    {
                        if (newIdx < newLines.Count && oldIdx < oldLines.Count &&
                            oldLines[oldIdx] == newLines[newIdx])
                        {
                            // 实际是相同行
                            break;
                        }
                        result.Add(new DiffLine
                        {
                            Type = DiffLineType.Deleted,
                            OldLineNumber = oldIdx + 1,
                            NewLineNumber = null,
                            Content = oldLines[oldIdx]
                        });
                        oldIdx++;
                    }

                    // 跳过 new 中不在 LCS 中的行（新增）
                    while (newIdx < newLines.Count &&
                           (newIdx >= newLines.Count || lcsIdx >= lcs.Count ||
                            newLines[newIdx] != lcs[lcsIdx]))
                    {
                        if (oldIdx < oldLines.Count && newIdx < newLines.Count &&
                            oldLines[oldIdx] == newLines[newIdx])
                        {
                            break;
                        }
                        result.Add(new DiffLine
                        {
                            Type = DiffLineType.Added,
                            OldLineNumber = null,
                            NewLineNumber = newIdx + 1,
                            Content = newLines[newIdx]
                        });
                        newIdx++;
                    }
                }

                // 匹配行（Unchanged）
                if (lcsIdx < lcs.Count && oldIdx < oldLines.Count && newIdx < newLines.Count &&
                    oldLines[oldIdx] == lcs[lcsIdx] && newLines[newIdx] == lcs[lcsIdx])
                {
                    result.Add(new DiffLine
                    {
                        Type = DiffLineType.Unchanged,
                        OldLineNumber = oldIdx + 1,
                        NewLineNumber = newIdx + 1,
                        Content = oldLines[oldIdx]
                    });
                    oldIdx++;
                    newIdx++;
                    lcsIdx++;
                }

                // 处理剩余行
                if (lcsIdx >= lcs.Count)
                {
                    while (oldIdx < oldLines.Count)
                    {
                        result.Add(new DiffLine
                        {
                            Type = DiffLineType.Deleted,
                            OldLineNumber = oldIdx + 1,
                            NewLineNumber = null,
                            Content = oldLines[oldIdx]
                        });
                        oldIdx++;
                    }
                    while (newIdx < newLines.Count)
                    {
                        result.Add(new DiffLine
                        {
                            Type = DiffLineType.Added,
                            OldLineNumber = null,
                            NewLineNumber = newIdx + 1,
                            Content = newLines[newIdx]
                        });
                        newIdx++;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 从 Markdown 内容中提取代码块和对应的文件路径。
        /// 支持格式：```language:filepath 或 ```language (filepath)
        /// 或 AI 回复中类似 "在 `xxx.cs` 中修改" 的模式。
        /// </summary>
        /// <param name="markdownContent">AI 的完整回复内容</param>
        /// <returns>代码块列表，包含代码和推断的文件路径</returns>
        public static List<CodeBlockInfo> ExtractCodeBlocks(string markdownContent)
        {
            var result = new List<CodeBlockInfo>();

            if (string.IsNullOrWhiteSpace(markdownContent))
                return result;

            // 匹配 Markdown 代码块: ```language\ncode\n```
            var codeBlockRegex = new Regex(
                @"```(\w+)?(?:\s*[:：]\s*([^\n\r]+))?\s*\n(.*?)```",
                RegexOptions.Singleline);

            var matches = codeBlockRegex.Matches(markdownContent);

            // 先尝试从 AI 回复中提取文件路径引用
            var filePathRefs = new List<string>();
            var fileRefRegex = new Regex(@"`([^`]+\.(cs|py|js|ts|java|cpp|c|h|xml|json|yaml|yml|md|sql|html|css|xaml))`");
            foreach (Match m in fileRefRegex.Matches(markdownContent))
            {
                filePathRefs.Add(m.Groups[1].Value);
            }

            int codeBlockIdx = 0;
            foreach (Match match in matches)
            {
                string language = match.Groups[1].Value?.Trim() ?? "";
                string filePathHint = match.Groups[2].Value?.Trim() ?? "";
                string code = match.Groups[3].Value;

                // 如果没有在代码块标注中指定文件路径，尝试从上下文推断
                if (string.IsNullOrEmpty(filePathHint) && codeBlockIdx < filePathRefs.Count)
                {
                    filePathHint = filePathRefs[codeBlockIdx];
                }

                result.Add(new CodeBlockInfo
                {
                    Language = language,
                    FilePath = filePathHint,
                    Code = code,
                    Index = codeBlockIdx
                });

                codeBlockIdx++;
            }

            return result;
        }

        #endregion

        #region Private Methods

        private static List<string> SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return new List<string>();

            var lines = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                    lines.Add(sb.ToString());
                    sb.Clear();
                }
                else if (text[i] == '\n')
                {
                    lines.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(text[i]);
                }
            }
            lines.Add(sb.ToString());
            return lines;
        }

        /// <summary>
        /// 简化的 LCS (Longest Common Subsequence) 算法。
        /// </summary>
        private static List<string> ComputeLcs(List<string> a, List<string> b)
        {
            int m = a.Count, n = b.Count;

            // 使用 DP 表
            var dp = new int[m + 1, n + 1];

            for (int i = 1; i <= m; i++)
            {
                for (int j = 1; j <= n; j++)
                {
                    if (a[i - 1] == b[j - 1])
                        dp[i, j] = dp[i - 1, j - 1] + 1;
                    else
                        dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                }
            }

            // 回溯
            var lcs = new List<string>();
            int x = m, y = n;
            while (x > 0 && y > 0)
            {
                if (a[x - 1] == b[y - 1])
                {
                    lcs.Insert(0, a[x - 1]);
                    x--;
                    y--;
                }
                else if (dp[x - 1, y] > dp[x, y - 1])
                {
                    x--;
                }
                else
                {
                    y--;
                }
            }

            return lcs;
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// diff 行类型。
    /// </summary>
    public enum DiffLineType
    {
        Unchanged,
        Added,
        Deleted
    }

    /// <summary>
    /// 表示一行 diff 结果。
    /// </summary>
    public class DiffLine
    {
        public DiffLineType Type { get; set; }
        public int? OldLineNumber { get; set; }
        public int? NewLineNumber { get; set; }
        public string Content { get; set; } = string.Empty;
    }

    /// <summary>
    /// 从 AI 回复中提取的代码块信息。
    /// </summary>
    public class CodeBlockInfo
    {
        /// <summary>编程语言</summary>
        public string Language { get; set; } = string.Empty;

        /// <summary>推断的目标文件路径</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>代码内容</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>代码块序号</summary>
        public int Index { get; set; }
    }

    #endregion
}
