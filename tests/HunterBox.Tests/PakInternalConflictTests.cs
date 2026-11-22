using HunterBox.Application.Activation;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>
/// 「检查PAK内部文件」实现：启用前展开 pak 条目表（路径哈希）与已启用 MOD 比对，重叠时给出提示但不阻止启用。
/// </summary>
public class PakInternalConflictTests
{

    private const ulong HashA = 0x1_0000_0002;
    private const ulong HashB = 0x2_0000_0003;

    private sealed class FakePakReader : IPakArchiveReader
    {
        private readonly Dictionary<string, ulong[]> _entriesByName;

        public FakePakReader(Dictionary<string, ulong[]> entriesByName) => _entriesByName = entriesByName;

        public List<string> ReadPaths { get; } = [];

        public PakArchiveInfo Read(string pakPath)
        {
            ReadPaths.Add(pakPath);
            string name = Path.GetFileName(pakPath);
            if (!_entriesByName.TryGetValue(name, out ulong[]? hashes))
            {
                throw new InvalidDataException($"无法解析 {name}");
            }

            List<PakEntryInfo> entries =
            [
                .. hashes.Select(hash => new PakEntryInfo(
                    (uint)(hash & 0xFFFF_FFFF),
                    (uint)(hash >> 32),
                    0,
                    0,
                    0,
                    0,
                    0)),
            ];
            return new PakArchiveInfo(4, 0, 0, 0, entries);
        }
    }

    private sealed record Fixture(BoxPaths Paths, string GameRoot, ModRepository Store);

    private static Fixture CreateBox(TempDir temp)
    {
        (BoxPaths paths, string gameRoot, _, ModRepository store) = TestBox.Create(temp);
        return new Fixture(paths, gameRoot, store);
    }

    private static ModRecord AddPakMod(Fixture fixture, int id, string pakName, string? gameId = null)
    {
        ModRecord mod = new()
        {
            Id = id,
            Index = id,
            GameId = gameId ?? TestGames.Wilds.Id,
            Kind = ModKind.PakFiles,
            RelativeFiles = [pakName],
        };

        string target = Path.Combine(fixture.Paths.FilesDirectory(id), pakName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, $"pak-content-{id}");
        fixture.Store.Add(mod);
        return mod;
    }

    private static ModActivationService CreateService(
        Fixture fixture,
        bool checkPakInternalFiles,
        IPakArchiveReader? pakReader) => new(
            fixture.Paths,
            fixture.Store,
            fixture.GameRoot,
            TestGames.Wilds,
            new FileContentFingerprint(),
            new ActivationOptions
            {
                FileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
                CheckPakInternalFiles = checkPakInternalFiles,
                PakReader = pakReader,
            });

    [Fact]
    public void Enable_WithPakCheck_ReportsInternalFileOverlap()
    {
        using TempDir temp = TempDir.Create();
        Fixture fixture = CreateBox(temp);
        ModRecord first = AddPakMod(fixture, 1, "first.pak");
        ModRecord second = AddPakMod(fixture, 2, "second.pak");
        FakePakReader reader = new(new Dictionary<string, ulong[]>
        {
            ["first.pak"] = [HashA, HashB],
            ["second.pak"] = [HashB],
        });
        ModActivationService service = CreateService(fixture, checkPakInternalFiles: true, reader);

        service.Enable(first, PakNamingMode.PatchNumbered);
        ActivationReport report = service.Enable(second, PakNamingMode.PatchNumbered);

        Assert.True(second.Enabled);
        Assert.Contains(report.Warnings, warning => warning.Contains("检查PAK内部文件"));
        Assert.Contains(report.Warnings, warning => warning.Contains("1 个 pak 内部文件重叠"));
        Assert.Contains(report.Warnings, warning => warning.Contains("MOD #2"));
        Assert.Equal(2, reader.ReadPaths.Count);
    }

    [Fact]
    public void Enable_WithoutPakCheck_DoesNotReadPaksOrWarn()
    {
        using TempDir temp = TempDir.Create();
        Fixture fixture = CreateBox(temp);
        ModRecord first = AddPakMod(fixture, 1, "first.pak");
        ModRecord second = AddPakMod(fixture, 2, "second.pak");
        FakePakReader reader = new(new Dictionary<string, ulong[]>
        {
            ["first.pak"] = [HashA],
            ["second.pak"] = [HashA],
        });
        ModActivationService service = CreateService(fixture, checkPakInternalFiles: false, reader);

        service.Enable(first, PakNamingMode.PatchNumbered);
        ActivationReport report = service.Enable(second, PakNamingMode.PatchNumbered);

        Assert.True(second.Enabled);
        Assert.Empty(report.Warnings);
        Assert.Empty(reader.ReadPaths);
    }

    [Fact]
    public void Enable_WithPakCheck_NoOverlap_NoWarning()
    {
        using TempDir temp = TempDir.Create();
        Fixture fixture = CreateBox(temp);
        ModRecord first = AddPakMod(fixture, 1, "first.pak");
        ModRecord second = AddPakMod(fixture, 2, "second.pak");
        FakePakReader reader = new(new Dictionary<string, ulong[]>
        {
            ["first.pak"] = [HashA],
            ["second.pak"] = [HashB],
        });
        ModActivationService service = CreateService(fixture, checkPakInternalFiles: true, reader);

        service.Enable(first, PakNamingMode.PatchNumbered);
        ActivationReport report = service.Enable(second, PakNamingMode.PatchNumbered);

        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void Enable_WithPakCheck_UnreadablePak_WarnsButStillEnables()
    {
        using TempDir temp = TempDir.Create();
        Fixture fixture = CreateBox(temp);
        ModRecord first = AddPakMod(fixture, 1, "first.pak");
        ModRecord second = AddPakMod(fixture, 2, "bad.pak");
        FakePakReader reader = new(new Dictionary<string, ulong[]>
        {
            ["first.pak"] = [HashA],
        });
        ModActivationService service = CreateService(fixture, checkPakInternalFiles: true, reader);

        service.Enable(first, PakNamingMode.PatchNumbered);
        ActivationReport report = service.Enable(second, PakNamingMode.PatchNumbered);

        Assert.True(second.Enabled);
        Assert.Contains(report.Warnings, warning => warning.Contains("无法读取") && warning.Contains("bad.pak"));
        Assert.True(File.Exists(Path.Combine(fixture.GameRoot, "pak_mods", "re_chunk_000.pak.sub_000.pak.patch_016.pak")));
    }

    [Fact]
    public void Enable_WithPakCheck_OnlyInspectsSameGameMods()
    {
        using TempDir temp = TempDir.Create();
        Fixture fixture = CreateBox(temp);
        ModRecord first = AddPakMod(fixture, 1, "first.pak");
        ModRecord second = AddPakMod(fixture, 2, "second.pak");
        ModRecord otherGame = AddPakMod(fixture, 3, "other.pak", gameId: "mhw");
        otherGame.Enabled = true;
        FakePakReader reader = new(new Dictionary<string, ulong[]>
        {
            ["first.pak"] = [HashA],
            ["second.pak"] = [HashA],
            ["other.pak"] = [HashA],
        });
        ModActivationService service = CreateService(fixture, checkPakInternalFiles: true, reader);

        service.Enable(first, PakNamingMode.PatchNumbered);
        ActivationReport report = service.Enable(second, PakNamingMode.PatchNumbered);

        // 跨游戏记录（mhw的 MOD #3）不参与当前游戏（mhws）的内部文件比对。
        Assert.Contains(report.Warnings, warning => warning.Contains("MOD #1"));
        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("MOD #3"));
    }
}
