using System.Text.Json.Nodes;

namespace AutoTranslator;

/// <summary>一个待翻译的任务文件（任务文本类）：源是扁平词典，目标是同目录下的 ch.json。</summary>
public sealed class LocaleFileJob
{
    public required string ModName { get; init; }
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }
    public required JsonObject SourceDict { get; init; }
    /// <summary>目标文件已有的内容（不存在时为空对象）；翻译时只补缺失的 key，绝不覆盖已有译文。</summary>
    public required JsonObject TargetDict { get; init; }
    public required bool TargetExists { get; init; }
    /// <summary>源里有、目标里没有的 key（按源文件顺序）。</summary>
    public required List<string> MissingKeys { get; init; }
}

/// <summary>一个内嵌 locales 段里的待翻译块（如物品、服装条目里的 locales.en）。</summary>
public sealed class LocaleBlock
{
    public required JsonObject LocalesNode { get; init; }
    public required JsonObject SourceDict { get; init; }
    public required List<string> MissingKeys { get; init; }
}

/// <summary>一个待翻译的内嵌 locales 文件：翻译时直接改树里的 ch 段，最后整文件写回。</summary>
public sealed class EmbeddedJob
{
    public required string ModName { get; init; }
    public required string FilePath { get; init; }
    public required JsonNode Root { get; init; }
    public required List<LocaleBlock> Blocks { get; init; }
}

public sealed class ScanResult
{
    public List<LocaleFileJob> LocaleFileJobs { get; } = new();
    public List<EmbeddedJob> EmbeddedJobs { get; } = new();
    public int ScannedFiles { get; set; }
    public int ParseFailedFiles { get; set; }
    public int DirsWithoutEnglishSource { get; set; }
    public List<string> Notes { get; } = new();

    public int TotalMissingKeys =>
        LocaleFileJobs.Sum(j => j.MissingKeys.Count) +
        EmbeddedJobs.Sum(j => j.Blocks.Sum(b => b.MissingKeys.Count));

    public int TotalFiles => LocaleFileJobs.Count + EmbeddedJobs.Count;
}

/// <summary>
/// 扫描 user/mods 下各 mod 的语言文件。只读，不写任何文件。
/// 识别两种形态：
///  1. 任务文本类：Locales 文件夹里、或以 en.json / *english*.json 命名的扁平词典；
///  2. 内嵌类：任意 JSON 里带 "locales": { "en": {...} } 段的条目（如物品、服装）。
/// 已有目标语言文件时只补缺失 key，不覆盖已有译文。
/// </summary>
public static class Scanner
{
    public static ScanResult Scan(Config cfg, string exeDir)
    {
        var result = new ScanResult();
        var modsRoot = Path.Combine(cfg.SptPath, "user", "mods");
        if (!Directory.Exists(modsRoot))
        {
            throw new DirectoryNotFoundException(
                $"找不到 mods 目录：{modsRoot}。请先在设置里填正确的 SPT 安装根目录。");
        }

        var excluded = new HashSet<string>(cfg.ExcludeMods, StringComparer.OrdinalIgnoreCase);
        foreach (var modDir in Directory.GetDirectories(modsRoot))
        {
            var modName = Path.GetFileName(modDir);
            if (modName.StartsWith('.') || excluded.Contains(modName))
            {
                continue;
            }
            ScanMod(cfg, exeDir, modDir, modName, result);
        }
        return result;
    }

    private static void ScanMod(
        Config cfg, string exeDir, string modDir, string modName, ScanResult result)
    {
        // 先按目录分组收集任务文本类的源候选（同一 Locales 目录只取一个最优先的源）。
        var candidatesByDir = new Dictionary<string, List<(string path, JsonObject dict)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in EnumerateModJsonFiles(modDir, exeDir))
        {
            JsonNode? root;
            try
            {
                root = JsonFile.Load(file);
                result.ScannedFiles++;
            }
            catch
            {
                result.ParseFailedFiles++;
                continue;
            }

            var fileName = Path.GetFileName(file);
            var parentDirName = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
            var isTargetName = fileName.Equals(cfg.TargetLang + ".json", StringComparison.OrdinalIgnoreCase)
                || (cfg.AlsoWriteCn && fileName.Equals("cn.json", StringComparison.OrdinalIgnoreCase));
            if (isTargetName)
            {
                continue; // 目标文件本身不作为源扫描。
            }

            // 形态 1：扁平词典 + 文件名表明是英文源（en.json、*english*.json，
            // 或在 Locales 目录里且文件名不是已知语言代码/语言名，如 ru.json 会被排除）。
            if (JsonFile.IsFlatStringDict(root, out var dict)
                && IsEnglishSourceCandidate(fileName, parentDirName))
            {
                var dir = Path.GetDirectoryName(file)!;
                if (!candidatesByDir.TryGetValue(dir, out var list))
                {
                    list = new List<(string, JsonObject)>();
                    candidatesByDir[dir] = list;
                }
                list.Add((file, dict!));
                continue;
            }

            // 形态 2：内嵌 locales 段。
            var blocks = FindLocaleBlocks(root, cfg.TargetLang);
            if (blocks.Count > 0)
            {
                result.EmbeddedJobs.Add(new EmbeddedJob
                {
                    ModName = modName,
                    FilePath = file,
                    Root = root!,
                    Blocks = blocks
                });
            }
        }

