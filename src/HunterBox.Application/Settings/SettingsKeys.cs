namespace HunterBox.Application.Settings;

/// <summary>INI 配置的节名与键名（唯一来源，避免 UI 与适配器各写一份字面量）。</summary>
public static class SettingsKeys
{
    /// <summary>通用设置节名。</summary>
    public const string Section = "settings";

    /// <summary>上次选择的游戏 ID。</summary>
    public const string Game = "game";

    /// <summary>游戏安装目录（按游戏 ID 作为节名存放）。</summary>
    public const string GamePath = "gamepath";

    /// <summary>存档目录（按游戏 ID 作为节名存放）。</summary>
    public const string SavePath = "savepath";

    /// <summary>启用前是否检查 pak 内部文件。</summary>
    public const string CheckPakFiles = "check_pak_files";

    /// <summary>旧版本的 pak 检查键名（仅迁移读取）。</summary>
    public const string LegacyCheckPakInner = "check_pak_inner";

    /// <summary>启用/清理前是否检查游戏正在运行。</summary>
    public const string CheckGameRunning = "check_game_running";

    /// <summary>是否自动修正 pak 编号。</summary>
    public const string FixPakNumber = "enable_fix_pak_num";

    /// <summary>界面主题偏好（system/light/dark）。</summary>
    public const string Theme = "theme";

    /// <summary>是否已提示/完成旧数据导入。</summary>
    public const string LegacyImported = "legacy_imported";
}

/// <summary>配置项的默认值（读取方不得再写各自字面量）。</summary>
public static class SettingsDefaults
{
    /// <summary>默认不检查 pak 内部文件。</summary>
    public const bool CheckPakFiles = false;

    /// <summary>默认检查游戏是否正在运行。</summary>
    public const bool CheckGameRunning = true;

    /// <summary>默认自动修正 pak 编号。</summary>
    public const bool FixPakNumber = true;

    /// <summary>默认跟随系统主题。</summary>
    public const string Theme = "system";

    /// <summary>默认尚未完成旧数据导入提示。</summary>
    public const bool LegacyImported = false;
}
