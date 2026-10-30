using System.Text.Encodings.Web;
using System.Text.Json;

namespace AutoTranslator;

/// <summary>
/// 工具配置，保存在 exe 同目录的 config.json 里，用户可手动编辑。
/// </summary>
public sealed class Config
{
    /// <summary>SPT 安装根目录（包含 user/mods 的那一层）。为空表示尚未设置。</summary>
    public string SptPath { get; set; } = "";

    /// <summary>目标语言代码，默认 ch（SPT 简体中文）。</summary>
    public string TargetLang { get; set; } = "ch";

    /// <summary>是否同时再写一份 cn.json（给认 cn 不认 ch 的旧版本兜底）。默认 false。</summary>
    public bool AlsoWriteCn { get; set; } = false;

    /// <summary>翻译源：google_free（免费谷歌，不用 key）或 deepl（需 key）。</summary>
    public string Provider { get; set; } = "google_free";

    /// <summary>DeepL API Key（免费版用 api-free 端点）。只在 Provider=deepl 时使用。</summary>
    public string DeeplApiKey { get; set; } = "";

    /// <summary>DeepL 端点。免费版默认 api-free；Pro 账号请改成 https://api.deepl.com/v2/translate。</summary>
    public string DeeplEndpoint { get; set; } = "https://api-free.deepl.com/v2/translate";

    /// <summary>跳过这些 mod（文件夹名，忽略大小写）。</summary>
    public List<string> ExcludeMods { get; set; } = new();

    /// <summary>每次翻译请求之间的间隔毫秒数。免费谷歌建议不低于 300，避免限流。</summary>
    public int DelayMs { get; set; } = 300;

    public static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<Config>(json);
                if (cfg is not null)
                {
                    return cfg;
                }
            }
        }
        catch
        {
            // 配置损坏时不崩溃，用默认配置继续，并在保存时覆盖。
        }
        return new Config();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(FilePath, json);
    }

}
