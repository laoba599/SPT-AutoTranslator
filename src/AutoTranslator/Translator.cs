using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;

namespace AutoTranslator;

/// <summary>翻译器接口：一次翻译一段完整文本。</summary>
public interface ITranslator
{
    string Name { get; }
    Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken ct);
}

/// <summary>
/// 文本切分与保护：把占位符、标签、链接、中文片段切成“原文段”，
/// 只把剩余的可翻译段送给翻译接口，拼回来时占位符分毫不差。
/// </summary>
public static class TextSegmenter
{
    // {0}、{name}、<b>、<color=#fff>、%s、%d、链接、[tag]、中文片段及中文标点
    private static readonly Regex LiteralPattern = new(
        @"\{[^{}]*\}|<[^<>]*>|https?://\S+|%[sdif]|\[[A-Za-z0-9_=/.\-]{1,30}\]|[\u4e00-\u9fff\u3400-\u4dbf\u3000-\u303f\uff00-\uffef]+",
        RegexOptions.Compiled);

    /// <summary>切分文本，返回 (是否原文保留, 片段) 列表。</summary>
    public static List<(bool literal, string text)> Split(string text)
    {
        var result = new List<(bool, string)>();
        var pos = 0;
        foreach (Match m in LiteralPattern.Matches(text))
        {
            if (m.Index > pos)
            {
                result.Add((false, text[pos..m.Index]));
            }
            result.Add((true, m.Value));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
        {
            result.Add((false, text[pos..]));
        }
        return result;
    }

    /// <summary>整句是否完全不用翻译（空白、纯符号数字、或已是中文）。</summary>
    public static bool NeedsNoTranslation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }
        // 去掉占位符/标签后如果只剩符号和数字，不用翻译。
        var stripped = LiteralPattern.Replace(text, "");
        return !stripped.Any(char.IsLetter);
    }
}

/// <summary>
/// 带缓存的翻译编排：先查缓存，未命中才调接口；可翻译段逐段翻译后拼回。
/// </summary>
public sealed class CachingTranslator
{
    private readonly ITranslator _inner;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly string _cachePath;
    private bool _dirty;

    public CachingTranslator(ITranslator inner, string cachePath)
    {
        _inner = inner;
        _cachePath = cachePath;
        try
        {
            if (File.Exists(cachePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(cachePath));
                if (loaded is not null)
                {
                    foreach (var kv in loaded)
                    {
                        _cache[kv.Key] = kv.Value;
                    }
                }
            }
        }
        catch
        {
            // 缓存损坏时丢弃重建，不影响翻译本身。
        }
    }

    public string ProviderName => _inner.Name;
    public int CachedCount => _cache.Count;

    /// <summary>
    /// 翻译一句文本。返回 (译文, 整句缓存命中, 跳过, 实时翻译的段数, 段缓存命中的段数)。
    /// 缓存分两级：整句缓存（全文命中直接返回）和段缓存（占位符切分后的可翻译段），
    /// 这样带占位符的句子也能复用已翻过的段，不会把占位符送进翻译接口。
    /// 跳过指原文无需翻译（如纯符号、已是中文），此时译文等于原文。
    /// </summary>
    public async Task<(string text, bool fromCache, bool skipped, int liveSegments, int cachedSegments)> TranslateAsync(
        string text, string sourceLang, string targetLang, int delayMs, CancellationToken ct)
    {
        if (TextSegmenter.NeedsNoTranslation(text))
        {
            return (text, false, true, 0, 0);
        }

        var fullKey = $"full|{_inner.Name}|{sourceLang}|{targetLang}|{text}";
        if (_cache.TryGetValue(fullKey, out var hit))
        {
            return (hit, true, false, 0, 0);
        }

        // 逐段翻译：原文段原样保留，可翻译段先查段缓存，未命中才调接口。
        var sb = new StringBuilder();
        var liveSegments = 0;
        var cachedSegments = 0;
        var anyTranslated = false;
        foreach (var (literal, part) in TextSegmenter.Split(text))
        {
            if (literal || string.IsNullOrWhiteSpace(part))
            {
                sb.Append(part);
                continue;
            }
            var segKey = $"seg|{_inner.Name}|{sourceLang}|{targetLang}|{part}";
            string translated;
            if (_cache.TryGetValue(segKey, out var segHit))
            {
                translated = segHit;
                cachedSegments++;
            }
            else
            {
                translated = await _inner.TranslateAsync(part, sourceLang, targetLang, ct);
                _cache[segKey] = translated;
                _dirty = true;
                liveSegments++;
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, ct);
                }
            }
            sb.Append(translated);
            anyTranslated = true;
        }

        var result = anyTranslated ? sb.ToString() : text;
        _cache[fullKey] = result;
        _dirty = true;
        return (result, false, false, liveSegments, cachedSegments);
    }

    public void Save()
    {
        if (!_dirty)
        {
            return;
        }
        var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        // 先写临时文件再替换，避免中途退出写坏缓存。
        var tmp = _cachePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _cachePath, overwrite: true);
        _dirty = false;
    }
}

