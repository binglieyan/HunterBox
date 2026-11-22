using HunterBox.Domain.Paks;

namespace HunterBox.Tests;

/// <summary>
/// D-04：Domain 编号 helper 的策略参数回归——起始号、已占用编号、按内容复用。
/// <see cref="PakPlanner"/> 与 <c>PakMountService</c> 都只通过本 helper 分配编号，
/// 这些用例锁住合并后的公共语义，防止任何一侧再长出第二套规则。
/// </summary>
public class PakNumberingTests
{
    private const string Prefix = TestGames.WildsPatchPrefix;

    private static PakPlacementOptions Placement() => new()
    {
        Mode = PakNamingMode.PatchNumbered,
        PatchPrefix = Prefix,
        PatchNumberFloor = 6,
    };

    private static IReadOnlyList<PakAssignment> Assign(
        PakNumberingOptions options,
        params (int Id, string Content)[] sources)
    {
        Dictionary<string, string> contentByPath = new(StringComparer.Ordinal);
        List<PakSource> list = [];
        foreach ((int id, string content) in sources)
        {
            string path = $"/mods/{id}/{id}.pak";
            contentByPath[path] = content;
            list.Add(new PakSource(id, "MOD " + id, id + ".pak", path));
        }

        return PakNumbering.AssignPatchNumbers(list, options, path => contentByPath[path]);
    }

    private static PakNumberingOptions Options(
        int firstCandidateNumber = 7,
        IReadOnlySet<int>? occupied = null,
        IReadOnlyDictionary<string, string>? existing = null,
        bool reuseExistingContent = false) => new()
        {
            PatchPrefix = Prefix,
            FirstCandidateNumber = firstCandidateNumber,
            OccupiedNumbers = occupied ?? new HashSet<int>(),
            ExistingNamesByContent = existing ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ReuseExistingContent = reuseExistingContent,
        };

    [Fact]
    public void AssignPatchNumbers_NumbersExhausted_ThrowsReadableInvalidOperationException()
    {
        // C-10：编号用到 99999 之后必须给出可读业务异常，而不是 FormatPatchName 的参数异常。
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            Assign(
                Options(firstCandidateNumber: PakNumbering.MaxParsableNumber,
                    occupied: new HashSet<int> { PakNumbering.MaxParsableNumber }),
                (1, "a")));

