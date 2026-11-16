using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace SpotlightSearch.Ai;

internal sealed record AiMessage(string Role, string Content)
{
    public bool IsIncomplete { get; init; }
}

internal sealed record AiUsage(int InputTokens, int OutputTokens)
{
    public int Total => InputTokens + OutputTokens;
    public static readonly AiUsage Empty = new(0, 0);
}

internal sealed class AiException(string message) : Exception(message);

/// <summary>
/// Port of Services/Spotlight/SpotlightAiService.cs, with the native SSE/JSON helpers
/// rewritten in managed code and one addition: an OpenAI-compatible custom endpoint.
///
/// Only fixed status codes and short labels reach the UI — never provider response
/// bodies, request URLs or headers.
/// </summary>
internal sealed class AiService
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(90),
        MaxResponseContentBufferSize = 2 * 1024 * 1024
    };

    public static readonly string[] Providers = ["OpenAI", "DeepSeek", "Claude", "Gemini", "Custom"];

    public static string DefaultModel(string provider) => provider switch
    {
        "OpenAI" => "gpt-4o-mini",
        "DeepSeek" => "deepseek-chat",
        "Claude" => "claude-sonnet-4-5",
        "Gemini" => "gemini-2.0-flash",
        _ => string.Empty
    };

    public static bool IsConfigured(Settings.AppSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.AiApiKey) && !string.IsNullOrWhiteSpace(settings.AiModel);

    public static string Describe(Settings.AppSettings settings) =>
        $"{settings.AiProvider} · {FormatModelName(settings.AiModel, settings.AiProvider)}";

    // Presentation only: the model ID sent to the API is never changed.
    // Port of upstream V-Notch Services/Spotlight/SpotlightAiModelName.cs —
    // "gpt-4o-mini" reads as "GPT-4o mini", "deepseek-v4-1-flash" as
    // "DeepSeek V4 1 Flash"; user-defined names ("Sol 6.1") stay intact.
    internal static string FormatModelName(string model, string provider)
    {
        model = model.Trim();
        if (model.Length == 0) return provider;
        if (model.Contains(' ') || model.Contains('/')) return model;

        string? family = null;
        foreach (string prefix in new[] { "gpt", "claude", "gemini", "deepseek", "sol", "o1", "o3", "o4" })
        {
            if (model.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
            {
                family = prefix;
                break;
            }
        }
        if (family == null) return model;

        // Snapshot suffixes like "-2024-06-01" drop out of the short title.
        if (model.Length > 11 && model[^11] == '-'
            && DateTime.TryParseExact(model[^10..], "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
        {
            model = model[..^11];
        }

        string brand = family switch
        {
            "gpt" => "GPT",
            "deepseek" => "DeepSeek",
            "o1" or "o3" or "o4" => family,
            _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(family)
        };

        var words = new List<string>();
        foreach (string part in model[(family.Length + 1)..].Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            // API snapshot dates remain in the raw model id, not the short title.
            if (part.Length == 8 && part.All(char.IsAsciiDigit)) continue;
            if (family == "claude" && part.All(char.IsAsciiDigit) && words.Count > 0
                && words[^1].All(c => char.IsAsciiDigit(c) || c == '.'))
            {
                words[^1] += "." + part;
            }
            else
            {
                words.Add(char.IsAsciiDigit(part[0])
                    ? part
                    : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(part));
            }
        }

        if (words.Count == 0) return model;
        return brand + (family == "gpt" ? "-" : " ") + string.Join(" ", words);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        Settings.AppSettings settings,
        IReadOnlyList<AiMessage> history,
        Action<AiUsage>? onUsage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = CreateRequest(settings, history);
        using var response = await Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new AiException(DescribeHttpError((int)response.StatusCode));

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var usage = AiUsage.Empty;
        bool received = false;
        int total = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null) break;
            if (line.Length == 0) continue;

            // Every provider we support emits one complete JSON payload per data line.
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            string payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]") break;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(payload);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("error", out JsonElement error))
                    throw new AiException(DescribeStreamError(error));

                usage = ReadUsage(root, settings.AiProvider, usage);
                onUsage?.Invoke(usage);

                string delta = ReadDelta(root, settings.AiProvider);
                if (delta.Length == 0) continue;

                total += delta.Length;
                if (total > 200_000) throw new AiException("回复过长，已中断。");
                received = true;
                yield return delta;
            }
        }

        if (!received) throw new AiException("模型没有返回任何内容，请重试。");
    }

    private static HttpRequestMessage CreateRequest(Settings.AppSettings settings, IReadOnlyList<AiMessage> history)
    {
        string key = settings.AiApiKey.Trim();
        string model = settings.AiModel.Trim();
        string provider = settings.AiProvider;
        string url = ResolveEndpoint(settings);

        object body;
        if (provider == "Gemini")
        {
            var contents = new List<object>(history.Count);
            foreach (var message in history)
            {
                contents.Add(new
                {
                    role = message.Role == "assistant" ? "model" : "user",
                    parts = new[] { new { text = message.Content } }
                });
            }
            body = new { contents };
        }
        else
        {
            var messages = new List<object>(history.Count);
            foreach (var message in history)
            {
                messages.Add(new { role = message.Role, content = message.Content });
            }

            // include_usage (upstream V-Notch) makes OpenAI-compatible servers
            // emit a final chunk with token counts; without it most of them stay
            // silent and the usage readout never appears.
            body = provider == "Claude"
                ? new { model, max_tokens = 4096, messages, stream = true }
                : new { model, messages, stream = true, stream_options = new { include_usage = true } };
        }

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

        switch (provider)
        {
            case "Gemini":
                request.Headers.Add("x-goog-api-key", key);
                break;
            case "Claude":
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", "2023-06-01");
                break;
            default:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                break;
        }

        return request;
    }

    private static string ResolveEndpoint(Settings.AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.AiBaseUrl))
        {
            string custom = settings.AiBaseUrl.Trim().TrimEnd('/');
            return custom.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? custom
                : custom + "/chat/completions";
        }

        return settings.AiProvider switch
        {
            "OpenAI" => "https://api.openai.com/v1/chat/completions",
            "DeepSeek" => "https://api.deepseek.com/chat/completions",
            "Claude" => "https://api.anthropic.com/v1/messages",
            "Gemini" =>
                $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(settings.AiModel.Trim())}:streamGenerateContent?alt=sse",
            _ => "https://api.openai.com/v1/chat/completions"
        };
    }

    private static string ReadDelta(JsonElement root, string provider)
    {
        switch (provider)
        {
            case "Gemini":
            {
                if (!root.TryGetProperty("candidates", out JsonElement candidates)
                    || candidates.ValueKind != JsonValueKind.Array
                    || candidates.GetArrayLength() == 0) return string.Empty;
                if (!candidates[0].TryGetProperty("content", out JsonElement content)
                    || !content.TryGetProperty("parts", out JsonElement parts)
                    || parts.ValueKind != JsonValueKind.Array) return string.Empty;

                var builder = new StringBuilder();
                foreach (JsonElement part in parts.EnumerateArray())
                {
                    if (!part.TryGetProperty("text", out JsonElement text)) continue;
                    // Gemini's reasoning stream is flagged; never show it as the answer.
                    if (part.TryGetProperty("thought", out JsonElement thought)
                        && thought.ValueKind == JsonValueKind.True) continue;
                    builder.Append(text.GetString());
                }
                return builder.ToString();
            }

            case "Claude":
                return root.TryGetProperty("delta", out JsonElement claudeDelta)
                       && claudeDelta.TryGetProperty("type", out JsonElement type)
                       && type.GetString() == "text_delta"
                       && claudeDelta.TryGetProperty("text", out JsonElement textNode)
                    ? textNode.GetString() ?? string.Empty
                    : string.Empty;

            default:
                return root.TryGetProperty("choices", out JsonElement choices)
                       && choices.ValueKind == JsonValueKind.Array
                       && choices.GetArrayLength() > 0
                       && choices[0].TryGetProperty("delta", out JsonElement delta)
                       && delta.TryGetProperty("content", out JsonElement textContent)
                       && textContent.ValueKind == JsonValueKind.String
                    ? textContent.GetString() ?? string.Empty
                    : string.Empty;
        }
    }

    private static AiUsage ReadUsage(JsonElement root, string provider, AiUsage current)
    {
        try
        {
            switch (provider)
            {
                case "Gemini":
                    if (root.TryGetProperty("usageMetadata", out JsonElement metadata))
                    {
                        return new AiUsage(
                            metadata.TryGetProperty("promptTokenCount", out JsonElement prompt) ? prompt.GetInt32() : current.InputTokens,
                            metadata.TryGetProperty("candidatesTokenCount", out JsonElement candidate) ? candidate.GetInt32() : current.OutputTokens);
                    }
                    break;

                case "Claude":
                    if (root.TryGetProperty("message", out JsonElement message)
                        && message.TryGetProperty("usage", out JsonElement messageUsage))
                    {
                        return new AiUsage(
                            messageUsage.TryGetProperty("input_tokens", out JsonElement input) ? input.GetInt32() : current.InputTokens,
                            messageUsage.TryGetProperty("output_tokens", out JsonElement output) ? output.GetInt32() : current.OutputTokens);
                    }
                    if (root.TryGetProperty("usage", out JsonElement deltaUsage)
                        && deltaUsage.TryGetProperty("output_tokens", out JsonElement deltaOutput))
                    {
                        return current with { OutputTokens = deltaOutput.GetInt32() };
                    }
                    break;

                default:
                    if (root.TryGetProperty("usage", out JsonElement usage))
                    {
                        return new AiUsage(
                            usage.TryGetProperty("prompt_tokens", out JsonElement prompt) ? prompt.GetInt32() : current.InputTokens,
                            usage.TryGetProperty("completion_tokens", out JsonElement completion) ? completion.GetInt32() : current.OutputTokens);
                    }
                    break;
            }
        }
        catch (Exception)
        {
            // Usage is decoration; a malformed block must not kill the stream.
        }

        return current;
    }

    private static string DescribeHttpError(int status) => status switch
    {
        401 or 403 => "API Key 无效或没有权限。",
        404 => "模型不存在，或接口地址不对。",
        429 => "请求过于频繁或额度用尽，请稍后再试。",
        500 or 502 or 503 or 504 => $"服务端暂时不可用（HTTP {status}）。",
        _ => $"请求失败（HTTP {status}）。"
    };

    private static string DescribeStreamError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("code", out JsonElement code)
            && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out int status))
        {
            return DescribeHttpError(status);
        }

        return "模型返回了错误，请稍后重试。";
    }
}
