using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 构建服务接口 — 为 Agent 提供解决方案构建能力。
    /// 实现类负责与 VS SDK (IVsSolutionBuildManager / DTE) 交互。
    /// </summary>
    public interface IBuildService
    {
        /// <summary>
        /// 执行解决方案构建。
        /// </summary>
        /// <param name="solutionPath">解决方案路径或工作区根目录（.sln 文件、CMakeLists.txt 所在目录等）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>构建结果摘要（成功/失败 + 错误详情）</returns>
        Task<string> BuildAsync(string? solutionPath, CancellationToken ct);
    }
}
