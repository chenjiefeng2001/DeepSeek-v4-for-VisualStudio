using Microsoft.VisualStudio.Shell;
using System.ComponentModel;
using System.Runtime.InteropServices; // Added for Dispid attribute

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// DeepSeek 选项页，对标共享项目 OptionPageGridGeneral。
    /// 通过 Tools → Options → DeepSeek Chat 访问。
    /// </summary>
    public class DeepSeekOptionsPage : DialogPage
    {
        [Category("API Settings")]
        [DisplayName("API Key")]
        [Description("DeepSeek API 密钥，从 https://platform.deepseek.com/api_keys 获取")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string ApiKey { get; set; } = string.Empty;

        [Category("API Settings")]
        [DisplayName("System Prompt")]
        [Description("系统提示词，定义 AI 助手的行为角色")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string SystemPrompt { get; set; } =
            "你是 DeepSeek Chat，一个深度集成在 Visual Studio 中的 AI 编程助手。" +
            "你的核心能力包括：解释代码逻辑、定位并修复 Bug、重构优化代码、生成单元测试、回答各类技术问题。" +
            "请遵循以下准则：\n" +
            "- 回答应简洁、准确、直接，优先给出可运行的代码方案。\n" +
            "- 涉及代码修改时，明确指出文件路径和具体行号。\n" +
            "- 优先使用用户项目已有的框架和库，不引入不必要的依赖。\n" +
            "- 如果用户的问题模糊不清，先追问澄清再给出建议。\n" +
            "- 使用中文回答，代码中的注释也使用中文。";

        [Category("Model Settings")]
        [DisplayName("Selected Model")]
        [Description("使用的 DeepSeek 模型")]
        [TypeConverter(typeof(ModelListConverter))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string SelectedModel { get; set; } = "deepseek-v4-pro";

        [Category("Model Settings")]
        [DisplayName("Enable Deep Thinking")]
        [Description("启用深度思考模式 (Reasoning)")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public bool IsThinkingEnabled { get; set; } = true;

        [Category("Model Settings")]
        [DisplayName("Reasoning Effort")]
        [Description("推理强度: high 或 max")]
        [TypeConverter(typeof(ReasoningEffortConverter))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string ReasoningEffort { get; set; } = "high";
    }

    /// <summary>
    /// 模型列表下拉选项。
    /// </summary>
    internal class ModelListConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(new[] { "deepseek-v4-pro", "deepseek-v4-flash" });
    }

    /// <summary>
    /// 推理强度下拉选项。
    /// </summary>
    internal class ReasoningEffortConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(new[] { "high", "max" });
    }
}
