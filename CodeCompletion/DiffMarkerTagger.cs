using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using System;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// Diff 标记器（方案三：已由 VS SDK 原生差异查看器接管）。
    /// 原有的内联红绿标记功能已迁移到 <see cref="View.DiffViewerWindow"/>，
    /// 该窗口使用 <see cref="Microsoft.VisualStudio.Text.Differencing.IWpfDifferenceViewer"/> 提供原生着色。
    /// 此 tagger 保留为空壳以维持 MEF 导出兼容性。
    /// </summary>
    internal sealed class DiffMarkerTagger : ITagger<ITextMarkerTag>
    {
        #region Constructors

        public DiffMarkerTagger(ITextBuffer buffer)
        {
            // 方案三：差异着色由 IDifferenceViewer 原生提供，此处无需操作
        }

        #endregion

        #region ITagger<ITextMarkerTag> Implementation

#pragma warning disable CS0067 // 方案三：标记事件不再需要触发，差异着色由 IDifferenceViewer 原生处理
        public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;
#pragma warning restore CS0067

        public IEnumerable<ITagSpan<ITextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            // 方案三：不再返回内联标记，差异着色由 DiffViewerWindow 中的 IWpfDifferenceViewer 原生处理
            yield break;
        }

        #endregion
    }
}
