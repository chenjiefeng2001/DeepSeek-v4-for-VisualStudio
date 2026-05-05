using DeepSeek_v4_for_VisualStudio.Models;
using Markdig;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 将聊天消息列表构建为完整的 HTML 页面，用于 WebView2 (Chromium) 渲染。
    /// 含内嵌 JS 语法高亮、代码复制按钮、Shift+滚轮横向滚动、流式自动滚动。
    /// 参考 VisualChatGPTStudioShared ucChat 的 UpdateBrowser 模板。
    /// </summary>
    public static class ChatHtmlService
    {
        #region Constants

        /// <summary>
        /// Markdig 解析管道：启用高级扩展，禁用原生 HTML（防 XSS）。
        /// 对标 ucChat 中 markdownPipeline 的定义。
        /// </summary>
        private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .Build();

        private const string AiAvatarHtml = "<span class='avatar avatar-ai'>AI</span>";
        private const string UserAvatarHtml = "<span class='avatar avatar-user'>U</span>";

        // ═══ 页面 CSS（暗色主题，无边框气泡，代码块纯 CSS） ═══
        private const string PageCss = @"
*{box-sizing:border-box;margin:0;padding:0}
body{background-color:#1E1E1E;color:#D4D4D4;font-family:'Segoe UI','Cascadia Code',Consolas,monospace;font-size:13px;line-height:1.6;padding:12px 16px;overflow-wrap:break-word;word-wrap:break-word}
h1,h2,h3,h4,h5,h6{color:#6CAFD9;margin:12px 0 6px;font-weight:600}
h1{font-size:1.4em;border-bottom:1px solid #444;padding-bottom:4px}
h2{font-size:1.25em;border-bottom:1px solid #444;padding-bottom:3px}
h3{font-size:1.1em}
p{margin:4px 0}
a{color:#6CAFD9;text-decoration:none}
a:hover{text-decoration:underline}
strong,b{color:#E8E8E8;font-weight:600}
em,i{font-style:italic;color:#C8C8C8}
code{background-color:#2D2D2D;color:#CE9178;padding:1px 5px;border-radius:3px;font-family:'Cascadia Code',Consolas,monospace;font-size:0.92em}
pre{background-color:#252526;border-radius:6px;padding:28px 12px 10px 12px;margin:8px 0;overflow-x:auto;font-size:0.9em;line-height:1.5;position:relative}
pre code{background:transparent;color:#D4D4D4;padding:0;font-size:inherit;white-space:pre;display:block}
ul,ol{padding-left:24px;margin:6px 0}
li{margin:2px 0}
blockquote{border-left:3px solid #6CAFD9;padding:6px 12px;margin:8px 0;background-color:#252526;color:#A0A0A0}
table.msg-table{border-collapse:collapse;margin:8px 0}
th,td{padding:6px 10px;text-align:left;border:none}
th{background:#2D2D2D;color:#E8E8E8;font-weight:600}
hr{border:none;border-top:1px solid #444;margin:12px 0}
img{max-width:100%}
.code-lang{position:absolute;top:4px;left:12px;color:#888;font-size:10px;font-family:'Segoe UI',sans-serif;text-transform:uppercase;letter-spacing:0.5px}
pre.mermaid-block{background:#1A1A2E;border-color:#3A3A6A;color:#8A8AD4;font-style:italic;padding:12px;text-align:center}
pre.mermaid-block::before{content:'📊  Mermaid 图表';display:block;color:#6A6AB4;margin-bottom:6px;font-style:normal}
details{margin:8px 0;border:1px solid #3A3A3A;border-radius:6px;background:#1E1E2E;overflow:hidden}
details summary{cursor:pointer;padding:6px 12px;color:#8A8A9A;font-size:12px;font-style:italic;background:#252535;user-select:none}
details summary:hover{color:#A0A0B0}
details .think-content,details> :not(summary){padding:8px 12px;color:#8A8A9A;font-size:12px;font-style:italic;line-height:1.5}
.copy-btn{position:absolute;top:4px;right:8px;background:#3C3C3C;color:#CCC;border:1px solid #555;border-radius:3px;padding:2px 8px;font-size:11px;cursor:pointer;font-family:'Segoe UI',sans-serif;z-index:1}
.copy-btn:hover{background:#4A4A4A;color:#FFF}
.copy-btn.copied{background:#1A3A1A;color:#4EC9B0}
.msg-ai{background:#2D2D2D;border-radius:8px;padding:10px 14px;color:#D4D4D4;font-size:13px;line-height:1.5}
.msg-user{background:#264F78;border-radius:8px;padding:10px 14px;color:#D4D4D4;font-size:13px;line-height:1.5}
.msg-header{font-weight:600;font-size:11px;margin-bottom:4px}
.msg-header-ai{color:#888}
.msg-header-user{color:#6CAFD9;text-align:right}
.msg-body{word-wrap:break-word;overflow-wrap:break-word}
.avatar{display:inline-flex;align-items:center;justify-content:center;width:32px;height:32px;border-radius:50%;font-weight:bold;font-size:14px;flex-shrink:0}
.avatar-ai{background:#4EC9B0;color:#1E1E1E}
.avatar-user{background:#569CD6;color:#fff}
::-webkit-scrollbar{width:8px;height:8px}
::-webkit-scrollbar-track{background:#1E1E1E}
::-webkit-scrollbar-thumb{background:#444;border-radius:4px}
::-webkit-scrollbar-thumb:hover{background:#555}
";

        #endregion

        #region Public Methods

        public static string BuildChatHtml(IReadOnlyList<ChatMessage> messages, bool isStreaming = false)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sb = new StringBuilder();

            int userCount = 0, assistantCount = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                var msg = messages[i];
                if (msg.Role == "user")
                {
                    AppendUserMessage(sb, msg.Content ?? string.Empty);
                    userCount++;
                }
                else if (msg.Role == "assistant")
                {
                    AppendAssistantMessage(sb, msg, i);
                    assistantCount++;
                }
            }

            string fullHtml = WrapFullPage(sb.ToString(), isStreaming);
            sw.Stop();
            System.Diagnostics.Debug.WriteLine(
                $"[Render] BuildChatHtml: {messages.Count} msgs (user={userCount}, asst={assistantCount}), " +
                $"bodyLen={sb.Length}, fullHtmlLen={fullHtml.Length}, streaming={isStreaming}, elapsed={sw.ElapsedMilliseconds}ms");
            return fullHtml;
        }

        #endregion

        #region Private Methods - Message Bodies

        private static void AppendAssistantMessage(StringBuilder sb, ChatMessage msg, int idx)
        {
            string bodyHtml;
            if (msg.IsStreaming && !string.IsNullOrEmpty(msg.Content))
                bodyHtml = BuildStreamingBody(msg.Content);
            else if (!string.IsNullOrEmpty(msg.Content))
                bodyHtml = BuildMessageBody(msg.Content);
            else
                bodyHtml = "<p style='color:#888;font-style:italic'>思考中…</p>";

            string reasoningPanel = BuildReasoningPanel(msg.ReasoningContent);
            string streamingDots = msg.IsStreaming
                ? "<span style='color:#6CAFD9;font-size:10px'>●●●</span>" : "";

            // AI 消息: 头像在左, 气泡在右
            sb.Append(
                "<table cellpadding='0' cellspacing='0' border='0' width='100%' style='margin-bottom:14px'>" +
                "<tr>" +
                "<td width='36' valign='top'>" + AiAvatarHtml + "</td>" +
                "<td valign='top'><div class='msg-ai'>" +
                "<div class='msg-header msg-header-ai'>DeepSeek " + streamingDots + "</div>" +
                reasoningPanel +
                "<div class='msg-body'>" + bodyHtml + "</div>" +
                "</div></td></tr></table>");
        }

        /// <summary>
        /// 将 Markdown 文本转换为 HTML 正文内容（不含完整页面包装）。
        /// 对标 ucChat.AddMessagesHtml 中 AI 消息的处理逻辑：
        /// 直接使用 Markdig.ToHtml() + 思考块预处理 + Mermaid 后处理。
        /// 
        /// 这替代了之前 "MarkdownRenderService.ConvertToHtml → ExtractBodyContent" 的双重包装方案，
        /// 该方案会导致渲染空白（ExtractBodyContent 剥离脚本后丢失上下文 + 双重 CSS 冲突）。
        /// </summary>
        private static string BuildMessageBody(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return string.Empty;
            try
            {
                string htmlContent;

                // ── 处理 &lt;think&gt;...&lt;/think&gt; 思考块 ──
                // 对标 ucChat 中 Regex.Match(content, @"^&lt;think&gt;(?&lt;content&gt;.*)&lt;\/think&gt;(?&lt;answer&gt;.*)$") 的逻辑
                Match thinkMatch = Regex.Match(markdown,
                    @"^<think>(?<content>.*)</think>(?<answer>.*)$",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                if (!thinkMatch.Success)
                {
                    // 无思考块：直接 Markdown → HTML
                    htmlContent = Markdown.ToHtml(markdown, MarkdownPipeline);
                }
                else
                {
                    // 有思考块：思考内容作为 &lt;details&gt; 折叠面板，答案部分正常渲染
                    string thinkBody = Markdown.ToHtml(thinkMatch.Groups["content"].Value, MarkdownPipeline);
                    string thinkBlock =
                        $"<details><summary>🧠 思考过程</summary>{thinkBody}</details>";
                    string answerHtml = Markdown.ToHtml(thinkMatch.Groups["answer"].Value, MarkdownPipeline);
                    htmlContent = $"{thinkBlock}\n{answerHtml}";
                }

                // ── Mermaid 代码块后处理 ──
                // 修复 Markdig 生成的 Mermaid HTML，统一为 &lt;pre&gt;&lt;code class="language-mermaid"&gt;
                // 对标 ucChat 和 MarkdownRenderService.PostprocessMermaidBlocks
                htmlContent = Regex.Replace(htmlContent,
                    @"<pre\s+class=""mermaid[^""]*""[^>]*>(.*?)</pre>",
                    m => WrapMermaidCodeBlock(m.Groups[1].Value),
                    RegexOptions.Singleline);

                htmlContent = Regex.Replace(htmlContent,
                    @"<div class=""lang-mermaid[^""]*"">(.*?)</div>",
                    m => WrapMermaidCodeBlock(m.Groups[1].Value),
                    RegexOptions.Singleline);

                // ── 移除末尾多余的 &lt;br /&gt; ──
                if (htmlContent.EndsWith("<br />"))
                {
                    htmlContent = htmlContent.Substring(0, htmlContent.Length - 6);
                }

                // ── 防止 XSS：转义残留的 &lt;script&gt; 标签 ──
                htmlContent = htmlContent
                    .Replace("<script", "&lt;script")
                    .Replace("</script>", "&lt;/script&gt;");

                return htmlContent;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Render] BuildMessageBody 异常 (len={markdown.Length}): {ex.Message}");
                return "<pre>" + System.Net.WebUtility.HtmlEncode(markdown) + "</pre>";
            }
        }

        /// <summary>
        /// 将 Mermaid 原始代码包装为标准格式 &lt;pre&gt;&lt;code class="language-mermaid"&gt;。
        /// 对标 ucChat 中 Mermaid 代码块处理逻辑。
        /// </summary>
        private static string WrapMermaidCodeBlock(string inner)
        {
            // 如果包含 SVG（预渲染），只取 SVG 之前的原始代码
            int svgIdx = inner.IndexOf("<svg", StringComparison.Ordinal);
            if (svgIdx > 0)
                inner = inner.Substring(0, svgIdx).Trim();

            inner = System.Net.WebUtility.HtmlDecode(inner);
            string escaped = System.Net.WebUtility.HtmlEncode(inner);
            return $"<pre><code class=\"language-mermaid\">{escaped}</code></pre>";
        }

        private static string BuildStreamingBody(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return System.Net.WebUtility.HtmlEncode(text).Replace("\n", "<br>");
        }

        private static string BuildReasoningPanel(string reasoningContent)
        {
            if (string.IsNullOrWhiteSpace(reasoningContent)) return string.Empty;
            string escaped = System.Net.WebUtility.HtmlEncode(reasoningContent);
            return "<div style='margin:8px 0;border:1px solid #3A3A3A;border-radius:4px;background:#1E1E2E;overflow:hidden'>" +
                   "<div style='padding:6px 12px;color:#8A8A9A;font-size:12px;font-style:italic;background:#252535'>🧠 思考过程</div>" +
                   "<div style='padding:8px 12px;color:#8A8A9A;font-size:12px;font-style:italic;line-height:1.5;white-space:pre-wrap'>" +
                   escaped.Replace("\n", "<br>") + "</div></div>";
        }

        #endregion

        #region Private Methods - Message Bubbles

        private static void AppendUserMessage(StringBuilder sb, string content)
        {
            string escaped = System.Net.WebUtility.HtmlEncode(content);
            string body = escaped.Replace("\n", "<br>");

            // 用户消息: 气泡在左, 头像在右
            sb.Append(
                "<table cellpadding='0' cellspacing='0' border='0' width='100%' style='margin-bottom:14px'>" +
                "<tr>" +
                "<td valign='top'><div class='msg-user'>" +
                "<div class='msg-header msg-header-user'>你</div>" +
                "<div class='msg-body'>" + body + "</div>" +
                "</div></td>" +
                "<td width='36' valign='top'>" + UserAvatarHtml + "</td>" +
                "</tr></table>");
        }

        #endregion

        #region Private Methods - Full Page

        private static string WrapFullPage(string messagesHtml, bool isStreaming)
        {
            string autoScrollJs = isStreaming ? BuildAutoScrollJs() : "";

            return "<!DOCTYPE html><html lang='zh-CN'><head><meta charset='UTF-8'><style>" +
                   PageCss + "</style></head><body><div id='chat-container'>" +
                   messagesHtml + "</div><script>" +
                   BuildCodeLangLabelsJs() + BuildCopyBtnJs() + BuildShiftScrollJs() +
                   autoScrollJs + "</script></body></html>";
        }

        #endregion

        #region Private Methods - JavaScript Blocks

        /// <summary>
        /// 为代码块添加语言标签（纯 CSS 方案，无需 JS 语法高亮）。
        /// 替代原 BuildHighlightJs，避免 ES6 模板字符串与 C# verbatim 转义冲突导致的渲染错误。
        /// </summary>
        private static string BuildCodeLangLabelsJs()
        {
            return @"
(function(){'use strict';
var pres=document.querySelectorAll('pre:not(.mermaid-block)');
pres.forEach(function(pre){
    var code=pre.querySelector('code');
    if(!code)return;
    // 提取语言标签并显示在代码块左上角
    var lang='';
    if(code.className){
        var m=code.className.match(/language-(\w+)/);
        if(m)lang=m[1];
    }
    if(lang){
        var label=document.createElement('span');
        label.className='code-lang';
        label.textContent=lang;
        pre.insertBefore(label,pre.firstChild);
    }
});
})();";
        }

        /// <summary>
        /// 代码块按钮 JS（复制 + 应用）。
        /// 对标共享项目 ucChat 的 copy-btn + apply-btn。
        /// 注意："应用"按钮通过 navigator.clipboard 复制代码并提示用户粘贴，
        /// 而非通过 WebMessageReceived（RemoteUserControl 架构下无法注册该事件）。
        /// </summary>
        private static string BuildCopyBtnJs()
        {
            return @"
(function(){
var pres=document.querySelectorAll('pre:not(.mermaid-block)');
pres.forEach(function(pre){
    // ── 复制按钮 ──
    var copyBtn=document.createElement('button');
    copyBtn.className='copy-btn';
    copyBtn.textContent='📋 复制';
    copyBtn.title='复制代码到剪贴板';
    copyBtn.onclick=function(){
        var text=pre.innerText,ok=false;
        if(navigator.clipboard&&navigator.clipboard.writeText){
            navigator.clipboard.writeText(text);ok=true;
        }else{
            var ta=document.createElement('textarea');
            ta.value=text;ta.style.cssText='position:fixed;opacity:0';
            document.body.appendChild(ta);ta.select();
            try{document.execCommand('copy');ok=true;}catch(e){}
            document.body.removeChild(ta);
        }
        if(ok){copyBtn.textContent='✓ 已复制';copyBtn.classList.add('copied');}
        setTimeout(function(){copyBtn.textContent='📋 复制';copyBtn.classList.remove('copied');},1500);
    };
    pre.appendChild(copyBtn);

    // ── 应用按钮（对标 ucChat 的 apply-btn） ──
    // RemoteUserControl 限制：无法注册 CoreWebView2.WebMessageReceived，
    // 因此改为复制到剪贴板并提示用户粘贴（与 Apply 效果等价，仅多一步 Ctrl+V）。
    var applyBtn=document.createElement('button');
    applyBtn.className='copy-btn';
    applyBtn.textContent='⚡ 应用';
    applyBtn.title='复制代码到剪贴板，请在编辑器中粘贴 (Ctrl+V)';
    applyBtn.style.right='60px';
    applyBtn.onclick=function(){
        var text=pre.innerText,ok=false;
        if(navigator.clipboard&&navigator.clipboard.writeText){
            navigator.clipboard.writeText(text);ok=true;
        }else{
            var ta=document.createElement('textarea');
            ta.value=text;ta.style.cssText='position:fixed;opacity:0';
            document.body.appendChild(ta);ta.select();
            try{document.execCommand('copy');ok=true;}catch(e){}
            document.body.removeChild(ta);
        }
        if(ok){applyBtn.textContent='✓ 已复制';applyBtn.classList.add('copied');}
        setTimeout(function(){applyBtn.textContent='⚡ 应用';applyBtn.classList.remove('copied');},1500);
    };
    pre.appendChild(applyBtn);
});
})();";
        }

        /// <summary>
        /// Shift+滚轮横向滚动 JS。
        /// </summary>
        private static string BuildShiftScrollJs()
        {
            return @"
document.addEventListener('wheel',function(e){
    if(!e.shiftKey)return;
    var pre=e.target.closest('pre');
    if(!pre||pre.scrollWidth<=pre.clientWidth)return;
    pre.scrollLeft+=e.deltaY>0?80:-80;
    e.preventDefault();
},{passive:false});";
        }

        /// <summary>
        /// 流式自动滚动 JS（MutationObserver 检测内容变化）。
        /// </summary>
        private static string BuildAutoScrollJs()
        {
            return @"
(function(){
var timer=null;
new MutationObserver(function(){
    if(timer)clearTimeout(timer);
    timer=setTimeout(function(){window.scrollTo({top:document.body.scrollHeight,behavior:'smooth'});},80);
}).observe(document.body,{childList:true,subtree:true,characterData:true});
})();";
        }

        #endregion
    }
}
