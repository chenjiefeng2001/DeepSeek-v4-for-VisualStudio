using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class CodeDiffServiceTests
{
    #region ComputeDiff

    [Fact]
    public void ComputeDiff_SameText_ReturnsAllUnchanged()
    {
        var text = "line1\nline2\nline3";
        var result = CodeDiffService.ComputeDiff(text, text);

        result.Should().HaveCount(3);
        result.Should().AllSatisfy(d => d.Type.Should().Be(DiffLineType.Unchanged));
        result[0].Content.Should().Be("line1");
        result[0].OldLineNumber.Should().Be(1);
        result[0].NewLineNumber.Should().Be(1);
    }

    [Fact]
    public void ComputeDiff_OneLineAdded_ReturnsAdded()
    {
        var oldText = "line1";
        var newText = "line1\nline2";

        var result = CodeDiffService.ComputeDiff(oldText, newText);

        result.Should().HaveCount(2);
        result[0].Type.Should().Be(DiffLineType.Unchanged);
        result[1].Type.Should().Be(DiffLineType.Added);
        result[1].Content.Should().Be("line2");
        result[1].NewLineNumber.Should().Be(2);
    }

    [Fact]
    public void ComputeDiff_OneLineDeleted_ReturnsDeleted()
    {
        var oldText = "line1\nline2";
        var newText = "line1";

        var result = CodeDiffService.ComputeDiff(oldText, newText);

        result.Should().HaveCount(2);
        result[0].Type.Should().Be(DiffLineType.Unchanged);
        result[1].Type.Should().Be(DiffLineType.Deleted);
        result[1].Content.Should().Be("line2");
        result[1].OldLineNumber.Should().Be(2);
    }

    [Fact]
    public void ComputeDiff_OneLineChanged_ReturnsDeletedAndAdded()
    {
        var oldText = "old line";
        var newText = "new line";

        var result = CodeDiffService.ComputeDiff(oldText, newText);

        result.Should().Contain(d => d.Type == DiffLineType.Deleted && d.Content == "old line");
        result.Should().Contain(d => d.Type == DiffLineType.Added && d.Content == "new line");
    }

    [Fact]
    public void ComputeDiff_EmptyOld_ReturnsAllAdded()
    {
        var newText = "line1\nline2";

        var result = CodeDiffService.ComputeDiff("", newText);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(d => d.Type.Should().Be(DiffLineType.Added));
    }

    [Fact]
    public void ComputeDiff_EmptyNew_ReturnsAllDeleted()
    {
        var oldText = "line1\nline2";

        var result = CodeDiffService.ComputeDiff(oldText, "");

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(d => d.Type.Should().Be(DiffLineType.Deleted));
    }

    [Fact]
    public void ComputeDiff_BothEmpty_ReturnsEmpty()
    {
        var result = CodeDiffService.ComputeDiff("", "");

        result.Should().BeEmpty();
    }

    [Fact]
    public void ComputeDiff_WindowsLineEndings_NormalizedCorrectly()
    {
        var oldText = "a\r\nb\r\nc";
        var newText = "a\nb\nc";

        var result = CodeDiffService.ComputeDiff(oldText, newText);

        result.Should().HaveCount(3);
        result.Should().AllSatisfy(d => d.Type.Should().Be(DiffLineType.Unchanged));
    }

    [Fact]
    public void ComputeDiff_MiddleInsertion_HandledCorrectly()
    {
        var oldText = "a\nb\nd";
        var newText = "a\nb\nc\nd";

        var result = CodeDiffService.ComputeDiff(oldText, newText);

        result.Should().ContainSingle(d => d.Type == DiffLineType.Added && d.Content == "c");
        result.Count(d => d.Type == DiffLineType.Unchanged).Should().Be(3);
    }

    [Fact]
    public void ComputeDiff_MultipleChanges_AllCaptured()
    {
        var oldText = "keep1\nremove1\nkeep2\nremove2";
        var newText = "keep1\nadd1\nkeep2\nadd2";

        var result = CodeDiffService.ComputeDiff(oldText, newText);

        result.Should().Contain(d => d.Type == DiffLineType.Deleted && d.Content == "remove1");
        result.Should().Contain(d => d.Type == DiffLineType.Deleted && d.Content == "remove2");
        result.Should().Contain(d => d.Type == DiffLineType.Added && d.Content == "add1");
        result.Should().Contain(d => d.Type == DiffLineType.Added && d.Content == "add2");
        result.Should().Contain(d => d.Type == DiffLineType.Unchanged && d.Content == "keep1");
        result.Should().Contain(d => d.Type == DiffLineType.Unchanged && d.Content == "keep2");
    }

    [Fact]
    public void ComputeDiff_NullInput_HandlesGracefully()
    {
        var result = CodeDiffService.ComputeDiff(null!, "test");
        // Should not throw
    }

    #endregion

    #region ExtractCodeBlocks

    [Fact]
    public void ExtractCodeBlocks_EmptyInput_ReturnsEmptyList()
    {
        var result = CodeDiffService.ExtractCodeBlocks("");

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractCodeBlocks_NullInput_ReturnsEmptyList()
    {
        var result = CodeDiffService.ExtractCodeBlocks(null!);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractCodeBlocks_SingleBlock_ExtractsCorrectly()
    {
        var markdown = "```csharp\nConsole.WriteLine(\"Hello\");\n```";

        var result = CodeDiffService.ExtractCodeBlocks(markdown);

        result.Should().HaveCount(1);
        result[0].Language.Should().Be("csharp");
        result[0].Code.Should().Contain("Console.WriteLine");
        result[0].Index.Should().Be(0);
    }

    [Fact]
    public void ExtractCodeBlocks_WithFilePathHint_ExtractsPath()
    {
        var markdown = "```csharp:Program.cs\n// code here\n```";

        var result = CodeDiffService.ExtractCodeBlocks(markdown);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().Be("Program.cs");
    }

    [Fact]
    public void ExtractCodeBlocks_WithChineseColonPath_ExtractsPath()
    {
        var markdown = "```csharp：MyFile.cs\n// code here\n```";

        var result = CodeDiffService.ExtractCodeBlocks(markdown);

        result[0].FilePath.Should().Be("MyFile.cs");
    }

    [Fact]
    public void ExtractCodeBlocks_MultipleBlocks_AllExtracted()
    {
        var markdown = "```csharp\nvar a = 1;\n```\n\n```python\nprint('hello')\n```";

        var result = CodeDiffService.ExtractCodeBlocks(markdown);

        result.Should().HaveCount(2);
        result[0].Language.Should().Be("csharp");
        result[1].Language.Should().Be("python");
        result[0].Index.Should().Be(0);
        result[1].Index.Should().Be(1);
    }

    [Fact]
    public void ExtractCodeBlocks_InferFilePathFromBacktickRef()
    {
        var markdown = "修改 `MyClass.cs` 文件：\n```csharp\nclass MyClass {}\n```";

        var result = CodeDiffService.ExtractCodeBlocks(markdown);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().Be("MyClass.cs");
    }

    [Fact]
    public void ExtractCodeBlocks_NoLanguage_StillExtractsCode()
    {
        var markdown = "```\nplain text code\n```";

        var result = CodeDiffService.ExtractCodeBlocks(markdown);

        result.Should().HaveCount(1);
        result[0].Language.Should().BeEmpty();
        result[0].Code.Should().Contain("plain text code");
    }

    [Fact]
    public void ExtractCodeBlocks_MultipleFileRefs_MatchesByIndex()
    {
        var markdown = "修改 `A.cs` 和 `B.cs`：\n```csharp\n// A\n```\n```csharp\n// B\n```";

        var result = CodeDiffService.ExtractCodeBlocks(markdown);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void ExtractCodeBlocks_FileRefMatchesVariousExtensions()
    {
        var extensions = new[] { "cs", "py", "js", "ts", "java", "cpp", "c", "h", "xml", "json", "yaml", "yml", "md", "sql", "html", "css", "xaml" };
        foreach (var ext in extensions)
        {
            var markdown = $"修改 `test.{ext}`：\n```\ncode\n```";
            var result = CodeDiffService.ExtractCodeBlocks(markdown);
            result.Should().HaveCount(1, $"should match .{ext} files");
            result[0].FilePath.Should().Be($"test.{ext}");
        }
    }

    #endregion
}
