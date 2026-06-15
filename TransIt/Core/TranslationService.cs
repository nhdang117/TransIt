using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TransIt.Core;

namespace TransIt.Core;

public class TranslationService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly AppSettings _settings;

    public TranslationService(AppSettings settings) => _settings = settings;

    public Task<List<string>> TranslateAsync(
        IList<string> texts,
        string sourceLang,
        string targetLang,
        CancellationToken ct = default)
    {
        if (texts.Count == 0) return Task.FromResult(new List<string>());

        return _settings.Provider == TranslationProvider.OpenAI
            ? TranslateOpenAiAsync(texts, sourceLang, targetLang, ct)
            : TranslateGoogleAsync(texts, sourceLang, targetLang, ct);
    }

    // ── OpenAI ───────────────────────────────────────────────────────────────

    private async Task<List<string>> TranslateOpenAiAsync(
        IList<string> texts, string src, string tgt, CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(texts);
        var systemPrompt =
            $"You are a professional translator from {src} to {tgt}. Follow these rules exactly:\n" +
            $"1. Translate every string from {src} to {tgt}.\n" +
            $"2. Preserve ALL newline characters (\\n) at exactly the same positions as in the source.\n" +
            $"3. Leave UNCHANGED: mathematical and technical symbols (→ ← ↑ ↓ ≤ ≥ ≠ ≈ ± × ÷ ∑ √ ∞ ∈ ∉ ⊂ ⊃ ∩ ∪ ∀ ∃ etc.), " +
            $"ASCII operators and punctuation used as symbols (-> >= <= == != => ** // etc.), " +
            $"identifiers in camelCase / PascalCase / snake_case / SCREAMING_SNAKE_CASE, " +
            $"proper nouns and personal names, URLs, file paths, version numbers, and numeric values.\n" +
            $"4. Preserve the original punctuation structure: hyphens, dashes, commas, and end-of-line periods.\n" +
            $"5. Return ONLY a valid JSON array of strings — same count and order as the input array. No markdown fences, no explanations.";

        var body = new
        {
            model = _settings.OpenAiModel,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = inputJson }
            },
            temperature = 0.1
        };

        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.OpenAiApiKey);

        var resp = await _http.SendAsync(request, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        var doc = JsonNode.Parse(json);
        var content = doc?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                      ?? throw new Exception("Unexpected OpenAI response shape.");

        return ParseJsonArray(content, texts.Count);
    }

    // ── OpenAI Vision ────────────────────────────────────────────────────────

    public async Task<List<string>> TranslateImageAsync(
        byte[] pngBytes, string src, string tgt, CancellationToken ct = default)
    {
        var base64 = Convert.ToBase64String(pngBytes);
        var dataUrl = $"data:image/png;base64,{base64}";

        var srcName = LangName(src);
        var tgtName = LangName(tgt);
        var systemPrompt =
            $"You are a professional OCR engine and translator. Follow these rules exactly:\n" +
            $"1. Read ALL visible text from the image.\n" +
            $"2. Translate every piece of extracted text from {srcName} into {tgtName}. Output MUST be in {tgtName}.\n" +
            $"3. Group translated {tgtName} text into visual paragraph blocks as they appear top-to-bottom in the image.\n" +
            $"4. Within each paragraph block, preserve newline positions that reflect the visual line breaks.\n" +
            $"5. Leave UNCHANGED: mathematical and technical symbols (→ ← ↑ ↓ ≤ ≥ ≠ ≈ ± × ÷ ∑ √ ∞ etc.), " +
            $"ASCII operators (-> >= <= == != => ** // etc.), " +
            $"identifiers in camelCase / PascalCase / snake_case / SCREAMING_SNAKE_CASE, " +
            $"proper nouns, personal names, URLs, file paths, version numbers, and numeric values.\n" +
            $"6. Return ONLY a valid JSON array of {tgtName} strings — one string per paragraph block, in reading order. No markdown fences, no explanations.";

        var body = new
        {
            model = _settings.OpenAiModel,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image_url", image_url = new { url = dataUrl } }
                    }
                }
            },
            temperature = 0.1
        };

        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.OpenAiApiKey);

        var resp = await _http.SendAsync(request, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        var doc = JsonNode.Parse(json);
        var content = doc?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                      ?? throw new Exception("Unexpected OpenAI vision response shape.");

        return ParseJsonArray(content, -1);
    }

    // ── Google Translate ──────────────────────────────────────────────────────

    private async Task<List<string>> TranslateGoogleAsync(
        IList<string> texts, string src, string tgt, CancellationToken ct)
    {
        // Google Translate v2 supports batch via multiple 'q' parameters
        var sb = new StringBuilder();
        sb.Append($"https://translation.googleapis.com/language/translate/v2?key={_settings.GoogleApiKey}");
        sb.Append($"&source={Uri.EscapeDataString(src)}");
        sb.Append($"&target={Uri.EscapeDataString(tgt)}");
        sb.Append("&format=text");
        foreach (var t in texts)
            sb.Append($"&q={Uri.EscapeDataString(t)}");

        var resp = await _http.GetAsync(sb.ToString(), ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        var doc = JsonNode.Parse(json);
        var translations = doc?["data"]?["translations"]?.AsArray()
                           ?? throw new Exception("Unexpected Google Translate response.");

        return translations
            .Select(t => t?["translatedText"]?.GetValue<string>() ?? string.Empty)
            .ToList();
    }

    // ── Language name lookup ──────────────────────────────────────────────────

    private static string LangName(string code) => code.ToLowerInvariant() switch
    {
        "en"    => "English",
        "vi"    => "Vietnamese",
        "zh"    => "Chinese (Simplified)",
        "zh-tw" => "Chinese (Traditional)",
        "ja"    => "Japanese",
        "ko"    => "Korean",
        "fr"    => "French",
        "de"    => "German",
        "es"    => "Spanish",
        "pt"    => "Portuguese",
        "ru"    => "Russian",
        "ar"    => "Arabic",
        "th"    => "Thai",
        "it"    => "Italian",
        "nl"    => "Dutch",
        "pl"    => "Polish",
        "tr"    => "Turkish",
        "id"    => "Indonesian",
        "ms"    => "Malay",
        _       => code
    };

    // ── Helper ────────────────────────────────────────────────────────────────

    private static List<string> ParseJsonArray(string content, int expectedCount)
    {
        // Strip markdown code fences if present
        content = content.Trim();
        if (content.StartsWith("```"))
        {
            var end = content.LastIndexOf("```");
            var start = content.IndexOf('\n') + 1;
            if (end > start) content = content[start..end].Trim();
        }

        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(content, _jsonOpts);
            if (arr != null && arr.Count > 0)
            {
                if (expectedCount < 0) return arr;
                // Pad with empty strings if short; truncate if long.
                while (arr.Count < expectedCount) arr.Add(string.Empty);
                return arr.Count > expectedCount ? arr.Take(expectedCount).ToList() : arr;
            }
        }
        catch { /* fall through */ }

        // Last resort: empty strings so callers can fall back to original text.
        if (expectedCount < 0) return [content];
        return Enumerable.Repeat(string.Empty, expectedCount).ToList();
    }
}
