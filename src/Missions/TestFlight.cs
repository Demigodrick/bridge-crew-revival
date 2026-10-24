using System.Collections.Generic;
using MissionSequence = Mission.Scripting.MNFBGALLEAJ;  // base class of mission sequences
using MissionObjective = Mission.Scripting.MJHDDKANLEC; // base class of a sequence's objectives
using Step = Mission.Scripting.Framework.Tasks.KAPOBAJDOEJ; // a scripting task, yielded from objective coroutines
using StartSystem = BHNEPPPPMKH;                         // starting-system choice

namespace StbcRevival.Missions
{
    /// <summary>
    /// Test Flight (Revival Missions): no enemies, no objectives, no end. For looking around a sector map:
    /// fly, scan, warp between its systems. Pairs with [Debug] MapOverride to visit the test and demo sectors;
    /// without it you get the Trench. Leave through the pause menu.
    /// </summary>
    public sealed class TestFlightSequence : MissionSequence
    {
        public override void CreateSequence()
        {
            var flight = Sequence.MakeNode<TestFlightObjective>();
            Sequence.OnFail(flight).MissionFails();
            Sequence.OnSucceed(flight).MissionSucceeds();
            Sequence.StartObjective(flight);
        }

        public override void StartingSystemSelection(StartSystem selection)
        {
            selection.ForceRandomSystemSelection = true;
            selection.Choice = StartSystem.LPCLBKKEBMK.Random;
        }
    }

    public sealed class TestFlightObjective : MissionObjective
    {
        internal const string Title = "TEST FLIGHT";
        private const string Directive = "Test flight: no hostiles. Explore the sector";

        public override void StartScript()
        {
            Objective.AddStep("flight", Task.MFLJBKCDBMC.WithSubtaskCoroutine(Flight));
        }

        private IEnumerator<Step> Flight()
        {
            foreach (var step in MissionAudio.Start(Task)) yield return step;
            yield return Task.NECMCDDOFJB.WithDescriptionString(Directive);
            yield return Task.HHNFJKHGEPH.WithMissionString(Title);
            yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Explore");
            var map = Loader.EJLNDBGLIFL.GetLoadedMap();
            Plugin.Log.LogInfo($"Test flight: {(map != null ? map.name : "no map")}, {(map != null ? map.Systems.Count : 0)} system(s)");
            for (;;) yield return Task.KNMMMJOOBHC.WithTime(60f); // nothing happens, on purpose
        }
    }
}
