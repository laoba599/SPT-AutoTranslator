using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace AutoTranslator;

/// <summary>
/// SPT 离线翻译工具 v1：控制台菜单版。
/// 使用流程（务必在 SPT 服务器关闭时运行）：
///   设路径 → 扫描 → 翻译（自动备份）→ 开服进游戏；出问题 → 一键恢复。
/// 翻译只补目标语言里缺失的 key，绝不覆盖已有译文；源文件（英文）永远不动。
/// </summary>
internal static class Program
{
    private static Config _cfg = new();
    private static string _exeDir = AppContext.BaseDirectory;
    private static ScanResult? _lastScan;

    private static async Task Main()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        _cfg = Config.Load();

        Console.WriteLine("=== SPT 离线翻译工具 v1 ===");
        Console.WriteLine("注意：请在 SPT 服务器关闭时运行本工具，翻译完成后再开服。");
        Console.WriteLine();

        while (true)
        {
            ShowStatus();
            Console.WriteLine("1. 设置 SPT 路径");
            Console.WriteLine("2. 扫描语言文件");
            Console.WriteLine("3. 翻译缺失条目（自动备份）");
            Console.WriteLine("4. 一键恢复（恢复到最近一次备份）");
            Console.WriteLine("5. 设置翻译源 / API Key");
            Console.WriteLine("6. 退出");
            Console.Write("请选择（1-6）：");
            var choice = Console.ReadLine()?.Trim();
            Console.WriteLine();
            try
            {
                switch (choice)
                {
                    case "1": SetSptPath(); break;
                    case "2": DoScan(); break;
                    case "3": await DoTranslateAsync(); break;
                    case "4": DoRestore(); break;
                    case "5": SetProvider(); break;
                    case "6":
                        _cfg.Save();
                        Console.WriteLine("已退出。再见。");
                        return;
                    default:
                        Console.WriteLine("输入无效，请输入 1-6。");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"出错：{ex.Message}");
            }
            Console.WriteLine();
        }
    }

    private static void ShowStatus()
    {
        Console.WriteLine($"SPT 路径：{(string.IsNullOrWhiteSpace(_cfg.SptPath) ? "（未设置）" : _cfg.SptPath)}");
        Console.WriteLine($"翻译源：{_cfg.Provider}    目标语言：{_cfg.TargetLang}" +
            (_cfg.AlsoWriteCn ? "（同时写 cn.json）" : ""));
        Console.WriteLine(new string('-', 50));
    }

    private static void SetSptPath()
    {
        Console.Write("请输入 SPT 安装根目录（包含 user 文件夹的那一层，例如 D:\\SPT）：");
        var path = Console.ReadLine()?.Trim().Trim('"') ?? "";
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(Path.Combine(path, "user", "mods")))
        {
            Console.WriteLine("该目录下找不到 user\\mods，请确认路径是否正确（SPT 根目录）。");
            return;
        }
        _cfg.SptPath = path;
        _cfg.Save();
        _lastScan = null;
        Console.WriteLine("已保存。");
    }

    private static void DoScan()
    {
        var scan = RequireScan();
        PrintScan(scan);
        _lastScan = scan;
    }

    private static ScanResult RequireScan()
    {
        if (string.IsNullOrWhiteSpace(_cfg.SptPath))
        {
            throw new InvalidOperationException("请先设置 SPT 路径（菜单 1）。");
        }
        return Scanner.Scan(_cfg, _exeDir);
    }

    private static void PrintScan(ScanResult scan)
    {
        Console.WriteLine($"扫描完成：共读 {scan.ScannedFiles} 个 JSON 文件，" +
            $"{scan.TotalFiles} 个文件需要翻译，共缺 {scan.TotalMissingKeys} 条。");
        foreach (var job in scan.LocaleFileJobs)
        {
            Console.WriteLine($"  [任务文本] {job.ModName} / {Path.GetFileName(job.TargetPath)}" +
                $"（源：{Path.GetFileName(job.SourcePath)}，缺 {job.MissingKeys.Count} 条）");
        }
        foreach (var job in scan.EmbeddedJobs)
        {
            var missing = job.Blocks.Sum(b => b.MissingKeys.Count);
            Console.WriteLine($"  [内嵌文本] {job.ModName} / {Path.GetFileName(job.FilePath)}（缺 {missing} 条）");
        }
        if (scan.ParseFailedFiles > 0)
        {
            Console.WriteLine($"  提示：{scan.ParseFailedFiles} 个 JSON 文件无法解析，已跳过。");
        }
        foreach (var note in scan.Notes)
        {
            Console.WriteLine($"  提示：{note}");
        }
        if (scan.TotalFiles == 0)
        {
            Console.WriteLine("  没有发现缺失翻译：要么都已是中文，要么没找到英文源文件。");
        }
    }

    private static async Task DoTranslateAsync()
    {
        var scan = _lastScan ?? RequireScan();
        if (scan.TotalFiles == 0)
        {
            PrintScan(scan);
            return;
        }
        PrintScan(scan);

        if (IsServerRunning())
        {
            Console.WriteLine("警告：检测到 SPT 服务器正在运行！运行中翻译不会生效，还可能写坏正在读的文件。");
            if (!Confirm("仍要继续吗？"))
            {
                Console.WriteLine("已取消。请先关闭 SPT 服务器再翻译。");
                return;
            }
        }
        if (!Confirm("开始翻译并自动备份吗？"))
        {
            Console.WriteLine("已取消。");
            return;
        }

        var translator = BuildTranslator();
        var caching = new CachingTranslator(
            translator, Path.Combine(_exeDir, "cache.json"));
        var backup = new BackupStore(_exeDir, _cfg.SptPath);
        backup.Begin();

        var translated = 0;
        var fromCache = 0;
        var skipped = 0;
        var failed = new List<string>();
        var done = 0;
        var total = scan.TotalMissingKeys;

        // 任务文本类：只补目标文件里缺失的 key。
        foreach (var job in scan.LocaleFileJobs)
        {
            if (job.TargetExists)
            {
                backup.BackupExisting(job.TargetPath);
            }
            else
            {
                backup.RecordCreated(job.TargetPath);
            }
            var ok = await TranslateKeysAsync(
                job.SourceDict, job.TargetDict, job.MissingKeys,
                caching, job.TargetPath, failed,
                () => { translated++; }, () => { fromCache++; }, () => { skipped++; });
            done += job.MissingKeys.Count;
            Console.WriteLine($"  已处理 {job.ModName} / {Path.GetFileName(job.TargetPath)}（进度 {done}/{total}）");
            try
            {
                JsonFile.Save(job.TargetDict, job.TargetPath);
            }
            catch (Exception ex)
            {
                failed.Add($"{job.TargetPath} 写文件失败：{ex.Message}");
            }
            _ = ok;
        }

        // 内嵌类：整文件改完后一次写回（先备份原文件）。
        foreach (var job in scan.EmbeddedJobs)
        {
            backup.BackupExisting(job.FilePath);
            foreach (var block in job.Blocks)
            {
                var targetDict = EnsureTargetDict(block.LocalesNode, _cfg.TargetLang);
                await TranslateKeysAsync(
                    block.SourceDict, targetDict, block.MissingKeys,
                    caching, job.FilePath, failed,
                    () => { translated++; }, () => { fromCache++; }, () => { skipped++; });
                if (_cfg.AlsoWriteCn && _cfg.TargetLang == "ch")
                {
                    // cn 与 ch 内容相同，直接复制已翻好的 ch 段，不再调接口。
                    var cnDict = EnsureTargetDict(block.LocalesNode, "cn");
                    foreach (var key in block.MissingKeys)
                    {
                        if (targetDict.TryGetPropertyValue(key, out var v))
                        {
                            cnDict[key] = v?.DeepClone();
                        }
                    }
                }
            }
            try
            {
                JsonFile.Save(job.Root, job.FilePath);
                done += job.Blocks.Sum(b => b.MissingKeys.Count);
                Console.WriteLine($"  已处理 {job.ModName} / {Path.GetFileName(job.FilePath)}（进度 {done}/{total}）");
            }
            catch (Exception ex)
            {
                failed.Add($"{job.FilePath} 写文件失败：{ex.Message}");
            }
        }

        caching.Save();
        backup.Commit();
        _lastScan = null; // 翻译后扫描结果已过期，下次重新扫描。

        Console.WriteLine();
        Console.WriteLine($"翻译完成：新翻 {translated} 条，缓存命中 {fromCache} 条，跳过 {skipped} 条，失败 {failed.Count} 条。");
        foreach (var f in failed.Take(10))
        {
            Console.WriteLine($"  失败：{f}");
        }
        if (backup.CurrentSnapshotDir is not null)
        {
            Console.WriteLine($"备份快照：{backup.CurrentSnapshotDir}");
            Console.WriteLine("如进游戏不正常，用菜单 4 一键恢复到这次备份之前的状态。");
        }
        else
        {
            Console.WriteLine("本次没有任何文件改动，未产生备份快照。");
        }
    }

    /// <summary>翻译一批 key：逐条调缓存翻译器，失败的 key 记下来并跳过，不中断整批。</summary>
    private static async Task<bool> TranslateKeysAsync(
        JsonObject source, JsonObject target, List<string> keys,
        CachingTranslator caching, string fileHint, List<string> failed,
        Action onTranslated, Action onCache, Action onSkipped)
    {
        foreach (var key in keys)
        {
            if (!source.TryGetPropertyValue(key, out var node)
                || node is not JsonValue value
                || !value.TryGetValue<string>(out var text))
            {
                continue;
            }
            try
            {
                var (result, wasCached, wasSkipped, liveSegments, cachedSegments) =
                    await caching.TranslateAsync(
                        text, "en", _cfg.TargetLang, _cfg.DelayMs, CancellationToken.None);
                target[key] = JsonValue.Create(result);
                if (wasSkipped) { onSkipped(); }
                else if (wasCached || (liveSegments == 0 && cachedSegments > 0)) { onCache(); }
                else { onTranslated(); }
            }
            catch (Exception ex)
            {
                failed.Add($"{fileHint} / {key}：{ex.Message}");
            }
        }
        return true;
    }

    private static JsonObject EnsureTargetDict(JsonObject localesNode, string lang)
    {
        if (localesNode.TryGetPropertyValue(lang, out var node)
            && node is JsonObject existing)
        {
            return existing;
        }
        var created = new JsonObject();
        localesNode[lang] = created;
        return created;
    }

    private static void DoRestore()
    {
        if (string.IsNullOrWhiteSpace(_cfg.SptPath))
        {
            throw new InvalidOperationException("请先设置 SPT 路径（菜单 1）。");
        }
        var store = new BackupStore(_exeDir, _cfg.SptPath);
        var snapshots = store.ListSnapshots();
        if (snapshots.Count == 0)
        {
            Console.WriteLine("没有任何备份快照，无法恢复。");
            return;
        }
        var (dir, manifest) = snapshots[0];
        Console.WriteLine($"最近一次备份：{manifest.CreatedAt}，共 {manifest.Entries.Count} 个文件。");
        if (!Confirm("确认恢复到这次备份之前的状态吗？"))
        {
            Console.WriteLine("已取消。");
            return;
        }
        var (restored, failures) = store.Restore(dir, manifest);
        Console.WriteLine($"恢复完成：{restored} 个文件，失败 {failures.Count} 个。");
        foreach (var f in failures.Take(10))
        {
            Console.WriteLine($"  失败：{f}");
        }
        _lastScan = null;
    }

    private static void SetProvider()
    {
        Console.WriteLine("翻译源：1. 免费谷歌（不用 key）  2. DeepL（需 key）");
        Console.Write("请选择（1-2）：");
        var choice = Console.ReadLine()?.Trim();
        if (choice == "2")
        {
            _cfg.Provider = "deepl";
            Console.Write("请输入 DeepL API Key（直接回车表示不改）：");
            var key = Console.ReadLine()?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(key))
            {
                _cfg.DeeplApiKey = key;
            }
            if (string.IsNullOrWhiteSpace(_cfg.DeeplApiKey))
            {
                Console.WriteLine("警告：DeepL Key 为空，翻译时会报错。请先填 Key 再用。");
            }
        }
        else if (choice == "1")
        {
            _cfg.Provider = "google_free";
        }
        else
        {
            Console.WriteLine("输入无效，未修改。");
            return;
        }
        Console.Write("是否同时再写一份 cn.json（给旧版本兜底）？(y/N)：");
        _cfg.AlsoWriteCn = Confirm();
        _cfg.Save();
        Console.WriteLine("已保存。");
    }

    private static ITranslator BuildTranslator() => _cfg.Provider switch
    {
        "deepl" => new DeepLTranslator(_cfg.DeeplApiKey, _cfg.DeeplEndpoint),
        _ => new GoogleFreeTranslator()
    };

    private static bool IsServerRunning()
    {
        try
        {
            return Process.GetProcessesByName("SPT.Server").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>默认 No 的确认：只有输入 y/Y/yes/是 才算确认。</summary>
    private static bool Confirm(string? prompt = null)
    {
        if (prompt is not null)
        {
            Console.Write($"{prompt} (y/N)：");
        }
        var input = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
        return input is "y" or "yes" or "是";
    }
}