/// <summary>
/// 免费谷歌翻译（非官方 gtx 接口，不用 key）。
/// 注意：该接口无官方保障，部分网络下会被谷歌风控页拦截（表现为返回 HTML），
/// 此时应稍后重试或改用 DeepL。
/// </summary>
public sealed class GoogleFreeTranslator : ITranslator
{
    public string Name => "google_free";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static GoogleFreeTranslator()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
    }

    public async Task<string> TranslateAsync(
        string text, string sourceLang, string targetLang, CancellationToken ct)
    {
        var googleTarget = targetLang is "ch" or "cn" ? "zh-CN" : targetLang;
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx"
            + $"&sl={Uri.EscapeDataString(sourceLang)}"
            + $"&tl={Uri.EscapeDataString(googleTarget)}"
            + $"&dt=t&q={Uri.EscapeDataString(text)}";

        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var resp = await Http.GetAsync(url, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (body.StartsWith("<", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "谷歌免费接口返回了风控页（当前网络被谷歌限制），请稍后重试或改用 DeepL。");
                }
                if (!resp.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"谷歌免费接口 HTTP {(int)resp.StatusCode}");
                }
                return ParseResponse(body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
                await Task.Delay(1000 * (attempt + 1), ct);
            }
        }
        throw new InvalidOperationException("谷歌免费接口请求失败（已重试 3 次）。", lastError);
    }

    /// <summary>
    /// 解析 gtx 接口返回：形如 [[["译文","原文",...]],null,"en"]，取每段译文拼起来。
    /// 独立成方法，便于用固定样本做离线单测。
    /// </summary>
    public static string ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("谷歌免费接口返回格式异常。");
        }
        var sb = new StringBuilder();
        foreach (var seg in root[0].EnumerateArray())
        {
            if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0
                && seg[0].ValueKind == JsonValueKind.String)
            {
                sb.Append(seg[0].GetString());
            }
        }
        return sb.ToString();
    }
}

/// <summary>DeepL 翻译（需 API Key）。默认免费版端点，Pro 账号可在配置里改端点。</summary>
public sealed class DeepLTranslator : ITranslator
{
    public string Name => "deepl";

    private readonly string _apiKey;
    private readonly string _endpoint;
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public DeepLTranslator(string apiKey, string endpoint)
    {
        _apiKey = apiKey;
        _endpoint = endpoint;
    }

    public async Task<string> TranslateAsync(
        string text, string sourceLang, string targetLang, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("未填写 DeepL API Key，请先在设置里填写。");
        }
        var deeplTarget = targetLang is "ch" or "cn" ? "ZH" : targetLang.ToUpperInvariant();
        var deeplSource = sourceLang.ToUpperInvariant();
        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("text", text),
            new KeyValuePair<string, string>("source_lang", deeplSource),
            new KeyValuePair<string, string>("target_lang", deeplTarget),
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", _apiKey);
        req.Content = form;
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"DeepL 接口 HTTP {(int)resp.StatusCode}：{body[..Math.Min(body.Length, 200)]}" +
                "（免费版 Key 请确认端点是 api-free；Pro Key 请把端点改成 api.deepl.com）");
        }
        using var doc = JsonDocument.Parse(body);
        var translations = doc.RootElement.GetProperty("translations");
        if (translations.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("DeepL 返回为空。");
        }
        return translations[0].GetProperty("text").GetString() ?? text;
    }
}

/// <summary>JSON 读写：统一 UTF-8 无 BOM，写文件先写临时文件再替换，避免写一半损坏。</summary>
public static class JsonFile
{
    private static readonly System.Text.Encoding Utf8NoBom = new UTF8Encoding(false);

    public static JsonNode? Load(string path)
    {
        return JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions
        {
            PropertyNameCaseInsensitive = false
        });
    }

    public static void Save(JsonNode node, string path)
    {
        var json = node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            // 中文直接写汉字而不是 \uXXXX，人才读得懂；SPT 读 UTF-8 完全没问题。
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, Utf8NoBom);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>判断一个 JSON 对象是不是“扁平词典”（所有值都是字符串），即任务文本类语言文件。</summary>
    public static bool IsFlatStringDict(
        JsonNode? node, [NotNullWhen(true)] out JsonObject? dict)
    {
        dict = null;
        if (node is not JsonObject obj || obj.Count == 0)
        {
            return false;
        }
        foreach (var kv in obj)
        {
            if (kv.Value is not JsonValue v || !v.TryGetValue<string>(out _))
            {
                return false;
            }
        }
        dict = obj;
        return true;
    }
}