        Assert.Contains("编号已用尽", error.Message);
    }

    [Fact]
    public void AssignPatchNumbers_WalksSequentiallyFromFirstCandidate()
    {
        IReadOnlyList<PakAssignment> assignments = Assign(Options(), (1, "a"), (2, "b"));

        Assert.Equal([Prefix + "007.pak", Prefix + "008.pak"], assignments.Select(a => a.FinalName));
        Assert.Equal([1, 2], assignments.Select(a => a.Source.ModId));
    }

    [Fact]
    public void AssignPatchNumbers_SkipsOccupiedNumbers()
    {
        IReadOnlyList<PakAssignment> assignments = Assign(
            Options(occupied: new HashSet<int> { 7, 8, 10 }),
            (1, "a"),
            (2, "b"));

        Assert.Equal([Prefix + "009.pak", Prefix + "011.pak"], assignments.Select(a => a.FinalName));
    }

    [Fact]
    public void AssignPatchNumbers_ReusesExistingNameForSameContent()
    {
        Dictionary<string, string> existing = new(StringComparer.Ordinal)
        {
            ["a"] = Prefix + "007.pak",
        };

        IReadOnlyList<PakAssignment> assignments = Assign(
            Options(occupied: new HashSet<int> { 7 }, existing: existing, reuseExistingContent: true),
            (1, "a"),
            (2, "b"));

        // a 沿用已部署的 007；b 是新内容，取最小空闲号 008。
        Assert.Equal([Prefix + "007.pak", Prefix + "008.pak"], assignments.Select(a => a.FinalName));
    }

    [Fact]
    public void AssignPatchNumbers_DoesNotReuseExistingNameWhenDisabled()
    {
        Dictionary<string, string> existing = new(StringComparer.Ordinal)
        {
            ["a"] = Prefix + "007.pak",
        };

        IReadOnlyList<PakAssignment> assignments = Assign(
            Options(occupied: new HashSet<int> { 7 }, existing: existing, reuseExistingContent: false),
            (1, "a"),
            (2, "b"));

        Assert.Equal([Prefix + "008.pak", Prefix + "009.pak"], assignments.Select(a => a.FinalName));
    }

    [Fact]
    public void AssignPatchNumbers_SharedContentGetsOneNumber()
    {
        IReadOnlyList<PakAssignment> assignments = Assign(Options(), (1, "same"), (2, "same"), (3, "other"));

        Assert.Equal([Prefix + "007.pak", Prefix + "007.pak", Prefix + "008.pak"], assignments.Select(a => a.FinalName));
        Assert.Equal([1, 2, 3], assignments.Select(a => a.Source.ModId));
    }

    [Fact]
    public void AssignPatchNumbers_DoesNotMutateCallerState()
    {
        HashSet<int> occupied = [7, 8];
        Dictionary<string, string> existing = new(StringComparer.Ordinal) { ["a"] = Prefix + "007.pak" };
        PakNumberingOptions options = Options(occupied: occupied, existing: existing, reuseExistingContent: true);

        _ = Assign(options, (1, "a"), (2, "b"));

        Assert.Equal([7, 8], occupied.Order());
        Assert.Equal(Prefix + "007.pak", Assert.Single(existing).Value);
    }

    [Fact]
    public void AssignPatchNumbers_NullArgumentsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => PakNumbering.AssignPatchNumbers(null!, Options(), _ => "x"));
        Assert.Throws<ArgumentNullException>(() => PakNumbering.AssignPatchNumbers([], null!, _ => "x"));
        Assert.Throws<ArgumentNullException>(() => PakNumbering.AssignPatchNumbers([], Options(), null!));
    }

    [Fact]
    public void PlacementPlan_StillUsesSharedNumbering()
    {
        // PakPlanner 只是给 helper 填策略参数：max+1 起算、复用关闭。
        FakeFingerprint print = new FakeFingerprint()
            .With("/mods/1/a.pak", "same")
            .With("/mods/2/b.pak", "same");
        PakPlacementOptions options = Placement() with
        {
            ExistingGameRootFileNames = [Prefix + "009.pak"],
        };

        PakPlan plan = PakPlanner.Plan(
            [new PakSource(1, "A", "a.pak", "/mods/1/a.pak"), new PakSource(2, "B", "b.pak", "/mods/2/b.pak")],
            options,
            print);

        Assert.Equal([Prefix + "010.pak", Prefix + "010.pak"], plan.Assignments.Select(a => a.FinalName));
    }

    [Fact]
    public void NextPatchNumber_And_FormatParseRoundTrip()
    {
        Assert.Equal(16, PakNumbering.NextPatchNumber(6, [Prefix + "007.pak", Prefix + "015.pak", "unrelated.pak"], Prefix));
        Assert.Equal(1001, PakNumbering.NextPatchNumber(6, [Prefix + "1000.pak"], Prefix));

        string formatted = PakNumbering.FormatPatchName(Prefix, 1000);
        Assert.Equal(Prefix + "1000.pak", formatted);
        Assert.True(PakNumbering.TryParsePatchNumber(formatted, Prefix, out int parsed));
        Assert.Equal(1000, parsed);

        // N-23：格式化支持到 5 位，解析必须能还原同样的 5 位编号。
        string fiveDigits = PakNumbering.FormatPatchName(Prefix, 10_000);
        Assert.Equal(Prefix + "10000.pak", fiveDigits);
        Assert.True(PakNumbering.TryParsePatchNumber(fiveDigits, Prefix, out int parsedFive));
        Assert.Equal(10_000, parsedFive);

        Assert.False(PakNumbering.TryParsePatchNumber(Prefix + "15.pak", Prefix, out _));
        Assert.False(PakNumbering.TryParsePatchNumber(Prefix + "001.pak.tmp", Prefix, out _));
        Assert.False(PakNumbering.TryParsePatchNumber("other.pak", Prefix, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PakNumbering.FormatPatchName(Prefix, PakNumbering.MaxParsableNumber + 1));
    }
}
