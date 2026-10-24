using System.Collections.Generic;
using Step = Mission.Scripting.Framework.Tasks.KAPOBAJDOEJ;   // a scripting task, yielded from objective coroutines
using Tasks = Mission.Scripting.Framework.Tasks.FNICGFPIEOL; // the task builders (an objective's Task)

namespace StbcRevival.Missions
{
    /// <summary>
    /// What every one of the game's missions does to the sound first. The bridge starts in the captain's log
    /// mix (VOManager.Start sets Captains_Log_Sqeuence = CLOG_On: consoles, impacts and crew voice turned
    /// down under the log); missions end it with Stop_CLOG_Sequence_Off, as even the developers' debug
    /// exploration mission does. Then the dynamic music is switched on, which the music moods ("Explore",
    /// "Tension") play through.
    /// </summary>
    internal static class MissionAudio
    {
        internal static IEnumerable<Step> Start(Tasks task)
        {
            yield return task.ABHANLLBILL.WithEventID("Stop_CLOG_Sequence_Off").ContinueImmediately();
            yield return task.CHBEPABEAEL.WithPlayEvent("Play_Switch_Music_Dynamic_PT02").ContinueImmediately();
        }
    }
}
