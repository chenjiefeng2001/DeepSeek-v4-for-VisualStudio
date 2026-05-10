using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 统一上下文管理器 — 负责多轮对话历史的存储、拼接与 Token 预算管理。
    /// 
    /// 核心职责：
    /// 1. 单一数据源：所有 API 调用所需的对话历史由此类统一管理
    /// 2. reasoning_content 回传规则（严格遵守 DeepSeek V4 思考模式协议）：
    ///    - 无工具调用的 assistant 消息 → reasoning_content 不需要回传（API 会忽略）
    ///    - 有工具调用的 assistant 消息 → reasoning_content 必须完整回传（否则 400 错误）
    /// 3. Token 预算估算与自动截断
    /// 4. 轮次（Turn）边界追踪
    /// 
    /// 参考：
    /// - https://api-docs.deepseek.com/zh-cn/guides/multi_round_chat
    /// - https://api-docs.deepseek.com/zh-cn/guides/thinking_mode
    /// </summary>
    public class ConversationContextManager
    {
        /// <summary>内部对话历史存储（单一数据源）</summary>
        private readonly List<ContextEntry> _entries = new();

        /// <summary>系统提示词（独立存储，不参与 turn 管理）</summary>
        private string? _systemPrompt;

        /// <summary>搜索结果上下文（独立存储，注入为 system 消息）</summary>
        private string? _searchContext;

        /// <summary>Skill 发现上下文（独立存储）</summary>
        private string? _skillContext;

        /// <summary>当前 Token 估算计数器</summary>
        private int _estimatedTokens;

        /// <summary>Token 预算上限（默认 64K，DeepSeek V4 上下文窗口为 128K，留一半给输出）</summary>
        public int TokenBudget { get; set; } = 64000;

        /// <summary>当超过 Token 预算时自动修剪的最旧轮次数</summary>
        public int AutoTrimTurns { get; set; } = 2;

        /// <summary>获取当前对话轮次数（一个 user 消息 = 一轮）</summary>
        public int TurnCount => _entries.Count(e => e.Role == "user");

        /// <summary>获取消息总条数</summary>
        public int MessageCount => _entries.Count;

        /// <summary>获取估算 Token 数</summary>
        public int EstimatedTokens => _estimatedTokens;

        /// <summary>上下文是否为空（无任何用户消息）</summary>
        public bool IsEmpty => !_entries.Any(e => e.Role == "user");

        #region Core API — 添加消息

        /// <summary>
        /// 设置系统提示词。
        /// </summary>
        public void SetSystemPrompt(string? prompt)
        {
            _systemPrompt = prompt;
        }

        /// <summary>
        /// 设置搜索上下文（注入为 system 消息）。
        /// </summary>
        public void SetSearchContext(string? searchContext)
        {
            _searchContext = searchContext;
        }

        /// <summary>
        /// 设置 Skill 发现上下文。
        /// </summary>
        public void SetSkillContext(string? skillContext)
        {
            _skillContext = skillContext;
        }

        /// <summary>
        /// 添加用户消息。
        /// </summary>
        public void AddUserMessage(string content)
        {
            if (string.IsNullOrEmpty(content)) return;

            _entries.Add(new ContextEntry
            {
                Role = "user",
                Content = content,
                TurnIndex = TurnCount + 1, // 新轮次
            });
            _estimatedTokens += EstimateTokens(content);

            AutoTrimIfNeeded();
        }

        /// <summary>
        /// 添加助手消息。
        /// </summary>
        /// <param name="content">回复内容</param>
        /// <param name="reasoningContent">思维链内容（思考模式下）</param>
        /// <param name="toolCalls">工具调用列表（可为 null）</param>
        public void AddAssistantMessage(string? content, string? reasoningContent = null, List<ToolCall>? toolCalls = null)
        {
            _entries.Add(new ContextEntry
            {
                Role = "assistant",
                Content = content,
                ReasoningContent = reasoningContent,
                ToolCalls = toolCalls,
                HasToolCalls = toolCalls != null && toolCalls.Count > 0,
                TurnIndex = TurnCount, // 属于当前轮次
            });
            _estimatedTokens += EstimateTokens(content);
            if (!string.IsNullOrEmpty(reasoningContent))
                _estimatedTokens += EstimateTokens(reasoningContent);
        }

        /// <summary>
        /// 添加工具调用结果（tool 角色消息）。
        /// </summary>
        public void AddToolResult(string toolCallId, string toolName, string result)
        {
            _entries.Add(new ContextEntry
            {
                Role = "tool",
                Content = result,
                ToolCallId = toolCallId,
                Name = toolName,
                TurnIndex = TurnCount, // 工具调用属于当前轮次
            });
            _estimatedTokens += EstimateTokens(result);
        }

        /// <summary>
        /// 添加自定义角色消息（如 skill 指令注入的 system 消息）。
        /// 这些消息不计入 turn 管理。
        /// </summary>
        public void AddCustomMessage(string role, string content)
        {
            if (string.IsNullOrEmpty(content)) return;

            _entries.Add(new ContextEntry
            {
                Role = role,
                Content = content,
                TurnIndex = -1, // 不属于任何轮次
            });
            _estimatedTokens += EstimateTokens(content);
        }

        #endregion

        #region Core API — 构建 API 消息

        /// <summary>
        /// 构建发送给 DeepSeek API 的完整消息列表。
        /// 正确处理 reasoning_content 回传规则。
        /// 
        /// DeepSeek V4 规则：
        /// - 如果 assistant 消息没有 tool_calls：reasoning_content 不应回传（会被 API 忽略）
        /// - 如果 assistant 消息有 tool_calls：reasoning_content 必须回传（否则 400 错误）
        /// - 所有 tool 角色消息必须保留 tool_call_id 和 name
        /// </summary>
        public List<ChatApiMessage> BuildApiMessages()
        {
            var messages = new List<ChatApiMessage>();

            // ── 1. 组装最终系统提示词 ──
            string? finalSystemPrompt = BuildFinalSystemPrompt();
            if (!string.IsNullOrWhiteSpace(finalSystemPrompt))
            {
                messages.Add(new ChatApiMessage { Role = "system", Content = finalSystemPrompt });
            }

            // ── 2. 注入搜索上下文（作为独立的 system 消息） ──
            if (!string.IsNullOrWhiteSpace(_searchContext))
            {
                messages.Add(new ChatApiMessage { Role = "system", Content = _searchContext });
            }

            // ── 3. 遍历对话历史，正确构建消息 ──
            foreach (var entry in _entries)
            {
                // 跳过没有内容的条目（除非有 tool_calls）
                if (string.IsNullOrEmpty(entry.Content) && (entry.ToolCalls == null || entry.ToolCalls.Count == 0))
                    continue;

                var apiMsg = new ChatApiMessage
                {
                    Role = entry.Role,
                    Content = entry.Content,
                };

                // ── reasoning_content 回传规则 ──
                if (entry.Role == "assistant" && !string.IsNullOrEmpty(entry.ReasoningContent))
                {
                    if (entry.HasToolCalls)
                    {
                        // 有工具调用 → 必须回传 reasoning_content
                        apiMsg.ReasoningContent = entry.ReasoningContent;
                    }
                    // 无工具调用 → 不回传 reasoning_content（API 会忽略）
                }

                // ── 工具调用相关字段 ──
                if (entry.Role == "assistant" && entry.ToolCalls != null && entry.ToolCalls.Count > 0)
                {
                    apiMsg.ToolCalls = entry.ToolCalls;
                }

                if (entry.Role == "tool")
                {
                    if (!string.IsNullOrEmpty(entry.ToolCallId))
                        apiMsg.ToolCallId = entry.ToolCallId;
                    if (!string.IsNullOrEmpty(entry.Name))
                        apiMsg.Name = entry.Name;
                }

                messages.Add(apiMsg);
            }

            return messages;
        }

        /// <summary>
        /// 构建仅包含最近 N 轮的 API 消息列表（用于 Agent 子调用）。
        /// 以 user 消息为轮次边界，保留完整的 tool 调用链。
        /// </summary>
        /// <param name="maxTurns">保留的最大轮次数</param>
        public List<ChatApiMessage> BuildApiMessagesRecentTurns(int maxTurns)
        {
            if (TurnCount <= maxTurns)
                return BuildApiMessages();

            // 找到需要保留的起始 user 消息（倒数第 maxTurns 个）
            int turnsToSkip = TurnCount - maxTurns;
            int userCount = 0;
            int startEntryIdx = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Role == "user")
                {
                    userCount++;
                    if (userCount > turnsToSkip)
                    {
                        startEntryIdx = i;
                        break;
                    }
                }
            }

            // 构建截断后的消息列表
            var messages = new List<ChatApiMessage>();

            // 系统消息始终保留
            string? finalSystemPrompt = BuildFinalSystemPrompt();
            if (!string.IsNullOrWhiteSpace(finalSystemPrompt))
                messages.Add(new ChatApiMessage { Role = "system", Content = finalSystemPrompt });
            if (!string.IsNullOrWhiteSpace(_searchContext))
                messages.Add(new ChatApiMessage { Role = "system", Content = _searchContext });

            // 从 startEntryIdx 开始构建
            for (int i = startEntryIdx; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (string.IsNullOrEmpty(entry.Content) && (entry.ToolCalls == null || entry.ToolCalls.Count == 0))
                    continue;

                var apiMsg = new ChatApiMessage
                {
                    Role = entry.Role,
                    Content = entry.Content,
                };

                if (entry.Role == "assistant" && !string.IsNullOrEmpty(entry.ReasoningContent) && entry.HasToolCalls)
                    apiMsg.ReasoningContent = entry.ReasoningContent;

                if (entry.Role == "assistant" && entry.ToolCalls != null && entry.ToolCalls.Count > 0)
                    apiMsg.ToolCalls = entry.ToolCalls;

                if (entry.Role == "tool")
                {
                    if (!string.IsNullOrEmpty(entry.ToolCallId))
                        apiMsg.ToolCallId = entry.ToolCallId;
                    if (!string.IsNullOrEmpty(entry.Name))
                        apiMsg.Name = entry.Name;
                }

                messages.Add(apiMsg);
            }

            return messages;
        }

        /// <summary>
        /// 克隆当前上下文（用于 Agent 并行调用时的隔离）。
        /// </summary>
        public ConversationContextManager Clone()
        {
            var clone = new ConversationContextManager
            {
                _systemPrompt = _systemPrompt,
                _searchContext = _searchContext,
                _skillContext = _skillContext,
                _estimatedTokens = _estimatedTokens,
                TokenBudget = TokenBudget,
                AutoTrimTurns = AutoTrimTurns,
            };
            clone._entries.AddRange(_entries.Select(e => e.Clone()));
            return clone;
        }

        #endregion

        #region Core API — 撤销与截断

        /// <summary>
        /// 移除指定索引之后的所有消息（用于重试/编辑场景）。
        /// </summary>
        /// <param name="entryIndex">条目在内部 _entries 列表中的索引</param>
        public void TrimAfter(int entryIndex)
        {
            if (entryIndex < 0 || entryIndex >= _entries.Count) return;

            int removeCount = _entries.Count - entryIndex;

            // 重新计算被移除部分的 token
            for (int i = entryIndex; i < _entries.Count; i++)
            {
                _estimatedTokens -= EstimateTokens(_entries[i].Content);
                if (!string.IsNullOrEmpty(_entries[i].ReasoningContent))
                    _estimatedTokens -= EstimateTokens(_entries[i].ReasoningContent);
            }

            _entries.RemoveRange(entryIndex, removeCount);
        }

        /// <summary>
        /// 移除最后一个 user 消息及其之后的所有条目，同时清除不属于任何轮次的 custom 消息
        /// （如 skill 指令）。用于重试/编辑回退场景。
        /// </summary>
        public void TrimAfterLastUserMessage()
        {
            // 从末尾向前找到最后一个 user 消息
            int lastUserIdx = -1;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Role == "user")
                {
                    lastUserIdx = i;
                    break;
                }
            }

            if (lastUserIdx < 0) return;

            // 同时移除所有 TurnIndex=-1 的 custom 消息（如 skill 指令），
            // 避免重试时残留旧的系统指令
            var toRemove = new List<int>();
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].TurnIndex == -1)
                    toRemove.Add(i);
            }
            // 从后往前移除，避免索引偏移
            for (int i = toRemove.Count - 1; i >= 0; i--)
            {
                int idx = toRemove[i];
                _estimatedTokens -= EstimateTokens(_entries[idx].Content);
                _entries.RemoveAt(idx);
                if (idx < lastUserIdx) lastUserIdx--; // 调整索引
            }

            TrimAfter(lastUserIdx);
        }

        /// <summary>
        /// 移除最后一条助手消息（用于流式取消时不保存不完整回复）。
        /// </summary>
        public void RemoveLastAssistantMessage()
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Role == "assistant")
                {
                    _estimatedTokens -= EstimateTokens(_entries[i].Content);
                    if (!string.IsNullOrEmpty(_entries[i].ReasoningContent))
                        _estimatedTokens -= EstimateTokens(_entries[i].ReasoningContent);
                    _entries.RemoveAt(i);
                    return;
                }
            }
        }

        #endregion

        #region Core API — Token 管理

        /// <summary>
        /// 估算文本的 Token 数。
        /// 规则：1 英文字符 ≈ 0.3 token，1 中文字符 ≈ 0.6 token。
        /// </summary>
        public static int EstimateTokens(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            int chineseChars = 0;
            int otherChars = 0;
            foreach (char c in text)
            {
                if (c >= 0x4E00 && c <= 0x9FFF)
                    chineseChars++;
                else if (!char.IsWhiteSpace(c))
                    otherChars++;
            }
            // 1 中文 ≈ 0.6 token, 1 英文 ≈ 0.3 token
            return (int)(chineseChars * 0.6 + otherChars * 0.3) + 1;
        }

        /// <summary>
        /// 当估算 Token 超过预算时，自动修剪最旧的轮次。
        /// </summary>
        public void AutoTrimIfNeeded()
        {
            while (_estimatedTokens > TokenBudget && TurnCount > AutoTrimTurns + 1)
            {
                TrimOldestTurn();
            }
        }

        /// <summary>
        /// 移除最旧的一轮对话（一个 user 消息 + 其后续的 assistant/tool 消息）。
        /// </summary>
        public void TrimOldestTurn()
        {
            // 找到第一个 user 消息
            int startIdx = -1;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Role == "user" && _entries[i].TurnIndex > 0)
                {
                    startIdx = i;
                    break;
                }
            }

            if (startIdx < 0) return;

            int firstTurnIndex = _entries[startIdx].TurnIndex;

            // 移除从 startIdx 到下一个 user 消息（不含）之间的所有条目
            int endIdx = _entries.Count;
            for (int i = startIdx + 1; i < _entries.Count; i++)
            {
                if (_entries[i].Role == "user")
                {
                    endIdx = i;
                    break;
                }
            }

            // 扣除 token
            for (int i = startIdx; i < endIdx; i++)
            {
                _estimatedTokens -= EstimateTokens(_entries[i].Content);
                if (!string.IsNullOrEmpty(_entries[i].ReasoningContent))
                    _estimatedTokens -= EstimateTokens(_entries[i].ReasoningContent);
            }

            _entries.RemoveRange(startIdx, endIdx - startIdx);
        }

        #endregion

        #region Core API — 查询与序列化

        /// <summary>
        /// 获取所有 user/assistant 角色的原始消息列表（用于持久化和 UI）。
        /// </summary>
        public List<ChatApiMessage> GetConversationHistory()
        {
            return _entries
                .Where(e => e.Role == "user" || e.Role == "assistant")
                .Select(e => new ChatApiMessage
                {
                    Role = e.Role,
                    Content = e.Content,
                    ReasoningContent = e.ReasoningContent,
                })
                .ToList();
        }

        /// <summary>
        /// 获取包含所有角色（含 tool）的完整消息列表，用于完整持久化。
        /// 恢复时使用 RestoreFullContext()。
        /// </summary>
        public List<ChatApiMessage> GetFullContext()
        {
            return _entries
                .Select(e => new ChatApiMessage
                {
                    Role = e.Role,
                    Content = e.Content,
                    ReasoningContent = e.ReasoningContent,
                    ToolCalls = e.ToolCalls?.Select(tc => new ToolCall
                    {
                        Id = tc.Id,
                        Type = tc.Type,
                        Function = new ToolCallFunction
                        {
                            Name = tc.Function.Name,
                            Arguments = tc.Function.Arguments,
                        }
                    }).ToList(),
                    ToolCallId = e.ToolCallId,
                    Name = e.Name,
                })
                .ToList();
        }

        /// <summary>
        /// 从完整消息列表恢复上下文（含 tool 消息）。
        /// </summary>
        public void RestoreFullContext(List<ChatApiMessage> fullHistory)
        {
            Clear();
            foreach (var msg in fullHistory)
            {
                switch (msg.Role)
                {
                    case "user":
                        AddUserMessage(msg.Content ?? string.Empty);
                        break;
                    case "assistant":
                        AddAssistantMessage(msg.Content, msg.ReasoningContent, msg.ToolCalls);
                        break;
                    case "tool":
                        if (!string.IsNullOrEmpty(msg.ToolCallId))
                            AddToolResult(msg.ToolCallId, msg.Name ?? "unknown", msg.Content ?? string.Empty);
                        break;
                    case "system":
                        AddCustomMessage("system", msg.Content ?? string.Empty);
                        break;
                }
            }
        }

        /// <summary>
        /// 从持久化的 ChatApiMessage 列表恢复上下文。
        /// 用于会话切换时重建上下文。
        /// </summary>
        public void RestoreFromHistory(List<ChatApiMessage> history)
        {
            Clear();
            int turnIdx = 0;
            foreach (var msg in history)
            {
                if (msg.Role == "user")
                {
                    turnIdx++;
                    AddUserMessage(msg.Content ?? string.Empty);
                }
                else if (msg.Role == "assistant")
                {
                    AddAssistantMessage(msg.Content, msg.ReasoningContent);
                }
            }
        }

        /// <summary>
        /// 清空所有上下文。
        /// </summary>
        public void Clear()
        {
            _entries.Clear();
            _estimatedTokens = 0;
            _systemPrompt = null;
            _searchContext = null;
            _skillContext = null;
        }

        /// <summary>
        /// 获取对话历史的调试摘要。
        /// </summary>
        public string GetDebugSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== 上下文管理器状态 ===");
            sb.AppendLine($"消息总数: {MessageCount}");
            sb.AppendLine($"轮次数: {TurnCount}");
            sb.AppendLine($"估算 Token: {_estimatedTokens}/{TokenBudget}");
            sb.AppendLine($"系统提示词: {(_systemPrompt != null ? $"{_systemPrompt.Length} 字符" : "无")}");
            sb.AppendLine($"搜索上下文: {(_searchContext != null ? $"{_searchContext.Length} 字符" : "无")}");
            sb.AppendLine($"Skill 上下文: {(_skillContext != null ? $"{_skillContext.Length} 字符" : "无")}");
            sb.AppendLine();
            foreach (var entry in _entries)
            {
                string preview = (entry.Content ?? "").Length > 80
                    ? (entry.Content ?? "").Substring(0, 80) + "..."
                    : (entry.Content ?? "");
                string reasoning = !string.IsNullOrEmpty(entry.ReasoningContent) ? " [含思维链]" : "";
                string tools = entry.HasToolCalls ? $" [工具调用:{entry.ToolCalls?.Count}]" : "";
                sb.AppendLine($"[T{entry.TurnIndex}] {entry.Role}: {preview}{reasoning}{tools}");
            }
            return sb.ToString();
        }

        #endregion

        #region Internal Helpers

        /// <summary>
        /// 组装最终的系统提示词（用户自定义 + Skill 上下文）。
        /// </summary>
        private string? BuildFinalSystemPrompt()
        {
            if (string.IsNullOrWhiteSpace(_systemPrompt) && string.IsNullOrWhiteSpace(_skillContext))
                return null;

            if (string.IsNullOrWhiteSpace(_systemPrompt))
                return _skillContext;

            if (string.IsNullOrWhiteSpace(_skillContext))
                return _systemPrompt;

            return _systemPrompt + "\n\n" + _skillContext;
        }

        #endregion

        #region Inner Types

        /// <summary>
        /// 上下文条目 — 内部存储单元，比 ChatApiMessage 更丰富。
        /// </summary>
        private class ContextEntry
        {
            public string Role { get; set; } = "user";
            public string? Content { get; set; }
            public string? ReasoningContent { get; set; }
            public List<ToolCall>? ToolCalls { get; set; }
            public bool HasToolCalls { get; set; }
            public string? ToolCallId { get; set; }
            public string? Name { get; set; }
            /// <summary>所属轮次（1-based），-1 表示不属于任何轮次</summary>
            public int TurnIndex { get; set; } = -1;

            public ContextEntry Clone()
            {
                return new ContextEntry
                {
                    Role = Role,
                    Content = Content,
                    ReasoningContent = ReasoningContent,
                    ToolCalls = ToolCalls?.Select(tc => new ToolCall
                    {
                        Id = tc.Id,
                        Type = tc.Type,
                        Function = new ToolCallFunction
                        {
                            Name = tc.Function.Name,
                            Arguments = tc.Function.Arguments,
                        }
                    }).ToList(),
                    HasToolCalls = HasToolCalls,
                    ToolCallId = ToolCallId,
                    Name = Name,
                    TurnIndex = TurnIndex,
                };
            }
        }

        #endregion
    }
}