        // 为每个有候选源的目录生成一个任务文件 Job。
        foreach (var (dir, candidates) in candidatesByDir)
        {
            var source = PickSource(candidates);
            if (source is null)
            {
                result.DirsWithoutEnglishSource++;
                continue;
            }
            var (sourcePath, sourceDict) = source.Value;
            foreach (var targetName in TargetFileNames(cfg))
            {
                var targetPath = Path.Combine(dir, targetName);
                JsonObject targetDict = new();
                var targetExists = File.Exists(targetPath);
                if (targetExists)
                {
                    try
                    {
                        var loaded = JsonFile.Load(targetPath);
                        if (JsonFile.IsFlatStringDict(loaded, out var existing))
                        {
                            targetDict = existing!;
                        }
                    }
                    catch
                    {
                        // 目标文件损坏：不动它，记一条提示。
                        result.Notes.Add($"目标文件无法解析，已跳过：{targetPath}");
                        continue;
                    }
                }
                var missing = sourceDict
                    .Where(kv => !targetDict.ContainsKey(kv.Key))
                    .Select(kv => kv.Key)
                    .ToList();
                if (missing.Count == 0)
                {
                    continue; // 已翻完，不产生任务。
                }
                result.LocaleFileJobs.Add(new LocaleFileJob
                {
                    ModName = modName,
                    SourcePath = sourcePath,
                    TargetPath = targetPath,
                    SourceDict = sourceDict,
                    TargetDict = targetDict,
                    TargetExists = targetExists,
                    MissingKeys = missing
                });
            }
        }
    }

    private static IEnumerable<string> TargetFileNames(Config cfg)
    {
        yield return cfg.TargetLang + ".json";
        if (cfg.AlsoWriteCn && cfg.TargetLang == "ch")
        {
            yield return "cn.json";
        }
    }

    /// <summary>
    /// 判断一个扁平词典文件是不是英文源：en.json / *english*.json 直接认；
    /// 在 Locales 目录里的其他文件，只有文件名不是已知语言代码/语言名时才当英文源
    /// （避免把 ru.json、de.json 这类非英文源当成英文送去翻译）。
    /// </summary>
    private static bool IsEnglishSourceCandidate(string fileName, string parentDirName)
    {
        if (fileName.Equals("en.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("english", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!parentDirName.Equals("Locales", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        return !KnownLanguageStems.Contains(stem);
    }

    private static readonly HashSet<string> KnownLanguageStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "ru", "russian", "de", "german", "fr", "french", "es", "spanish",
        "it", "italian", "pt", "portuguese", "ja", "jp", "japanese",
        "ko", "kr", "korean", "ch", "cn", "chinese", "cs", "cz", "czech",
        "pl", "polish", "uk", "ua", "ukrainian", "tr", "turkish",
        "nl", "dutch", "sv", "se", "swedish", "nb", "no", "norwegian",
        "fi", "finnish", "da", "danish", "hu", "hungarian", "ro", "romanian",
        "el", "gr", "greek", "ar", "arabic", "th", "thai", "vi", "vn", "vietnamese",
        "id", "indonesian", "ms", "malay"
    };

    /// <summary>同一目录多个英文候选时：en.json 优先，其次 *english*.json，最后取 key 最多的。</summary>
    private static (string path, JsonObject dict)? PickSource(
        List<(string path, JsonObject dict)> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }
        var byName = candidates.FirstOrDefault(c =>
            Path.GetFileName(c.path).Equals("en.json", StringComparison.OrdinalIgnoreCase));
        if (byName.path is not null)
        {
            return byName;
        }
        byName = candidates.FirstOrDefault(c =>
            Path.GetFileName(c.path).Contains("english", StringComparison.OrdinalIgnoreCase));
        if (byName.path is not null)
        {
            return byName;
        }
        return candidates.MaxBy(c => c.dict.Count);
    }

    /// <summary>递归找所有带 "locales": { "en": {...} } 的节点，只保留目标语言缺 key 的块。</summary>
    private static List<LocaleBlock> FindLocaleBlocks(JsonNode? node, string targetLang)
    {
        var blocks = new List<LocaleBlock>();
        Walk(node);
        return blocks;

        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject obj:
                    if (obj.TryGetPropertyValue("locales", out var localesNode)
                        && localesNode is JsonObject localesObj
                        && localesObj.TryGetPropertyValue("en", out var enNode)
                        && JsonFile.IsFlatStringDict(enNode, out var enDict))
                    {
                        JsonObject? targetDict = null;
                        if (localesObj.TryGetPropertyValue(targetLang, out var tNode)
                            && JsonFile.IsFlatStringDict(tNode, out var tDict))
                        {
                            targetDict = tDict;
                        }
                        var missing = enDict!
                            .Where(kv => targetDict is null || !targetDict.ContainsKey(kv.Key))
                            .Select(kv => kv.Key)
                            .ToList();
                        if (missing.Count > 0)
                        {
                            blocks.Add(new LocaleBlock
                            {
                                LocalesNode = localesObj,
                                SourceDict = enDict,
                                MissingKeys = missing
                            });
                        }
                    }
                    foreach (var kv in obj)
                    {
                        Walk(kv.Value);
                    }
                    break;
                case JsonArray arr:
                    foreach (var item in arr)
                    {
                        Walk(item);
                    }
                    break;
            }
        }
    }

    private static IEnumerable<string> EnumerateModJsonFiles(string modDir, string exeDir)
    {
        foreach (var file in Directory.EnumerateFiles(modDir, "*.json", SearchOption.AllDirectories))
        {
            // 跳过备份类目录、隐藏目录，以及工具自身所在目录（防止用户把 exe 放进 mods 里导致自扫）。
            var rel = Path.GetRelativePath(modDir, file);
            var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s.Equals("backup", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith('.')))
            {
                continue;
            }
            if (!string.IsNullOrEmpty(exeDir)
                && file.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            yield return file;
        }
    }
}
