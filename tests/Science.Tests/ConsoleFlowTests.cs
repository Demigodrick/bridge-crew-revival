using System.Collections.Generic;
using StbcRevival.Science;
using Xunit;

public class ConsoleFlowTests
{
    sealed class Recorder : IScienceLink
    {
        public readonly List<string> Sent = new List<string>();
        public void Claim() { Sent.Add("claim"); }
        public void Release() { Sent.Add("release"); }
        public void Heartbeat() { Sent.Add("beat"); }
        public void Report(string id, float p) { Sent.Add("report " + id + " " + p.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)); }
        public void Complete(string id) { Sent.Add("complete " + id); }
        public int Count(string prefix) { int n = 0; foreach (var s in Sent) if (s.StartsWith(prefix)) n++; return n; }
    }

    const int Me = 2, Other = 5;

    static ConsoleFlow Started(Recorder r, float progress = 0f, string id = "42")
    {
        var f = new ConsoleFlow(r);
        Assert.True(f.Start(id, progress, false, 0f));
        return f;
    }

    // Both games set to their answers at time t.
    static void Solve(ConsoleFlow f, float t)
    {
        foreach (var g in new[] { f.GameA, f.GameB })
        {
            g.Tick(t);
            for (int i = 0; i < g.ControlCount; i++) g.SetControl(i, g.Target(i));
            // A sweep ring nudges its neighbour: set them again until they all sit.
            for (int k = 0; k < 20; k++) for (int i = 0; i < g.ControlCount; i++) g.SetControl(i, g.Target(i));
        }
    }

    [Fact]
    public void StartsOnTargets_AndATapClaimsAndOpensScan()
    {
        var r = new Recorder();
        var f = new ConsoleFlow(r);
        Assert.Equal(ScreenMode.Targets, f.Mode);
        Assert.True(f.Start("42", 0f, false, 0f));
        Assert.Equal(ScreenMode.Scan, f.Mode);
        Assert.Equal("42", f.TargetId);
        Assert.Equal(new[] { "claim" }, r.Sent);
        Assert.NotEqual(f.GameA.Kind, f.GameB.Kind);
    }

    [Fact]
    public void Start_UsesTheTargetsTwoGames_SameEveryTime()
    {
        var a = ConsoleFlow.GamesFor("7");
        var b = ConsoleFlow.GamesFor("7");
        var kinds = MiniGamePick.For(MiniGamePick.SeedOf("7"));
        Assert.Equal(kinds[0], a[0].Kind);
        Assert.Equal(kinds[1], a[1].Kind);
        for (int i = 0; i < 3; i++) Assert.Equal(a[0].Target(i), b[0].Target(i));
    }

    [Fact]
    public void Start_RefusesADoneTarget_AndWhileScanning()
    {
        var r = new Recorder();
        var f = new ConsoleFlow(r);
        Assert.False(f.Start("42", 100f, true, 0f));
        Assert.True(f.Start("42", 0f, false, 0f));
        Assert.False(f.Start("43", 0f, false, 0f));
        Assert.Equal("42", f.TargetId);
    }

    [Fact]
    public void Start_ResumesFromTheBoard()
    {
        var f = Started(new Recorder(), 37.5f);
        Assert.Equal(37.5f, f.Progress.Value);
    }

    [Fact]
    public void Scan_ReportsFourTimesASecondWhileLocked_AndBeatsOnce()
    {
        var r = new Recorder();
        var f = Started(r, 10f);
        r.Sent.Clear();
        float t = 0f;
        for (int i = 0; i < 105; i++) // just over 1 s at 100 Hz
        {
            t += 0.01f;
            Solve(f, t);
            f.Tick(t, 0.01f, Me, Me, TargetState.InRange, false);
        }
        Assert.True(f.Progress.Locked);
        Assert.InRange(r.Count("report"), 3, 5);
        Assert.Equal(1, r.Count("beat"));
        Assert.Equal(0, r.Count("claim"));
    }

    [Fact]
    public void Scan_NoReportsWithoutALock()
    {
        var r = new Recorder();
        var f = Started(r);
        r.Sent.Clear();
        for (float t = 0.1f; t < 2f; t += 0.1f)
        {
            foreach (var g in new[] { f.GameA, f.GameB }) for (int i = 0; i < 3; i++) g.SetControl(i, g.Target(i) > 0.5f ? 0f : 1f);
            f.Tick(t, 0.1f, Me, Me, TargetState.InRange, false);
        }
        Assert.False(f.Progress.Locked);
        Assert.Equal(0, r.Count("report"));
        Assert.Equal(0f, f.Progress.Value);
    }

    [Fact]
    public void Scan_ClaimsAgainWhileTheBoardDoesNotShowUs_OnceASecond()
    {
        var r = new Recorder();
        var f = Started(r);
        r.Sent.Clear();
        for (float t = 0.1f; t <= 3.05f; t += 0.1f) f.Tick(t, 0.1f, 0, Me, TargetState.InRange, false);
        Assert.InRange(r.Count("claim"), 2, 3);
        Assert.Equal(ScreenMode.Scan, f.Mode);
        r.Sent.Clear();
        for (float t = 3.1f; t <= 6f; t += 0.1f) f.Tick(t, 0.1f, Me, Me, TargetState.InRange, false);
        Assert.Equal(0, r.Count("claim"));
    }

    [Fact]
    public void Scan_At100_ReportsCompletesReleasesAndGoesBack()
    {
        var r = new Recorder();
        var f = Started(r, 99.9f);
        r.Sent.Clear();
        float t = 0f;
        for (int i = 0; i < 100 && f.Mode == ScreenMode.Scan; i++)
        {
            t += 0.1f;
            Solve(f, t);
            f.Tick(t, 0.1f, Me, Me, TargetState.InRange, false);
        }
        Assert.Equal(ScreenMode.Targets, f.Mode);
        Assert.Null(f.TargetId);
        int complete = r.Sent.IndexOf("complete 42");
        Assert.True(complete > 0);
        Assert.Equal("report 42 100", r.Sent[complete - 1]);
        Assert.Equal("release", r.Sent[complete + 1]);
    }

    [Fact]
    public void OutOfRange_PausesReportsOnceAndKeepsBeating_ThenResumes()
    {
        var r = new Recorder();
        var f = Started(r, 20f);
        Solve(f, 0.1f);
        f.Tick(0.1f, 0.1f, Me, Me, TargetState.InRange, false);
        float v = f.Progress.Value;
        Assert.True(v > 20f);
        r.Sent.Clear();
        for (float t = 0.2f; t < 3f; t += 0.1f)
        {
            Solve(f, t);
            f.Tick(t, 0.1f, Me, Me, TargetState.OutOfRange, false);
        }
        Assert.Equal(ScreenMode.OutOfRange, f.Mode);
        Assert.Equal(v, f.Progress.Value);
        Assert.Equal(1, r.Count("report"));
        Assert.True(r.Count("beat") >= 2);
        var a = f.GameA;
        Solve(f, 3f);
        f.Tick(3f, 0.1f, Me, Me, TargetState.InRange, false);
        Assert.Equal(ScreenMode.Scan, f.Mode);
        Assert.Same(a, f.GameA);
        Assert.True(f.Progress.Value > v);
    }

    [Fact]
    public void Busy_WhenSomeoneElseHoldsIt_DropsTheScan_AndFreesAfter()
    {
        var r = new Recorder();
        var f = Started(r);
        f.Tick(0.1f, 0.1f, Other, Me, TargetState.InRange, false);
        Assert.Equal(ScreenMode.Busy, f.Mode);
        Assert.Equal(Other, f.BusyActor);
        Assert.Null(f.TargetId);
        Assert.False(f.Start("42", 0f, false, 0.2f));
        f.Tick(0.3f, 0.1f, 0, Me, TargetState.Missing, false);
        Assert.Equal(ScreenMode.Targets, f.Mode);
        Assert.Equal(0, f.BusyActor);
        Assert.True(f.Start("42", 0f, false, 0.4f));
    }

    [Fact]
    public void Busy_FromTheList_Too()
    {
        var f = new ConsoleFlow(new Recorder());
        f.Tick(0f, 0.1f, Other, Me, TargetState.Missing, false);
        Assert.Equal(ScreenMode.Busy, f.Mode);
    }

    [Fact]
    public void ScanEnds_WhenTheBoardSaysDone_OrTheTargetIsGoneAWhile()
    {
        var r = new Recorder();
        var f = Started(r);
        f.Tick(0.1f, 0.1f, Me, Me, TargetState.InRange, true);
        Assert.Equal(ScreenMode.Targets, f.Mode);
        Assert.Contains("release", r.Sent);

        f = Started(r);
        f.Tick(1f, 0.1f, Me, Me, TargetState.Missing, false);
        f.Tick(2f, 0.1f, Me, Me, TargetState.Missing, false);
        Assert.Equal(ScreenMode.OutOfRange, f.Mode); // a moment off the list: paused
        f.Tick(3f, 0.1f, Me, Me, TargetState.InRange, false);
        f.Tick(4f, 0.1f, Me, Me, TargetState.Missing, false);
        f.Tick(6.9f, 0.1f, Me, Me, TargetState.Missing, false);
        Assert.Equal(ScreenMode.OutOfRange, f.Mode); // the grace starts again
        f.Tick(7.1f, 0.1f, Me, Me, TargetState.Missing, false);
        Assert.Equal(ScreenMode.Targets, f.Mode);
    }

    [Fact]
    public void Leave_ReportsAndReleases()
    {
        var r = new Recorder();
        var f = Started(r, 12f);
        r.Sent.Clear();
        f.Leave();
        Assert.Equal(new[] { "report 42 12", "release" }, r.Sent);
        Assert.Equal(ScreenMode.Targets, f.Mode);
        r.Sent.Clear();
        f.Leave();
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void InUse_OnlyWhileHoldingAndShownAsClaimant()
    {
        var f = new ConsoleFlow(new Recorder());
        Assert.False(f.InUse(Me, Me));
        f.Start("42", 0f, false, 0f);
        Assert.True(f.InUse(Me, Me));
        Assert.False(f.InUse(0, Me));
    }

    [Fact]
    public void Upgrades_ListsWhatTheCrewHas()
    {
        var l = new Ledger();
        Assert.Equal("Upgrades: none", ConsoleText.Upgrades(l, s => s));
        l.AddScan(UpgradeArea.Weapons);
        l.AddScan(UpgradeArea.Weapons);
        l.AddScan(UpgradeArea.Shields);
        l.AddFactionScan(2);
        l.AddFactionScan(7);
        l.AddFactionScan(8);
        l.AddFactionScan(8);
        Assert.Equal("Upgrades: Weapons +10%  Shields +5%  vs Klingon +5%  vs Pirate +10%", ConsoleText.Upgrades(l, s => s));
    }

    [Fact]
    public void Km_OneDecimalUnderTen()
    {
        Assert.Equal("4.2", ConsoleText.Km(4200, false));
        Assert.Equal("4,2", ConsoleText.Km(4200, true));
        Assert.Equal("12", ConsoleText.Km(12340, false));
        Assert.Equal("20", ConsoleText.Km(ScienceTuning.RangeMetres, false));
    }
}
