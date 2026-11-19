using StbcRevival;

namespace Update.Tests;

public class SteamVrSettingsTests
{
    private const string Ours = "file:///S:/steamapps/common/Star%20Trek%20Bridge%20Crew/BepInEx/vr/quest-binding.json";

    private static string Settings(string app) => "{ \"steamvr\": { \"enableHomeApp\": false }, " + app + " }";

    [Fact]
    public void TheInstallersLayoutIsSeen() =>
        Assert.True(SteamVrSettings.UsesQuestLayout(Settings(
            "\"steam.app.527100\": { \"oculus_touch_CurrentURL_legacy\": \"" + Ours + "\", \"oculus_touch_AutosaveURL_legacy\": \"" + Ours + "\" }")));

    [Fact]
    public void AnotherTouchKeyNameCounts() =>
        Assert.True(SteamVrSettings.UsesQuestLayout(Settings("\"steam.app.527100\": { \"oculus_touch_quest3_CurrentURL_legacy\": \"" + Ours + "\" }")));

    [Fact]
    public void BackslashesCount() =>
        Assert.True(SteamVrSettings.UsesQuestLayout(Settings("\"steam.app.527100\": { \"oculus_touch_CurrentURL_legacy\": \"S:\\\\games\\\\BepInEx\\\\vr\\\\quest-binding.json\" }")));

    [Fact]
    public void SteamVrsOwnLayoutIsNot() =>
        Assert.False(SteamVrSettings.UsesQuestLayout(Settings("\"steam.app.527100\": { \"oculus_touch_CurrentURL_legacy\": \"{527100}/input/legacy_binding_oculus_touch.json\" }")));

    [Fact]
    public void AnotherGamesLayoutIsNot() =>
        Assert.False(SteamVrSettings.UsesQuestLayout(Settings("\"steam.app.1\": { \"oculus_touch_CurrentURL_legacy\": \"" + Ours + "\" }")));

    [Fact]
    public void NoGameSectionIsNot() => Assert.False(SteamVrSettings.UsesQuestLayout(Settings("\"x\": 1")));
}
