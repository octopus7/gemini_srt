using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GeminiSrtTranslator.Models;

namespace GeminiSrtTranslator.Services;

public class GeminiTranslationService
{
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta";
    private const string Model = "gemini-2.5-flesh";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly HttpClient _httpClient;

    public GeminiTranslationService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(
        IReadOnlyList<SubtitleEntry> entries,
        string apiKey,
        string sourceLanguage,
        string targetLanguage,
        bool preserveFormatting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API 키를 입력하세요.", nameof(apiKey));
        }

        var instruction = BuildPrompt(entries, sourceLanguage, targetLanguage, preserveFormatting);
        var request = new GeminiRequest
        {
            Contents =
            [
                new GeminiContent
                {
                    Parts = [ new GeminiPart { Text = instruction } ]
                }
            ],
            GenerationConfig = new GeminiGenerationConfig
            {
                Temperature = 0.2f,
                TopP = 0.95f,
                TopK = 40,
                CandidateCount = 1
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint}/models/{Model}:generateContent?key={apiKey}")
        {
            Content = new StringContent(JsonSerializer.Serialize(request, SerializerOptions), Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Gemini API 호출 실패: {(int)response.StatusCode} {response.ReasonPhrase}\n{content}");
        }

        return ExtractTranslations(content, entries);
    }

    private static string BuildPrompt(IReadOnlyList<SubtitleEntry> entries, string sourceLanguage, string targetLanguage, bool preserveFormatting)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a subtitle translation assistant.");
        sb.AppendLine($"Translate the following SRT subtitle segments from '{sourceLanguage}' to '{targetLanguage}'.");
        sb.AppendLine("Respond strictly in JSON array format where each item has fields 'index' and 'translation'.");
        sb.AppendLine("Do not add explanations or extra text outside the JSON.");
        if (preserveFormatting)
        {
            sb.AppendLine("Preserve line breaks and inline formatting markers (HTML tags, brackets, punctuation).");
        }

        sb.AppendLine();
        sb.AppendLine("Input:");

        foreach (var entry in entries)
        {
            sb.AppendLine($"- index: {entry.Index}");
            sb.AppendLine($"  text: |-");
            foreach (var line in entry.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                sb.AppendLine($"    {line}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Output JSON example:");
        sb.AppendLine("[");
        sb.AppendLine("  { \"index\": 1, \"translation\": \"첫 번째 문장\" }");
        sb.AppendLine("]");

        return sb.ToString();
    }

    private static IReadOnlyDictionary<int, string> ExtractTranslations(string rawJson, IReadOnlyList<SubtitleEntry> entries)
    {
        using var document = JsonDocument.Parse(rawJson);
        if (!document.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Gemini 응답에 candidates가 없습니다.");
        }

        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts) || parts.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Gemini 응답에 content.parts가 없습니다.");
        }

        var text = parts[0].GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Gemini 응답 텍스트가 비어 있습니다.");
        }

        text = NormalizeJsonPayload(text);

        using var translationDocument = JsonDocument.Parse(text);
        var result = new Dictionary<int, string>();
        foreach (var element in translationDocument.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("index", out var indexProperty) || !element.TryGetProperty("translation", out var translationProperty))
            {
                continue;
            }

            var index = indexProperty.GetInt32();
            var translation = translationProperty.GetString() ?? string.Empty;
            result[index] = translation;
        }

        // Ensure missing translations are accounted for
        foreach (var entry in entries)
        {
            result.TryAdd(entry.Index, string.Empty);
        }

        return result;
    }

    private static string NormalizeJsonPayload(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLineBreak = trimmed.IndexOf('\n');
        if (firstLineBreak < 0)
        {
            return trimmed;
        }

        var contentStart = firstLineBreak + 1;
        var closingFenceIndex = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFenceIndex <= contentStart)
        {
            return trimmed.Substring(contentStart).Trim();
        }

        var inner = trimmed.Substring(contentStart, closingFenceIndex - contentStart);
        return inner.Trim();
    }

    private sealed class GeminiRequest
    {
        public IList<GeminiContent> Contents { get; init; } = new List<GeminiContent>();
        public GeminiGenerationConfig? GenerationConfig { get; init; }
        public IList<GeminiSafetySetting>? SafetySettings { get; init; }
    }

    private sealed class GeminiContent
    {
        public IList<GeminiPart> Parts { get; init; } = new List<GeminiPart>();
    }

    private sealed class GeminiPart
    {
        public string Text { get; init; } = string.Empty;
    }

    private sealed class GeminiGenerationConfig
    {
        public float Temperature { get; init; }
        public float TopP { get; init; }
        public int TopK { get; init; }
        public int CandidateCount { get; init; }
    }

    private sealed class GeminiSafetySetting
    {
        public string Category { get; init; } = string.Empty;
        public string Threshold { get; init; } = string.Empty;
    }
}
