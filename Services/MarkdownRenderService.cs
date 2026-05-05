using Markdig;
using System;
using System.Text;
using System.Text.RegularExpressions;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 将 Markdown 转换为自包含的 HTML（无 CDN 依赖），适配 WebView2 的 data:text/html URI。
    /// 提供内嵌代码语法高亮、思考块折叠、代码复制/应用按钮。
    /// 支持流式渲染（轻量 HTML）和完成渲染（完整高亮 HTML）两种模式。
    /// </summary>
    public static class MarkdownRenderService
    {
        #region Constants

        /// <summary>
        /// Markdig 解析管道：启用高级扩展，禁用原生 HTML（防 XSS）。
        /// </summary>
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .Build();

        #endregion

        #region Public Methods

        /// <summary>
        /// 将 Markdown 转换为自包含的完整 HTML 页面（含内嵌语法高亮、代码按钮）。
        /// </summary>
        /// <param name="markdown">Markdown 原始文本。</param>
        /// <param name="messageIndex">消息序号，用于 JS 唯一标识。</param>
        public static string ConvertToHtml(string markdown, int messageIndex = 0)
        {
            if (string.IsNullOrEmpty(markdown))
                return WrapHtml(string.Empty, messageIndex);

            try
            {
                string processed = PreprocessThinkBlocks(markdown);
                string htmlBody = Markdown.ToHtml(processed, Pipeline);
                htmlBody = PostprocessMermaidBlocks(htmlBody);
                return WrapHtml(htmlBody, messageIndex);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Render] MarkdownRenderService.ConvertToHtml 异常 (idx={messageIndex}, len={markdown.Length}): {ex.Message}");
                string escaped = System.Net.WebUtility.HtmlEncode(markdown);
                return WrapHtml($"<pre>{escaped}</pre>", messageIndex);
            }
        }

        /// <summary>
        /// 流式渲染用：将纯文本内容包装为轻量 HTML（无 JS 高亮，仅 &lt;pre&gt; 换行）。
        /// 用于流式输出过程中实时显示，避免每帧都执行完整的 Markdig 解析。
        /// </summary>
        /// <param name="text">流式累积的纯文本。</param>
        public static string ConvertStreamingToHtml(string text)
        {
            if (string.IsNullOrEmpty(text))
                return WrapStreamingHtml(string.Empty);

            // 转义 HTML 特殊字符
            string escaped = System.Net.WebUtility.HtmlEncode(text);
            // 保留换行
            string body = escaped.Replace("\n", "<br>");
            return WrapStreamingHtml(body);
        }

        /// <summary>
        /// 流式渲染用：将 Markdown 文本转为 HTML（走完整 Markdig 解析）。
        /// 用于流式过程中每 N 个字符触发一次完整渲染。
        /// </summary>
        public static string ConvertStreamingMarkdownToHtml(string markdown, int messageIndex = 0)
        {
            return ConvertToHtml(markdown, messageIndex);
        }

        /// <summary>
        /// Markdown → data:text/html;base64 URI，直接绑定到 WebView2.Source。
        /// </summary>
        public static string ConvertToDataUri(string markdown, int messageIndex = 0)
        {
            string html = ConvertToHtml(markdown, messageIndex);
            return HtmlToDataUri(html);
        }

        /// <summary>
        /// 流式文本 → data:text/html;base64 URI。
        /// </summary>
        public static string ConvertStreamingToDataUri(string text)
        {
            string html = ConvertStreamingToHtml(text);
            return HtmlToDataUri(html);
        }

        /// <summary>
        /// 流式 Markdown → data:text/html;base64 URI。
        /// </summary>
        public static string ConvertStreamingMarkdownToDataUri(string markdown, int messageIndex = 0)
        {
            string html = ConvertStreamingMarkdownToHtml(markdown, messageIndex);
            return HtmlToDataUri(html);
        }

        #endregion

        #region Private Methods

        private static string HtmlToDataUri(string html)
        {
            if (string.IsNullOrEmpty(html)) return string.Empty;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(html);
                return $"data:text/html;charset=utf-8;base64,{Convert.ToBase64String(bytes)}";
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// 预处理 &lt;think&gt;...&lt;/think&gt; → HTML 注释标记，后续在 JS 中替换为可折叠面板。
        /// </summary>
        private static string PreprocessThinkBlocks(string markdown)
        {
            Match m = Regex.Match(markdown,
                @"<think>(?<content>.*?)</think>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            if (!m.Success) return markdown;

            string thinkContent = m.Groups["content"].Value.Trim();
            string thinkMarker = $"\n\n<!--THINK_BLOCK-->{System.Net.WebUtility.HtmlEncode(thinkContent)}<!--/THINK_BLOCK-->\n\n";
            string after = markdown.Substring(m.Index + m.Length);

            return markdown.Substring(0, m.Index) + thinkMarker + after;
        }

        /// <summary>
        /// 修复 Markdig 生成的 Mermaid HTML，统一为 &lt;pre&gt;&lt;code class="language-mermaid"&gt;。
        /// </summary>
        private static string PostprocessMermaidBlocks(string html)
        {
            html = Regex.Replace(html,
                @"<pre\s+class=""mermaid[^""]*""[^>]*>(.*?)</pre>",
                m => WrapMermaidBlock(m.Groups[1].Value),
                RegexOptions.Singleline);

            html = Regex.Replace(html,
                @"<div class=""lang-mermaid[^""]*"">(.*?)</div>",
                m => WrapMermaidBlock(m.Groups[1].Value),
                RegexOptions.Singleline);

            return html;
        }

        private static string WrapMermaidBlock(string inner)
        {
            int svgIdx = inner.IndexOf("<svg", StringComparison.Ordinal);
            if (svgIdx > 0) inner = inner.Substring(0, svgIdx).Trim();
            inner = System.Net.WebUtility.HtmlDecode(inner);
            string escaped = System.Net.WebUtility.HtmlEncode(inner);
            return $"<pre class=\"mermaid-block\"><code>{escaped}</code></pre>";
        }

        /// <summary>
        /// 流式文本的轻量 HTML 包裹（纯文本 + 简洁样式，无 JS 高亮）。
        /// </summary>
        private static string WrapStreamingHtml(string body)
        {
            return $@"<!DOCTYPE html><html lang=""zh-CN""><head><meta charset=""UTF-8"">
<style>
*{{margin:0;padding:0;box-sizing:border-box}}
body{{background:#1E1E1E;color:#D4D4D4;font-family:'Segoe UI','Cascadia Code',Consolas,monospace;font-size:13px;line-height:1.6;padding:10px 14px;overflow-wrap:break-word}}
pre{{white-space:pre-wrap;word-wrap:break-word}}
</style></head><body>{body}</body></html>";
        }

        /// <summary>
        /// 完整 HTML 包裹（含内嵌语法高亮 JS、代码按钮、思考块折叠）。
        /// 使用 @"" 非插值 verbatim 字符串，手动替换占位符，避免 C# 插值与 JS 转义冲突。
        /// </summary>
        private static string WrapHtml(string bodyContent, int messageIndex)
        {
            // 使用 @"" 非插值：{} 是字面量，\ 是字面量，" 需双写为 ""
            string template = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<style>
    * { box-sizing: border-box; margin: 0; padding: 0; }
    body {
        background-color: #1E1E1E; color: #D4D4D4;
        font-family: 'Segoe UI', 'Cascadia Code', 'Consolas', monospace;
        font-size: 13px; line-height: 1.6; padding: 10px 14px;
        overflow-wrap: break-word;
    }
    h1,h2,h3,h4,h5,h6 { color: #6CAFD9; margin: 12px 0 6px; font-weight: 600; }
    h1 { font-size:1.4em; border-bottom:1px solid #444; padding-bottom:4px; }
    h2 { font-size:1.25em; border-bottom:1px solid #444; padding-bottom:3px; }
    h3 { font-size:1.1em; }
    p { margin: 4px 0; }
    a { color: #6CAFD9; text-decoration: none; } a:hover { text-decoration: underline; }
    strong,b { color: #E8E8E8; font-weight: 600; }
    em,i { font-style: italic; color: #C8C8C8; }
    code {
        background-color: #2D2D2D; color: #CE9178;
        padding: 1px 5px; border-radius: 3px;
        font-family: 'Cascadia Code', 'Consolas', monospace; font-size: 0.92em;
    }
    pre {
        background-color: #252526; border: 1px solid #444; border-radius: 6px;
        padding: 28px 10px 10px 10px; margin: 8px 0; overflow-x: auto;
        font-size: 0.9em; line-height: 1.5; position: relative;
    }
    pre code { background: transparent; color: #D4D4D4; padding: 0; font-size: inherit; }
    ul,ol { padding-left: 24px; margin: 6px 0; }
    li { margin: 2px 0; }
    blockquote {
        border-left: 3px solid #6CAFD9; padding: 6px 12px; margin: 8px 0;
        background-color: #252526; color: #A0A0A0;
    }
    table { border-collapse: collapse; margin: 8px 0; width: 100%; }
    th,td { border: 1px solid #444; padding: 6px 10px; text-align: left; }
    th { background: #2D2D2D; color: #E8E8E8; font-weight: 600; }
    tr:nth-child(even) { background: #222; }
    hr { border: none; border-top: 1px solid #444; margin: 12px 0; }
    img { max-width: 100%; }

    details.think-block {
        margin:8px 0; border:1px solid #3A3A3A; border-radius:6px;
        background:#1E1E2E; overflow:hidden;
    }
    details.think-block summary {
        cursor:pointer; padding:6px 12px; color:#8A8A9A;
        font-size:12px; font-style:italic; background:#252535;
    }
    details.think-block summary:hover { color:#A0A0B0; }
    details.think-block .think-content {
        padding:8px 12px; color:#8A8A9A; font-size:12px; font-style:italic;
        line-height:1.5; white-space:pre-wrap;
    }

    .code-btn-wrapper { position: relative; margin: 8px 0; }
    .code-btn {
        position:absolute; top:5px; background:#3C3C3C; color:#CCC;
        border:1px solid #555; border-radius:3px; padding:2px 8px;
        font-size:11px; cursor:pointer; z-index:10; font-family:'Segoe UI',sans-serif;
        transition:background 0.15s;
    }
    .code-btn:hover { background:#4A4A4A; color:#FFF; }
    .code-btn.copy-btn { right:44px; }
    .code-btn.apply-btn { right:8px; }
    .code-btn.copied { background:#1A3A1A; color:#4EC9B0; }

    pre.mermaid-block {
        background:#1A1A2E; border-color:#3A3A6A; color:#8A8AD4;
        font-style:italic; padding:12px; text-align:center;
    }
    pre.mermaid-block::before {
        content:'📊  Mermaid 图表'; display:block;
        color:#6A6AB4; margin-bottom:6px; font-style:normal;
    }

    .hljs-keyword { color: #569CD6; }
    .hljs-string  { color: #CE9178; }
    .hljs-comment { color: #6A9955; font-style: italic; }
    .hljs-number  { color: #B5CEA8; }
    .hljs-title   { color: #DCDCAA; }
    .hljs-type    { color: #4EC9B0; }
    .hljs-literal { color: #569CD6; }
    .hljs-built_in { color: #DCDCAA; }
    .hljs-meta    { color: #9B9B9B; }
    .hljs-attr    { color: #9CDCFE; }
    .hljs-params  { color: #D4D4D4; }
    .hljs-symbol  { color: #B5CEA8; }
    .hljs-function { color: #DCDCAA; }
    .hljs-variable { color: #9CDCFE; }
    .hljs-selector { color: #D7BA7D; }
</style>
</head>
<body>
__BODY__
<script>
(function(){'use strict';
var msgIdx=__MSG_IDX__;

// ── 思考块 → details ──
(function(){
    var b=document.body, h=b.innerHTML;
    h=h.replace(/&lt;!--THINK_BLOCK--&gt;([\s\S]*?)&lt;!--\/THINK_BLOCK--&gt;/g,function(_,c){
        return '<details class=""think-block""><summary>🧠 思考过程</summary><div class=""think-content"">'+c+'</div></details>';
    });
    b.innerHTML=h;
})();

// ── 内嵌语法高亮 ──
(function(){
    var pres=document.querySelectorAll('pre:not(.mermaid-block)');
    pres.forEach(function(pre){
        var code=pre.querySelector('code');
        if(!code)return;
        var lang='';
        if(code.className){
            var m=code.className.match(/language-(\w+)/);
            if(m)lang=m[1].toLowerCase();
        }
        var text=code.textContent;
        if(!text.trim())return;
        code.innerHTML=highlightCode(text,lang);
    });

    function highlightCode(code,lang){
        var esc=code.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');

        var kws='abstract|as|base|bool|break|byte|case|catch|char|checked|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|finally|fixed|float|for|foreach|goto|if|implicit|in|int|interface|internal|is|lock|long|namespace|new|null|object|operator|out|override|params|private|protected|public|readonly|ref|return|sbyte|sealed|short|sizeof|stackalloc|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|var|virtual|void|volatile|while|async|await|yield|record|init|required|file|global';
        var kwSet=new Set(kws.split('|'));

        if(lang==='python'||lang==='py'){
            ['def','lambda','self','None','True','False','and','or','not','elif','except','pass','raise','import','from','with','as','print'].forEach(function(k){kwSet.add(k);});
        }else if(lang==='javascript'||lang==='js'||lang==='typescript'||lang==='ts'){
            ['function','const','let','var','undefined','NaN','Infinity','console','document','window','module','exports','require','Promise','async','await','typeof','instanceof','new'].forEach(function(k){kwSet.add(k);});
        }else if(lang==='sql'){
            ['SELECT','FROM','WHERE','JOIN','LEFT','RIGHT','INNER','OUTER','ON','GROUP','BY','HAVING','ORDER','LIMIT','INSERT','INTO','VALUES','UPDATE','SET','DELETE','CREATE','TABLE','ALTER','DROP','INDEX','VIEW','AND','OR','NOT','IN','BETWEEN','LIKE','IS','NULL','COUNT','SUM','AVG','MIN','MAX','CAST','CONVERT'].forEach(function(k){kwSet.add(k);});
        }else if(lang==='xml'||lang==='html'){
            var r=esc.replace(/(&lt;\/?)(\w[\w-]*)/g,'$1<span class=""hljs-selector"">$2</span>');
            r=r.replace(/(\w[\w-]*)(=)(""[^""]*"")/g,'<span class=""hljs-attr"">$1</span>$2<span class=""hljs-string"">$3</span>');
            return r;
        }

        var r=esc;
        r=r.replace(/(\/\/[^\n]*)/g,'<span class=""hljs-comment"">$1</span>');
        r=r.replace(/(\/\*[\s\S]*?\*\/)/g,'<span class=""hljs-comment"">$1</span>');
        if(lang==='python'||lang==='py'){
            r=r.replace(/(#[^\n]*)/g,'<span class=""hljs-comment"">$1</span>');
        }

        r=r.replace(/(""(?:[^""\\]|\\.)*"")/g,'<span class=""hljs-string"">$1</span>');
        r=r.replace(/('(?:[^'\\]|\\.)*')/g,'<span class=""hljs-string"">$1</span>');
        r=r.replace(/(`(?:[^`\\]|\\.)*`)/g,'<span class=""hljs-string"">$1</span>');

        r=r.replace(/\b(\d+\.?\d*[fFlLdDmM]?|0x[0-9a-fA-F]+|0b[01]+)\b/g,'<span class=""hljs-number"">$1</span>');

        kwSet.forEach(function(kw){
            var re=new RegExp('\\b('+kw.replace(/[.*+?^${}()|[\]\\]/g,'\\$&')+')\\b','g');
            r=r.replace(re,'<span class=""hljs-keyword"">$1</span>');
        });

        return r;
    }
})();

// ── 代码块按钮 ──
(function(){
    var pres=document.querySelectorAll('pre:not(.mermaid-block)');
    pres.forEach(function(pre){
        var wrapper=document.createElement('div');
        wrapper.className='code-btn-wrapper';
        pre.parentNode.insertBefore(wrapper,pre);
        wrapper.appendChild(pre);

        var copyBtn=document.createElement('button');
        copyBtn.className='code-btn copy-btn';
        copyBtn.textContent='📋 复制';
        copyBtn.title='复制代码到剪贴板';
        copyBtn.onclick=function(){
            var text=pre.innerText, ok=false;
            if(navigator.clipboard&&navigator.clipboard.writeText){
                navigator.clipboard.writeText(text); ok=true;
            }else{
                var ta=document.createElement('textarea');
                ta.value=text; ta.style.cssText='position:fixed;opacity:0';
                document.body.appendChild(ta); ta.select();
                try{document.execCommand('copy');ok=true;}catch(e){}
                document.body.removeChild(ta);
            }
            if(ok){copyBtn.textContent='✓ 已复制';copyBtn.classList.add('copied');}
            setTimeout(function(){copyBtn.textContent='📋 复制';copyBtn.classList.remove('copied');},1500);
        };
        wrapper.appendChild(copyBtn);

        var applyBtn=document.createElement('button');
        applyBtn.className='code-btn apply-btn';
        applyBtn.textContent='⚡ 应用';
        applyBtn.title='将代码应用到编辑器';
        applyBtn.onclick=function(){
            var codeText=pre.innerText;
            try{
                if(window.chrome&&window.chrome.webview&&window.chrome.webview.postMessage){
                    window.chrome.webview.postMessage(JSON.stringify({type:'applyCode',code:codeText,messageIndex:msgIdx}));
                }else if(window.external&&window.external.notify){
                    window.external.notify(JSON.stringify({type:'applyCode',code:codeText,messageIndex:msgIdx}));
                }
                applyBtn.textContent='✓ 已发送';
                setTimeout(function(){applyBtn.textContent='⚡ 应用';},1500);
            }catch(e){}
        };
        wrapper.appendChild(applyBtn);
    });
})();

// ── Shift+滚轮 横向滚动 ──
(function(){
    document.addEventListener('wheel',function(e){
        if(!e.shiftKey)return;
        var pre=e.target.closest('pre');
        if(!pre||pre.scrollWidth<=pre.clientWidth)return;
        pre.scrollLeft+=e.deltaY>0?80:-80;
        e.preventDefault();
    },{passive:false});
})();

})();
</script>
</body>
</html>";

            return template
                .Replace("__BODY__", bodyContent)
                .Replace("__MSG_IDX__", messageIndex.ToString());
        }

        #endregion
    }
}
