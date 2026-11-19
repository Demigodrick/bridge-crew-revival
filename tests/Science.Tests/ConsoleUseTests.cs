using StbcRevival.Science;
using Xunit;

public class ConsoleUseTests
{
    static UseFrame Frame(bool standing = true, bool vr = false, bool active = true, float distance = 0.5f, bool key = false, bool esc = false) =>
        new UseFrame { Standing = standing, Vr = vr, Active = active, Distance = distance, UseKey = key, Escape = esc };

    [Theory]
    [InlineData(0f, true)]
    [InlineData(1.2f, true)]
    [InlineData(1.21f, false)]
    [InlineData(float.PositiveInfinity, false)]
    [InlineData(float.NaN, false)]
    [InlineData(-1f, false)]
    public void Within_IsUpTo1Point2Metres(float d, bool expected) => Assert.Equal(expected, ConsoleUse.Within(d));

    [Fact]
    public void Seated_NeverInReachNorRegistered()
    {
        var u = new ConsoleUse();
        Assert.Equal(UseChange.None, u.Step(Frame(standing: false, vr: true, distance: 0f)));
        Assert.Equal(UseChange.None, u.Step(Frame(standing: false, distance: 0f, key: true)));
        Assert.False(u.InReach);
        Assert.False(u.Registered);
        Assert.False(u.Engaged);
        Assert.False(u.PassesHandInput);
    }

    [Fact]
    public void ConsoleOff_IsOutOfReach()
    {
        var u = new ConsoleUse();
        Assert.Equal(UseChange.None, u.Step(Frame(vr: true, active: false)));
        Assert.Equal(UseChange.None, u.Step(Frame(active: false, key: true)));
        Assert.False(u.InReach);
        Assert.False(u.Engaged);
    }

    [Fact]
    public void Vr_RegistersInReach_AndPassesTheHands()
    {
        var u = new ConsoleUse();
        Assert.Equal(UseChange.None, u.Step(Frame(vr: true, distance: 3f)));
        Assert.False(u.PassesHandInput);
        Assert.Equal(UseChange.Register, u.Step(Frame(vr: true, distance: 1f)));
        Assert.True(u.PassesHandInput);
        Assert.Equal(UseChange.None, u.Step(Frame(vr: true, distance: 0.3f)));
        Assert.True(u.Registered);
    }

    [Fact]
    public void Vr_LeavingReach_UnregistersAndLeaves()
    {
        var u = new ConsoleUse();
        u.Step(Frame(vr: true));
        Assert.Equal(UseChange.Unregister | UseChange.Leave, u.Step(Frame(vr: true, distance: 1.5f)));
        Assert.False(u.PassesHandInput);
        Assert.Equal(UseChange.None, u.Step(Frame(vr: true, distance: 1.5f)));
    }

    [Fact]
    public void Vr_SittingDown_UnregistersAndLeaves()
    {
        var u = new ConsoleUse();
        u.Step(Frame(vr: true));
        Assert.Equal(UseChange.Unregister | UseChange.Leave, u.Step(Frame(standing: false, vr: true)));
        Assert.False(u.PassesHandInput);
    }

    [Fact]
    public void Vr_TheUseKeyDoesNotEngage()
    {
        var u = new ConsoleUse();
        u.Step(Frame(vr: true));
        Assert.Equal(UseChange.None, u.Step(Frame(vr: true, key: true)));
        Assert.False(u.Engaged);
    }

    [Fact]
    public void Monitor_InReach_NothingUntilTheKey()
    {
        var u = new ConsoleUse();
        Assert.Equal(UseChange.None, u.Step(Frame()));
        Assert.True(u.InReach);
        Assert.False(u.Registered);
        Assert.False(u.PassesHandInput);
        Assert.False(u.Engaged);
    }

    [Fact]
    public void Monitor_KeyOutOfReach_DoesNothing()
    {
        var u = new ConsoleUse();
        Assert.Equal(UseChange.None, u.Step(Frame(distance: 2f, key: true)));
        Assert.False(u.Engaged);
    }

