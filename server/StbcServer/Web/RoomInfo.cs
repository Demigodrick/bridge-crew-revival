using StbcServer.Game;

namespace StbcServer.Web;

/// <summary>
/// A room as the public API shows it, with Bridge Crew's room properties decoded
/// (names from the game's OOLKIMEFIPC accessors: C0 LanguageID, C1 NumPlayers, C2 MaxPlayers,
/// C3 GameMode, C4 cross-play, C5 EmptySeatAvailable, C6 CurrentGamePhase, C7 RoomName, C8 QualityDemerits,
/// C9 UsingTNG). Private (invisible) rooms are never exposed beyond a count.
/// </summary>
public sealed record RoomInfo(
    string Name, string Code, string Mode, string Phase, int Players, int MaxPlayers, bool Open,
    string Language, bool NextGeneration, bool CrossPlay, string[] PlayerNames, string AppVersion, int AgeMinutes)
{
    private static readonly string[] Languages =
    [
        "Unsupported", "Arabic", "Portuguese (BR)", "Chinese (Simplified)", "Chinese (Traditional)", "Czech", "Danish",
        "Dutch", "English (US)", "Finnish", "French", "German", "Hungarian", "Italian", "Japanese", "Korean",
        "Spanish (MX)", "Norwegian", "Polish", "Portuguese", "Russian", "Spanish", "Swedish", "Turkish",
    ];

    public static RoomInfo From(RoomSnapshot r)
    {
        int? Int(string key) => (r.CustomProperties.TryGetValue(key, out var v) ? v : null) switch
        {
            int i => i, byte b => b, short s => s, long l => (int)l, _ => null,
        };

        var code = r.Name.Contains('|') ? r.Name[(r.Name.IndexOf('|') + 1)..] : r.Name;
        string mode = Int("C3") switch
        {
            0 => "Public",
            1 => "Solo",
            2 => "Private",
            3 => "Tutorial",
            null => "Unknown",
            var m => $"Mode {m}",
        };
        string phase = Int("C6") switch
        {
            1 => "Shuttle",
            2 => "Briefing room",
            4 => "Intro",
            null => "Unknown",
            var p => "In mission",
        };
        int lang = Int("C0") ?? 0;
        return new RoomInfo(
            r.Name, code, mode, phase, r.Players, r.MaxPlayers, r.Open,
            lang >= 0 && lang < Languages.Length ? Languages[lang] : $"Language {lang}",
            Int("C9") == 1, Int("C4") is null or 0, r.PlayerNames, r.AppVersion,
            (int)(DateTime.UtcNow - r.Created).TotalMinutes);
    }
}
