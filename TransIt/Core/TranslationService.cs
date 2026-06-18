using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TransIt.Core;

namespace TransIt.Core;

public class TranslationService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly HttpClient _httpVision = new() { Timeout = TimeSpan.FromSeconds(120) };
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly AppSettings _settings;

    public TranslationService(AppSettings settings) => _settings = settings;

    public record TranslatableBlock(int Id, string Text, double X, double Y, double W, double H);

    // Sends each block's text together with its on-screen rectangle so the model has layout
    // context (e.g. table cells vs. paragraphs). Rects never round-trip back — mapping to the
    // local OcrBlock rects happens here via Id, which avoids the shuffling risk of a bare
    // positional string array if the model reorders or drops an item.
    public async Task<Dictionary<int, string>> TranslateBlocksAsync(
        IList<TranslatableBlock> blocks,
        string sourceLang,
        string targetLang,
        CancellationToken ct = default)
    {
        if (blocks.Count == 0) return new Dictionary<int, string>();

        return _settings.Provider == TranslationProvider.OpenAI
            ? await TranslateBlocksOpenAiAsync(blocks, sourceLang, targetLang, ct)
            : await TranslateBlocksGoogleAsync(blocks, sourceLang, targetLang, ct);
    }

    private async Task<Dictionary<int, string>> TranslateBlocksOpenAiAsync(
        IList<TranslatableBlock> blocks, string src, string tgt, CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(blocks);
        Console.WriteLine("===================");
        Console.WriteLine($"TranslateBlocksOpenAiAsync inputJson = {inputJson}");
        var systemPrompt =
            $"You are a professional translator from {src} to {tgt}. Follow these rules exactly:\n" +
            $"1. Translate every item's \"Text\" field from {src} to {tgt}.\n" +
            $"2. Prioritize conveying the correct meaning and intent — do NOT translate word-for-word. " +
            $"Use natural, idiomatic {tgt} phrasing. Lightly polish the sentence flow so the result reads smoothly and is easy to understand, as if written by a native {tgt} speaker.\n" +
            $"3. Leave UNCHANGED: mathematical and technical symbols (→ ← ↑ ↓ ≤ ≥ ≠ ≈ ± × ÷ ∑ √ ∞ ∈ ∉ ⊂ ⊃ ∩ ∪ ∀ ∃ etc.), " +
            $"ASCII operators and punctuation used as symbols (-> >= <= == != => ** // etc.), " +
            $"identifiers in camelCase / PascalCase / snake_case / SCREAMING_SNAKE_CASE, " +
            $"proper nouns and personal names, URLs, file paths, version numbers, and numeric values.\n" +
            $"4. Preserve the original punctuation structure: hyphens, dashes, commas, and end-of-line periods.\n" +
            $"5. Each input item has an \"Id\" and a rectangle (X, Y, W, H) giving its position on screen — use the rectangle only as layout context (e.g. to tell paragraphs apart from table cells), do not include X/Y/W/H in your output.\n" +
            $"6. Return ONLY a valid JSON array of objects shaped like {{\"id\": <int>, \"translatedText\": \"...\"}}, one per input item, with \"id\" exactly matching an input item's \"Id\". No markdown fences, no explanations.";

        var body = new
        {
            model = _settings.OpenAiModel,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = inputJson }
            },
            temperature = 0.5
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
        Console.WriteLine("===================");
        Console.WriteLine($"OpenAI response content = {content}");

        return ParseJsonIdArray(content, blocks.Select(b => b.Id).ToList());
    }

    private async Task<Dictionary<int, string>> TranslateBlocksGoogleAsync(
        IList<TranslatableBlock> blocks, string src, string tgt, CancellationToken ct)
    {
        var texts = blocks.Select(b => b.Text).ToList();
        var translated = await TranslateGoogleAsync(texts, src, tgt, ct);

        var result = new Dictionary<int, string>();
        for (int i = 0; i < blocks.Count; i++)
            result[blocks[i].Id] = i < translated.Count ? translated[i] : string.Empty;
        return result;
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

    // ── Summarize ─────────────────────────────────────────────────────────────

    public static string GetSummarizeImagesSystemPrompt(int sliceCount, string sourceLang, string contentType = "other")
    {
        var preamble = $"You are a summarization assistant. The user captured a long page and split it into {sliceCount} vertical slices (top to bottom, with slight overlap). Read all slices in order as a single continuous document. The source content is in {sourceLang}. Respond in Vietnamese.";

        return contentType switch
        {
            "email" =>
                preamble +
                "\n\nThe content is an email thread captured top-to-bottom, meaning the NEWEST message appears first and the OLDEST last. Ignore From/To/CC/Subject/Date headers, signatures, footers, and legal disclaimers." +
                "\n\nStructure your response exactly:" +
                "\n1. One line: 'Chủ đề: [one-sentence subject summary of the whole thread]'." +
                "\n2. A blank line." +
                "\n3. Thread flow in CHRONOLOGICAL order (oldest → newest): list each message as '• [Người gửi]: [tóm tắt 1 câu nội dung]'." +
                "\n4. A blank line." +
                "\n5. Only if the thread contains actions, confirmations, or deadlines for the user: add a blank line then 'Hành động cần làm:' followed by bullet list. If none exist, omit this section entirely." +
                "\nNo headings, no markdown fences, no extra commentary.",

            "code" =>
                preamble +
                "\n\nThe content is source code." +
                "\n\nStructure your response exactly:" +
                "\n1. One line: 'Ngôn ngữ: [detected language]'." +
                "\n2. A blank line." +
                "\n3. One short paragraph (2-3 sentences) describing the overall purpose." +
                "\n4. A blank line." +
                "\n5. Function/method summary as bullet list: '• [tên hàm]: [mục đích 1 câu]', one per line." +
                "\nNo markdown fences, no extra commentary.",

            "document" or "webpage" =>
                preamble +
                "\n\nIgnore navigation bars, ads, sidebars, headers, and footers — focus on the main content." +
                "\n\nStructure your response exactly:" +
                "\n1. One short paragraph (2-3 sentences) capturing the overall topic." +
                "\n2. A blank line." +
                "\n3. Key points as bullet list using '•', one per line, each brief (1 sentence max)." +
                "\nNo headings, no markdown fences, no extra commentary.",

            _ =>
                preamble +
                "\n\nStructure your response exactly:" +
                "\n1. One short paragraph (2-3 sentences) capturing the overall topic." +
                "\n2. A blank line." +
                "\n3. Key points as bullet list using '•', one per line, each brief (1 sentence max)." +
                "\nNo headings, no markdown fences, no extra commentary."
        };
    }

    // Sends a full-screen screenshot to the model and returns a single content-type word.
    // Used to adapt the summarize system prompt before scroll-capture sessions.
    public static async Task<string> DetectContentTypeAsync(
        string apiKey, string model, byte[] jpeg, CancellationToken ct = default)
    {
        // Detection requires a vision-capable model regardless of the user's configured model.
        // gpt-4o-mini supports image input, is fast, and cheap (~5 output tokens).
        var visionModel = model.StartsWith("gpt-4o") ? model : "gpt-4o-mini";
        var b64 = Convert.ToBase64String(jpeg);
        var body = new
        {
            model = visionModel,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "Look at this screenshot. What type of content is primarily shown? Reply with exactly one word: email, document, code, webpage, or other." },
                        new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{b64}", detail = "low" } }
                    }
                }
            },
            max_tokens = 5,
            temperature = 0
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        var resp = await _http.SendAsync(request, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var doc = JsonNode.Parse(json);
        var word = doc?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                       ?.Trim().ToLowerInvariant() ?? "other";

        return word is "email" or "document" or "code" or "webpage" ? word : "other";
    }

    public static string GetSummarizeTextSystemPrompt(string sourceLang) =>
        $"You are a summarization assistant. Summarize the following text in Vietnamese. " +
        $"The source text is in {sourceLang}. " +
        $"IMPORTANT: Structure your response in exactly this format:\n" +
        $"1. One short concise paragraph (2-3 sentences max) capturing the overall meaning.\n" +
        $"2. A blank line.\n" +
        $"3. Key points as a bullet list using '•' character, one point per line, each point brief (1 sentence max).\n" +
        $"No headings, no extra commentary, no markdown fences. Return only the paragraph and bullet list.";

    // Sends captured scroll screenshots to OpenAI Vision API and returns a Vietnamese summary.
    // Each jpeg is sent as a low-detail image_url part to keep token cost minimal (~85 tokens/image).
    public async Task<string> SummarizeImagesAsync(
        IList<byte[]> jpegImages,
        string sourceLang,
        CancellationToken ct = default)
    {
        if (jpegImages.Count == 0) return string.Empty;
        if (_settings.Provider != TranslationProvider.OpenAI)
            throw new NotSupportedException("Image summarization requires OpenAI provider.");

        var systemPrompt = GetSummarizeImagesSystemPrompt(jpegImages.Count, sourceLang);

        var contentParts = new List<object>
        {
            new { type = "text", text = $"Here are {jpegImages.Count} vertical slices of a stitched page (top to bottom):" }
        };
        foreach (var jpeg in jpegImages)
        {
            var b64 = Convert.ToBase64String(jpeg);
            contentParts.Add(new
            {
                type = "image_url",
                image_url = new { url = $"data:image/jpeg;base64,{b64}", detail = "high" }
            });
        }

        var body = new
        {
            model = _settings.OpenAiModel,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = contentParts }
            },
            temperature = 0.5,
            max_tokens = 2000
        };

        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.OpenAiApiKey);

        var resp = await _httpVision.SendAsync(request, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        var doc = JsonNode.Parse(json);
        return doc?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
               ?? throw new Exception("Unexpected OpenAI response shape.");
    }

    public async Task<string> SummarizeAsync(
        string text,
        string sourceLang,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        if (_settings.Provider != TranslationProvider.OpenAI)
            throw new NotSupportedException("Summarization requires OpenAI provider.");

        var systemPrompt = GetSummarizeTextSystemPrompt(sourceLang);

        var body = new
        {
            model = _settings.OpenAiModel,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = text }
            },
            temperature = 0.5
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
        return doc?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
               ?? throw new Exception("Unexpected OpenAI response shape.");
    }

    // ── Chat ──────────────────────────────────────────────────────────────────

    // Multi-turn chat continuing from an initial summarize context (images or text).
    // contextImages XOR contextText must be non-null — they represent the first user message.
    // history contains all turns after the initial exchange (alternating user/assistant).
    public async Task<string> ChatAsync(
        string systemPrompt,
        IList<byte[]>? contextImages,
        string? contextText,
        IList<(string Role, string Content)> history,
        string newUserMessage,
        CancellationToken ct = default)
    {
        if (_settings.Provider != TranslationProvider.OpenAI)
            throw new NotSupportedException("Chat requires OpenAI provider.");

        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        // First user message: images or text from the initial capture
        if (contextImages != null && contextImages.Count > 0)
        {
            var parts = new List<object>
            {
                new { type = "text", text = $"Here are {contextImages.Count} vertical slices of a stitched page (top to bottom):" }
            };
            foreach (var jpeg in contextImages)
            {
                var b64 = Convert.ToBase64String(jpeg);
                parts.Add(new
                {
                    type = "image_url",
                    image_url = new { url = $"data:image/jpeg;base64,{b64}", detail = "high" }
                });
            }
            messages.Add(new { role = "user", content = (object)parts });
        }
        else if (!string.IsNullOrEmpty(contextText))
        {
            messages.Add(new { role = "user", content = (object)contextText });
        }

        // Prior conversation turns
        foreach (var (role, content) in history)
            messages.Add(new { role, content = (object)content });

        // New user question
        messages.Add(new { role = "user", content = (object)newUserMessage });

        var body = new
        {
            model = _settings.OpenAiModel,
            messages,
            temperature = 0.7,
            max_tokens = 2000
        };

        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.OpenAiApiKey);

        var resp = await _httpVision.SendAsync(request, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        var doc = JsonNode.Parse(json);
        return doc?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
               ?? throw new Exception("Unexpected OpenAI response shape.");
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static string StripMarkdownFences(string content)
    {
        content = content.Trim();
        if (content.StartsWith("```"))
        {
            var end = content.LastIndexOf("```");
            var start = content.IndexOf('\n') + 1;
            if (end > start) content = content[start..end].Trim();
        }
        return content;
    }

    private record TranslatedIdItem(int Id, string TranslatedText);

    private static Dictionary<int, string> ParseJsonIdArray(string content, IReadOnlyCollection<int> expectedIds)
    {
        content = StripMarkdownFences(content);

        var result = new Dictionary<int, string>();
        try
        {
            var items = JsonSerializer.Deserialize<List<TranslatedIdItem>>(content, _jsonOpts);
            if (items != null)
                foreach (var item in items)
                    result[item.Id] = item.TranslatedText;
        }
        catch { /* fall through */ }

        foreach (var id in expectedIds)
            result.TryAdd(id, string.Empty);
        return result;
    }

}
