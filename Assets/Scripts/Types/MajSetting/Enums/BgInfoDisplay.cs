namespace MajdataViewX.Types.Enums
{
    // 注意：成员顺序即线格式数值，必须与 Edit 端 BgInfoDisplay 完全一致（DXScore_Dec 在 DXScore 之后），
    // 否则设置项会静默错位（如 DXScore_Dec 被解成 S_Border）。
    public enum BgInfoDisplay
    {
        None,
        Combo,
        Achievement_101,
        Achievement_100,
        Achievement,
        AchievementClassical,
        AchievementClassical_100,
        DXScore,
        DXScore_Dec,
        S_Border,
        SS_Border,
        SSS_Border,
    }
}