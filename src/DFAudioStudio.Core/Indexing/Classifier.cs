using System.Text.RegularExpressions;
using DFAudioStudio.Core.Models;

namespace DFAudioStudio.Core.Indexing;

/// <summary>
/// 依据文件名/路径规则给音频分区与打标签。
/// 规则来自对 DeltaForce WwiseAudio 导出目录的实际统计（Voice_302_*、Music_ScndAnniversary_e6_*、AZ5_Cutscene_* 等）。
/// </summary>
public static class Classifier
{
    private static readonly Regex RxOperator = new(@"(?:^|_)(?:Voice|Char|C|UI_C|Lobby_Character_C|Skin)(\d{3})(?:_|$)", RegexOptions.IgnoreCase);
    private static readonly Regex RxAnyOperator = new(@"\b(?:Voice_)?([1-6]\d{2})_", RegexOptions.IgnoreCase);
    private static readonly Regex RxSuffixCode = new(@"(?:^|_)([A-Z0-9]{2,8}\d[A-Z0-9]*|[A-Z]{3,8}_[a-z0-9]{2})(?:_|$)", RegexOptions.Compiled);

    /// <summary>关键字 → 事件标签。顺序即优先级。</summary>
    private static readonly (string Key, string Tag)[] EventRules =
    {
        ("ScndAnniversary", "二周年"),
        ("2rd_Anniversary", "二周年"),
        ("Anniversary", "周年庆"),
        ("Musicfest", "音乐节"),
        ("musicfest", "音乐节"),
        ("NewYear", "新年"),
        ("Xmas", "圣诞"),
        ("Christmas", "圣诞"),
        ("MusicFest", "音乐节"),
        ("Birthday", "生日"),
        ("Ma1_", "Ma1"), ("Ma2_", "Ma2"), ("Ma3", "Ma3"), ("Ma4", "Ma4"), ("Ma5", "Ma5"),
        ("EasterEgg", "彩蛋"),
        ("Halloween", "万圣节"),
        ("Summer", "夏日"),
        ("Arknights", "明日方舟联动"),
        ("Pandora", "潘多拉"),
    };

    /// <summary>地图标签（代号，配置文本与音频命名里都在用）。</summary>
    private static readonly (string Key, string Tag)[] MapRules =
    {
        ("AZ3", "AZ3核电园区"),
        ("AZ5", "AZ5(NPP剧情)"),
        ("NPP", "核电站NPP"),
        ("SpaceTransferStation", "航天中转站"),
        ("SpaceCenter", "航天基地"),
        ("PowerStation", "发电站"),
        ("SamirManor", "萨米尔庄园"),
        ("NavalBattle", "海战"),
        ("Monument", "纪念碑"),
        ("Collapse", "崩塌"),
        ("Derail", "脱轨"),
        ("Cracked", "裂隙"),
        ("Fault", "断层"),
        ("Crest", "峰顶"),
        ("Shafted", "竖井"),
        ("Retrowa", "Retrowa"),
        ("Prison", "潮汐监狱"),
        ("Brakkesh", "巴克什"),
        ("Forest", "森林"),
        ("Dam", "零号大坝"),
        ("Island", "海岛"),
        ("Harbor", "港口"),
    };

    private static readonly (string Key, string Tag)[] SideRules =
    {
        ("InHavvk", "哈夫克·局内"),
        ("OutHavvk", "哈夫克·局外"),
        ("Haavk", "哈夫克"),
        ("GTI", "GTI"),
        ("Ahsarahn", "阿萨拉"),
        ("Ahsarah", "阿萨拉"),
    };

