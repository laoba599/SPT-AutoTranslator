using System.Text.Encodings.Web;
using System.Text.Json;

namespace AutoTranslator;

/// <summary>一次备份快照里的条目：modified=覆盖前备份了原文件；created=翻译新建的文件。</summary>
public sealed class ManifestEntry
{
    public required string RelPath { get; set; }
    public required string Action { get; set; }
    /// <summary>modified 时，备份文件在快照内的相对路径；created 时为空。</summary>
    public string? BackupRelPath { get; set; }
}

public sealed class SnapshotManifest
{
    public required string CreatedAt { get; set; }
    public required string SptPath { get; set; }
    public required List<ManifestEntry> Entries { get; set; }
}

/// <summary>
/// 快照式备份：每次翻译运行产生一个带时间戳的快照目录，
/// 里面按原相对路径镜像存放被覆盖的原文件，并记一份 manifest。
/// 恢复 = 按最新快照把原文件拷回去、删掉新建的翻译文件，一键完成。
/// </summary>
public sealed class BackupStore
{
    private readonly string _root;
    private readonly string _sptPath;
    private string? _currentDir;
    private readonly Dictionary<string, ManifestEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public BackupStore(string exeDir, string sptPath)
    {
        _root = Path.Combine(exeDir, "backup");
        _sptPath = sptPath;
    }

    public string? CurrentSnapshotDir => _currentDir;

    public void Begin()
    {
        _currentDir = Path.Combine(_root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_currentDir);
        _entries.Clear();
    }

    /// <summary>在覆盖一个已存在文件之前先备份它。同一文件一次运行里只备一次。</summary>
    public void BackupExisting(string fullPath)
    {
        if (_currentDir is null)
        {
            return;
        }
        var rel = Rel(fullPath);
        if (_entries.ContainsKey(rel))
        {
            return;
        }
        var dest = Path.Combine(_currentDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(fullPath, dest, overwrite: true);
        _entries[rel] = new ManifestEntry
        {
            RelPath = rel,
            Action = "modified",
            BackupRelPath = rel
        };
    }

    /// <summary>记录一个翻译新建的文件（恢复时删掉它）。</summary>
    public void RecordCreated(string fullPath)
    {
        if (_currentDir is null)
        {
            return;
        }
        var rel = Rel(fullPath);
        _entries.TryAdd(rel, new ManifestEntry { RelPath = rel, Action = "created" });
    }

    /// <summary>写 manifest；如果本次没有任何改动，删掉空快照目录。</summary>
    public void Commit()
    {
        if (_currentDir is null)
        {
            return;
        }
        if (_entries.Count == 0)
        {
            Directory.Delete(_currentDir, recursive: true);
            _currentDir = null;
            return;
        }
        var manifest = new SnapshotManifest
        {
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            SptPath = _sptPath,
            Entries = _entries.Values.ToList()
        };
        File.WriteAllText(
            Path.Combine(_currentDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
    }

    public List<(string dir, SnapshotManifest manifest)> ListSnapshots()
    {
        var list = new List<(string, SnapshotManifest)>();
        if (!Directory.Exists(_root))
        {
            return list;
        }
        foreach (var dir in Directory.GetDirectories(_root))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }
            try
            {
                var manifest = JsonSerializer.Deserialize<SnapshotManifest>(
                    File.ReadAllText(manifestPath));
                if (manifest is not null)
                {
                    list.Add((dir, manifest));
                }
            }
            catch
            {
                // 损坏的快照直接跳过。
            }
        }
        list.Sort((a, b) => string.CompareOrdinal(b.Item1, a.Item1)); // 时间戳命名，倒序即最新在前。
        return list;
    }

    /// <summary>按快照恢复：modified 拷回原文件，created 删文件。返回 (成功数, 失败信息)。</summary>
    public (int restored, List<string> failures) Restore(string snapshotDir, SnapshotManifest manifest)
    {
        var restored = 0;
        var failures = new List<string>();
        foreach (var entry in manifest.Entries)
        {
            try
            {
                var target = Path.Combine(_sptPath, entry.RelPath);
                if (entry.Action == "modified" && entry.BackupRelPath is not null)
                {
                    var backup = Path.Combine(snapshotDir, entry.BackupRelPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(backup, target, overwrite: true);
                }
                else if (entry.Action == "created")
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
                restored++;
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.RelPath}：{ex.Message}");
            }
        }
        return (restored, failures);
    }

    private string Rel(string fullPath) => Path.GetRelativePath(_sptPath, fullPath);
}
