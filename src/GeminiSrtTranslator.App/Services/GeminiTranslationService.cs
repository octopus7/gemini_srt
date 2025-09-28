using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using GeminiSrtTranslator.Models;

namespace GeminiSrtTranslator.Services;

public class GeminiTranslationService
{
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta";
    internal const string DefaultModelName = "gemini-2.5-flash";
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
        string? modelName,
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

        var model = string.IsNullOrWhiteSpace(modelName) ? DefaultModelName : modelName;
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

        var requestUri = $"{Endpoint}/models/{model}:generateContent?key={apiKey}";
        var payload = JsonSerializer.Serialize(request, SerializerOptions);

        Debug.WriteLine("[Gemini] Sending request");
        Debug.WriteLine($"  URI: {MaskApiKey(requestUri, apiKey)}");
        Debug.WriteLine($"  Model: {model}");
        Debug.WriteLine($"  Source: {sourceLanguage} -> Target: {targetLanguage}");
        Debug.WriteLine($"  Entries: {entries.Count}");
        Debug.WriteLine($"  Payload bytes: {Encoding.UTF8.GetByteCount(payload)}");

        using var message = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Debug.WriteLine("[Gemini] Response received");
        Debug.WriteLine($"  StatusCode: {(int)response.StatusCode} {response.ReasonPhrase}");
        Debug.WriteLine($"  Body preview: {Truncate(content, 1024)}");

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
        sb.AppendLine($"Translate the following SRT subtitle segments from '{sourceLanguage}' to '{targetLanguage}' with natural phrasing.");
        sb.AppendLine("Return only valid JSON containing an object with a 'translations' array.");
        sb.AppendLine("Each array item must include integer field 'index' and string field 'text'.");
        sb.AppendLine("Do not include explanations, comments, or trailing text outside the JSON object.");
        if (preserveFormatting)
        {
            sb.AppendLine("Preserve line breaks and inline formatting markers (HTML tags, brackets, punctuation) inside the translated text.");
        }

        sb.AppendLine();
        sb.AppendLine("Input subtitles as JSON array:");
        var inputPayload = entries
            .Select(entry => new
            {
                index = entry.Index,
                text = entry.Text.Replace("\r\n", "\n")
            })
            .ToList();
        sb.AppendLine(JsonSerializer.Serialize(inputPayload, SerializerOptions));
        sb.AppendLine();
        sb.AppendLine("Respond with:");
        sb.AppendLine("{");
        sb.AppendLine("  \"translations\": [");
        sb.AppendLine("    { \"index\": 1, \"text\": \"번역된 문장\" }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");

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
        var root = translationDocument.RootElement;

        JsonElement translationArray;
        switch (root.ValueKind)
        {
            case JsonValueKind.Object when root.TryGetProperty("translations", out var translationsProperty) && translationsProperty.ValueKind == JsonValueKind.Array:
                translationArray = translationsProperty;
                break;
            case JsonValueKind.Array:
                translationArray = root;
                break;
            default:
                throw new InvalidOperationException("Gemini 응답이 예상한 JSON 형식과 다릅니다.");
        }

        var result = new Dictionary<int, string>();
        foreach (var element in translationArray.EnumerateArray())
        {
            if (!element.TryGetProperty("index", out var indexProperty))
            {
                continue;
            }

            var index = indexProperty.GetInt32();
            string translation = string.Empty;

            if (element.TryGetProperty("text", out var textProperty))
            {
                translation = textProperty.GetString() ?? string.Empty;
            }
            else if (element.TryGetProperty("translation", out var translationProperty))
            {
                translation = translationProperty.GetString() ?? string.Empty;
            }

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

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value.Substring(0, maxLength) + "...";
    }

    private static string MaskApiKey(string text, string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length < 6)
        {
            return text;
        }

        var masked = apiKey[..3] + new string('*', apiKey.Length - 6) + apiKey[^3..];
        return text.Replace(apiKey, masked, StringComparison.Ordinal);
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
