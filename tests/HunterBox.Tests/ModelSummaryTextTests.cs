using HunterBox.Application.Models;

namespace HunterBox.Tests;

/// <summary>「备注信息」列的模型摘要投影（纯逻辑）。</summary>
public class ModelSummaryTextTests
{
    [Fact]
    public void Build_NoModels_FallsBackToUserDescription()
    {
        Assert.Equal("用户备注", ModelSummaryText.Build([], "用户备注"));
        Assert.Equal(string.Empty, ModelSummaryText.Build([], null));
    }

    [Fact]
    public void Build_JoinsNameCodeAndMarkers()
    {
        string text = ModelSummaryText.Build(
        [
            new ModelSummaryRow("公会骑士", "046_000", HasPrefab: true),
            new ModelSummaryRow("机械军刀", "03_0017", FromPak: true),
        ]);

        Assert.Equal("公会骑士 (046_000) [pfb]、机械军刀 (03_0017) [pak]", text);
    }

    [Fact]
    public void Build_SameCodeAcrossPaks_MergesFlagsAndKeepsFirstPosition()
    {
        string text = ModelSummaryText.Build(
        [
            new ModelSummaryRow("刺花蜘蛛α/β(男款)", "012_000", FromPak: true),
            new ModelSummaryRow("刺花蜘蛛α/β(男款)", "012_000", HasPrefab: true, FromPak: true),
            new ModelSummaryRow(null, "049_000"),
        ]);

        Assert.Equal("刺花蜘蛛α/β(男款) (012_000) [pfb] [pak]、049_000", text);
    }

    [Fact]
    public void Build_UnknownName_ShowsCodeOnly()
    {
        Assert.Equal("049_000 [pfb]", ModelSummaryText.Build(
            [new ModelSummaryRow(null, "049_000", HasPrefab: true)]));
    }

    [Fact]
    public void Build_SkipsBlankCodesAndDeduplicatesIgnoringCase()
    {
        string text = ModelSummaryText.Build(
        [
            new ModelSummaryRow("名字", "012_000"),
            new ModelSummaryRow("名字", "012_000".ToLowerInvariant(), FromPak: true),
            new ModelSummaryRow("空", " "),
        ]);

        Assert.Equal("名字 (012_000) [pak]", text);
    }

    [Fact]
    public void Build_ComposesWithModelDetectorForLooseFiles()
    {
        // 应用层最小集成：检测 → 投影（名称表解析在 Infrastructure/App 层，这里用固定名字替代）。
        (string Path, string? PakFile)[] entries =
        [
            ("natives/stm/art/model/character/ch02/012/003/5/ch02_012_0035.mesh.1", null),
            ("natives/stm/gamedesign/equip/_prefab/armor/male/012/003/body/ch02_012_0032.pfb.18", null),
        ];
        IReadOnlyList<DetectedModel> detected = ModelDetector.Detect(entries);
        List<ModelSummaryRow> rows = [.. detected.Select(model =>
            new ModelSummaryRow("刺花蜘蛛", model.RewriteId, model.HasPrefab, model.PakFile is not null))];

        Assert.Equal("刺花蜘蛛 (012_003) [pfb]", ModelSummaryText.Build(rows, "描述"));
    }
}