    /// <summary>
    /// 场景/用途分区：把文件按"它在游戏里什么时候播"归类，便于按内容而不是按文件名浏览。
    /// 顺序即优先级（先匹配到的为准）。
    /// </summary>
    private static readonly (string Key, string Tag)[] SceneRules =
    {
        ("Entry_Select", "选人入场"),
        ("GameStart", "对局开局"),
        ("LoadingVideo", "载入CG"),
        ("Loading", "载入界面"),
        ("_Intro", "开场"),
        ("Settlement", "结算"),
        ("MVP", "MVP展示"),
        ("Lobby", "大厅"),
        ("QuickMsg", "快捷消息"),
        ("Marker", "标记报点"),
        ("Mark_", "标记报点"),
        ("Combat_Passive", "战斗提示"),
        ("Alert", "战斗提示"),
        ("Inform", "战况通报"),
        ("Skill", "技能语音"),
        ("Callin", "呼叫支援"),
        ("Evac", "撤离"),
        ("Death", "阵亡"),
        ("Cutscene", "剧情过场"),
        ("_CG_", "剧情过场"),
        ("CS_", "剧情过场"),
        ("Cinematics", "剧情过场"),
        ("Radio", "电台"),
        ("Roulette", "转盘活动"),
        ("SeasonQuest", "赛季任务"),
        ("LiveOps", "活动"),
        ("Activity", "活动"),
        ("Inspect", "检视"),
        ("Fishing", "钓鱼"),
        ("Emote", "表情动作"),
        ("Gesture", "表情动作"),
        ("Breath", "呼吸/喘息"),
        ("Footstep", "脚步"),
        ("C4", "爆破"),
        ("Bomb", "爆破"),
        ("Defuse", "爆破"),
        ("RPS", "小游戏"),
        ("Unarmed", "空手"),
        ("InGame", "局内通用"),
        ("MP_", "多人大厅"),
        ("Amb_", "环境氛围"),
        ("AMB_", "环境氛围"),
        ("Object_", "物件交互"),
        ("Prop", "物件交互"),
        ("Wpn_", "武器"),
        ("Weapons_", "武器"),
        ("Vehicle", "载具"),
        ("UI_", "界面"),
        ("HUD", "界面"),
        ("Music", "音乐"),
        ("Vocie", "角色台词"),
        ("Voice", "角色台词"),
    };

    private static readonly string[] SkinKeys =
    {
        "Yanzu", "SkinA", "SkinB", "Outers", "Cowboy", "Outrage", "Researcher",
        "BeyondTheStar", "MadeOfFire", "PosterGirl", "SpotlightHunter", "TheFear",
        "GotYou", "Dawn", "AnaisReborn", "Claire", "Nox", "Zoya", "Yukine", "Sinea", "Smee"
    };

    /// <summary>语音类前缀（含导出里真实存在的拼写错误 Vocie_）。</summary>
    private static readonly string[] VoicePrefixes =
    {
        "Voice", "Vocie", "Lobby", "NPC_", "Emote_", "Gesture_", "Char_Voice", "Dialogue", "Subtitle"
    };

    /// <summary>音乐类前缀（Musicfest_ 是音乐节场景音效，不算音乐）。</summary>
    private static readonly string[] MusicPrefixes = { "Music_", "Music", "Radio_", "DF_", "BGM", "Bgm", "Song_" };

    /// <summary>音效类前缀（按实跑统计补齐：脚步/破坏/子弹/移动/角色技能等）。</summary>
    private static readonly string[] SfxPrefixes =
    {
        "Footsteps", "Footstep", "Destruction", "Destructible", "Bullet", "ShellBounce", "Shell", "Exp", "Exp_",
        "Hit_", "Impact", "Impacts", "Classes", "Mvmnt", "Movement", "SOL_", "Container", "Door", "Machine", "Crocodile",
        "walk", "Walk", "Object_", "Foley_", "Prop", "Wpn_", "Weapons_", "Vehicle_", "Vehicles_", "Char_", "SFX_", "Sfx_",
        "MP_", "Item_", "Ammo_", "Explosion", "Gear", "Player_", "Reload", "Switch_", "Glass", "Metal", "Wood", "Water_",
        "Amb_SFX", "Whoosh", "UI_SFX", "CS_", "Ingame_", "Emote_SFX", "Musicfest_", "Musicfest",
        "Melee", "Ability", "Football", "Shark", "Harmonica", "Operation", "Intro", "DBWMissile",
        "Propeller", "Rocket", "Turret", "Tank", "Helicopter", "Drone", "Parachute", "Zipline"
    };

    private static readonly Regex RxSkillSfx = new(@"^C\d{3}_", RegexOptions.Compiled);

