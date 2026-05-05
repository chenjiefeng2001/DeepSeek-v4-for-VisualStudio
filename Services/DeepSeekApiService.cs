using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    public class DeepSeekApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private const string BaseUrl = "https://api.deepseek.com";
        private const string ChatEndpoint = "/chat/completions";

        private string _model;
        private bool _thinkingEnabled = true;
        private string _reasoningEffort = "high";

        public DeepSeekApiService(string apiKey, string model = "deepseek-v4-pro")
        {
            _model = model;
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromMinutes(5)
            };
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }

        public void UpdateModel(string model) => _model = model;
        public void ConfigureThinking(bool enabled, string effort = "high")
        {
            _thinkingEnabled = enabled;
            _reasoningEffort = effort;
        }

        public async IAsyncEnumerable<string> ChatStreamAsync(
            IEnumerable<ChatApiMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = new DeepSeekChatRequest
            {
                Model = _model,
                Messages = new List<ChatApiMessage>(messages),
                Stream = true,
                Thinking = new ThinkingControl { Type = _thinkingEnabled ? "enabled" : "disabled" },
                ReasoningEffort = _thinkingEnabled ? _reasoningEffort : null
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ChatEndpoint)
            {
                Content = JsonContent.Create(request, options: new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                })
            };
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync();

                if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                    continue;

                var jsonData = line.Substring(6);
                if (jsonData == "[DONE]")
                    yield break;

                // 解析结果存入局部变量
                string? reasoning = null;
                string? content = null;

                try
                {
                    var chunk = JsonSerializer.Deserialize<DeepSeekStreamChunk>(jsonData);
                    var delta = chunk?.Choices?[0]?.Delta;
                    if (delta != null)
                    {
                        reasoning = delta.ReasoningContent;
                        content = delta.Content;
                    }
                }
                catch (JsonException)
                {
                    // 忽略无法解析的行（如 keep-alive 注释）
                    continue;
                }

                // yield return 移至 try 块外部，解决编译错误
                if (!string.IsNullOrEmpty(reasoning))
                    yield return $"[THINKING]{reasoning}";

                if (!string.IsNullOrEmpty(content))
                    yield return content;
            }
        }

        public void Dispose() => _httpClient?.Dispose();
    }
}