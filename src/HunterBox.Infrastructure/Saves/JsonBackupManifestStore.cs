using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HunterBox.Application.Saves;

namespace HunterBox.Infrastructure.Saves;

/// <summary>
/// 备份哈希清单的 JSON 编解码实现：格式细节与版本号只存在于基础设施层。
/// </summary>
public sealed class JsonBackupManifestStore : IBackupManifestStore
{
    /// <summary>当前清单格式版本；与旧版不兼容时按「清单不可读」处理。</summary>
    private const int Version = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <inheritdoc />
    public string Serialize(DateTimeOffset createdAt, IReadOnlyList<BackupManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ManifestDto dto = new()
        {
            Version = Version,
            CreatedAt = createdAt,
            Files = [.. entries.Select(static entry => new FileDto
            {
                Path = entry.Path,
                Size = entry.Size,
                Sha256 = entry.Sha256,
            })],
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    /// <inheritdoc />
    public IReadOnlyList<BackupManifestEntry>? TryDeserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            ManifestDto? dto = JsonSerializer.Deserialize<ManifestDto>(json, JsonOptions);
            if (dto?.Version != Version)
            {
                return null;
            }

            // 任一条目缺失/为 null/路径或哈希为空时整份清单按不可读处理。
            // 不能过滤后放行：未列出的文件不会参与校验，界面却会报告「哈希校验通过」。
            if (dto.Files is null || dto.Files.Count == 0)
            {
                return null;
            }

            List<BackupManifestEntry> entries = new(dto.Files.Count);
            HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
            foreach (FileDto? file in dto.Files)
            {
                if (file is null
                    || string.IsNullOrWhiteSpace(file.Path)
                    || !IsSha256Hex(file.Sha256))
                {
                    return null;
                }

                // `a/./b`、`a//b`、`a/b/` 与 `a/b` 指同一位置，重复即整份不可信。
                if (!paths.Add(BuildManifestComparisonKey(file.Path)))
                {
                    return null;
                }

                entries.Add(new BackupManifestEntry(file.Path, file.Size, file.Sha256));
            }

            return entries;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 仅用于重复检测的比较键（独立规则，不复用严格路径校验）：
    /// 去空段与 <c>.</c> 段、消解 <c>..</c>、去段尾空白/点，保留段首空格（Windows 上 `" a.sav"` 与 `"a.sav"` 是不同文件）。
    /// 非法路径（如保留设备名）仍留给还原前的严格校验报 <c>InvalidPath</c>，这里只保证「同一位置的不同写法」可比较。
    /// </summary>
    /// <param name="path">清单里的相对路径。</param>
    /// <returns>归一后的比较键。</returns>
    private static string BuildManifestComparisonKey(string path)
    {
        string[] segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        List<string> kept = new(segments.Length);
        foreach (string raw in segments)
        {
            string segment = raw.TrimEnd(' ');
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                // Windows 解析会消解上一段；没有可消解的段时保留（严格校验会在还原前拒绝它）。
                if (kept.Count > 0 && kept[^1] != "..")
                {
                    kept.RemoveAt(kept.Count - 1);
                }
                else
                {
                    kept.Add(segment);
                }

                continue;
            }

            // Windows 会忽略段尾的点（"a." 与 "a" 指同一位置）。
            string trimmed = segment.TrimEnd('.');
            if (trimmed.Length > 0)
            {
                kept.Add(trimmed);
            }
        }

        return string.Join('/', kept);
    }

    /// <summary>SHA-256 必须是 64 位十六进制，长度/字符不对时按清单损坏处理。</summary>
    /// <param name="value">清单里的哈希文本。</param>
    /// <returns>格式合法时返回 <see langword="true"/>。</returns>
    private static bool IsSha256Hex([NotNullWhen(true)] string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>清单文件的 JSON 结构。</summary>
    private sealed class ManifestDto
    {
        public int Version { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public List<FileDto?>? Files { get; set; }
    }

    /// <summary>清单里一条文件记录的 JSON 结构。</summary>
    private sealed class FileDto
    {
        public string? Path { get; set; }

        public long Size { get; set; }

        public string? Sha256 { get; set; }
    }
}