    public static (AudioCategory Category, string SubCategory) CategoryOf(string fileName, string relPath)
    {
        string n = fileName;
        string lower = n.ToLowerInvariant();
        string pathLower = relPath.ToLowerInvariant();

        bool StartsAny(string[] prefixes)
        {
            foreach (var p in prefixes)
                if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        bool isMusic = StartsAny(MusicPrefixes)
                       || lower.Contains("_music")
                       || lower.Contains("music_")
                       || lower.Contains("bgm")
                       || lower.Contains("_song_")
                       || pathLower.Contains(@"\music\");

        bool isVoice = StartsAny(VoicePrefixes)
                       || n.Contains("_Voice_", StringComparison.OrdinalIgnoreCase)
                       || pathLower.Contains(@"\voice\");

        AudioCategory cat;
        if (isMusic && !n.StartsWith("Musicfest", StringComparison.OrdinalIgnoreCase)) cat = AudioCategory.Music;
        else if (isVoice) cat = AudioCategory.Voice;
        else if (n.StartsWith("Amb_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("AMB_", StringComparison.OrdinalIgnoreCase)) cat = AudioCategory.Ambience;
        else if (n.StartsWith("UI_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Ui_", StringComparison.OrdinalIgnoreCase)
                 || n.StartsWith("HUD_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Ingame_Inv", StringComparison.OrdinalIgnoreCase)) cat = AudioCategory.Ui;
        else if (StartsAny(SfxPrefixes) || RxSkillSfx.IsMatch(n)) cat = AudioCategory.Sfx;
        else cat = AudioCategory.Other;

        return (cat, SubCategoryOf(fileName));
    }

    /// <summary>取前两段作为子分类，例如 Voice_302 / Music_ScndAnniversary / AZ5_Cutscene。</summary>
    public static string SubCategoryOf(string fileName)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";
        if (parts.Length == 1) return parts[0];
        return parts[0] + "_" + parts[1];
    }

    public static string OperatorOf(string fileName)
    {
        var m = RxOperator.Match(fileName);
        if (m.Success) return m.Groups[1].Value;
        m = RxAnyOperator.Match(fileName);
        if (m.Success) return m.Groups[1].Value;
        return "";
    }

    public static string EventOf(string fileName)
    {
        foreach (var (key, tag) in EventRules)
            if (fileName.Contains(key, StringComparison.OrdinalIgnoreCase)) return tag;
        return "";
    }

    public static string MapOf(string fileName, string relPath)
    {
        string hay = fileName + "|" + relPath;
        foreach (var (key, tag) in MapRules)
            if (hay.Contains(key, StringComparison.OrdinalIgnoreCase)) return tag;
        return "";
    }

    public static string SideOf(string fileName, string relPath)
    {
        string hay = fileName + "|" + relPath;
        foreach (var (key, tag) in SideRules)
            if (hay.Contains(key, StringComparison.OrdinalIgnoreCase)) return tag;
        return "";
    }

    /// <summary>场景/用途标签（按内容归类，供"按场景"分区使用）。</summary>
    public static string SceneOf(string fileName, string relPath)
    {
        string hay = fileName + "|" + relPath;
        foreach (var (key, tag) in SceneRules)
            if (hay.Contains(key, StringComparison.OrdinalIgnoreCase)) return tag;
        return "";
    }

    public static string SkinOf(string fileName)
    {        foreach (var k in SkinKeys)
            if (fileName.Contains(k, StringComparison.OrdinalIgnoreCase)) return k;
        // 形如 _7DL9D_gz / _9CYV3_vv / _AN252G_xl 的联动皮肤后缀
        var m = RxSuffixCode.Match(fileName);
        if (m.Success)
        {
            string code = m.Groups[1].Value;
            if (!code.Equals("Low", StringComparison.OrdinalIgnoreCase) && !code.Equals("High", StringComparison.OrdinalIgnoreCase))
                return code;
        }
        return "";
    }

    /// <summary>一次性算全部分类与标签。</summary>
    public static AudioItem Apply(AudioItem item)
    {
        var (cat, sub) = CategoryOf(item.FileName, item.RelPath);
        item.Category = cat;
        item.SubCategory = sub;
        item.Operator = OperatorOf(item.FileName);
        item.EventTag = EventOf(item.FileName);
        item.MapTag = MapOf(item.FileName, item.RelPath);
        item.SideTag = SideOf(item.FileName, item.RelPath);
        item.SkinTag = SkinOf(item.FileName);
        item.SceneTag = SceneOf(item.FileName, item.RelPath);
        return item;
    }
}