    [Fact]
    public void Monitor_KeyEngages_KeyAgainDisengages()
    {
        var u = new ConsoleUse();
        Assert.Equal(UseChange.Engage, u.Step(Frame(key: true)));
        Assert.True(u.Engaged);
        Assert.False(u.PassesHandInput); // the mouse is the mod's own while engaged, not the hands'
        Assert.Equal(UseChange.None, u.Step(Frame()));
        Assert.Equal(UseChange.Disengage, u.Step(Frame(key: true)));
        Assert.False(u.Engaged);
        Assert.True(u.InReach);
    }

    [Fact]
    public void Monitor_EscapeDisengages_AndIsSwallowed()
    {
        var u = new ConsoleUse();
        u.Step(Frame(key: true));
        Assert.Equal(UseChange.Disengage | UseChange.SwallowEscape, u.Step(Frame(esc: true)));
        Assert.False(u.Engaged);
    }

    [Fact]
    public void Monitor_EscapeNotEngaged_IsLeftToBridgeWalk()
    {
        var u = new ConsoleUse();
        Assert.Equal(UseChange.None, u.Step(Frame(esc: true)));
    }

    [Fact]
    public void Monitor_EscapeAndKeyTogether_DisengageOnceAndSwallow()
    {
        var u = new ConsoleUse();
        u.Step(Frame(key: true));
        Assert.Equal(UseChange.Disengage | UseChange.SwallowEscape, u.Step(Frame(key: true, esc: true)));
        Assert.False(u.Engaged);
    }

    [Fact]
    public void Monitor_SittingDownEngaged_DisengagesAndLeaves()
    {
        var u = new ConsoleUse();
        u.Step(Frame(key: true));
        Assert.Equal(UseChange.Disengage | UseChange.Leave, u.Step(Frame(standing: false)));
        Assert.False(u.Engaged);
    }

    [Fact]
    public void Monitor_ConsoleOffEngaged_DisengagesAndLeaves()
    {
        var u = new ConsoleUse();
        u.Step(Frame(key: true));
        Assert.Equal(UseChange.Disengage | UseChange.Leave, u.Step(Frame(active: false)));
    }

    [Fact]
    public void Monitor_DisengagingInReach_KeepsTheConsole()
    {
        var u = new ConsoleUse();
        u.Step(Frame(key: true));
        Assert.Equal(UseChange.Disengage, u.Step(Frame(key: true)));
        // Walking off afterwards frees it.
        Assert.Equal(UseChange.Leave, u.Step(Frame(distance: 5f)));
    }

    [Fact]
    public void Ease_TakesPoint3SecondsEachWay()
    {
        float b = 0f;
        for (int i = 0; i < 2; i++) b = ConsoleUse.Ease(b, true, 0.1f);
        Assert.True(b > 0.6f && b < 0.7f);
        b = ConsoleUse.Ease(b, true, 0.1f);
        Assert.Equal(1f, b, 0.0001f);
        Assert.Equal(1f, ConsoleUse.Ease(b, true, 0.1f));
        b = ConsoleUse.Ease(b, false, 0.15f);
        Assert.Equal(0.5f, b, 0.0001f);
        b = ConsoleUse.Ease(b, false, 1f);
        Assert.Equal(0f, b);
        Assert.Equal(0f, ConsoleUse.Ease(0f, false, 0f));
    }

    [Fact]
    public void Smooth_EasesInAndOut()
    {
        Assert.Equal(0f, ConsoleUse.Smooth(-1f));
        Assert.Equal(0f, ConsoleUse.Smooth(0f));
        Assert.Equal(0.5f, ConsoleUse.Smooth(0.5f), 0.00001f);
        Assert.Equal(1f, ConsoleUse.Smooth(1f));
        Assert.Equal(1f, ConsoleUse.Smooth(2f));
        Assert.True(ConsoleUse.Smooth(0.1f) < 0.1f);
    }
}
