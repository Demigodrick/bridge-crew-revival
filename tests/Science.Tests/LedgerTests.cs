using StbcRevival.Science;
using Xunit;

public class LedgerTests
{
    [Fact]
    public void AddScan_StepsFivePercentCappedAtFifty()
    {
        var l = new Ledger();
        l.AddScan(UpgradeArea.Weapons);
        Assert.Equal(0.05f, l.Crew(UpgradeArea.Weapons), 0.0001f);
        for (int i = 0; i < 30; i++) { l.AddScan(UpgradeArea.Weapons); l.AddFactionScan(5); }
        Assert.Equal(0.50f, l.Crew(UpgradeArea.Weapons), 0.0001f);
        Assert.Equal(0.50f, l.CrewVsFaction(5), 0.0001f);
        Assert.Equal(0f, l.Crew(UpgradeArea.Shields));
        Assert.Equal(0f, l.CrewVsFaction(2));
    }

    [Fact]
    public void EnemyStep_RotatesDamageShieldsSpeed()
    {
        var l = new Ledger();
        for (int i = 0; i < 4; i++) l.AddEnemyStep();
        Assert.Equal(4, l.EnemySteps);
        Assert.Equal(0.06f, l.Enemy(EnemyArea.Damage), 0.0001f);
        Assert.Equal(0.03f, l.Enemy(EnemyArea.Shields), 0.0001f);
        Assert.Equal(0.03f, l.Enemy(EnemyArea.Speed), 0.0001f);
    }

    [Fact]
    public void Ledger_RoundTripsThroughWireFormat()
    {
        var l = new Ledger { On = true };
        l.AddScan(UpgradeArea.Shields); l.AddScan(UpgradeArea.Shields); l.AddScan(UpgradeArea.Repair);
        l.AddFactionScan(2); l.AddFactionScan(5); l.AddFactionScan(5);
        for (int i = 0; i < 5; i++) l.AddEnemyStep();
        var r = Ledger.FromWire(l.ToWire());
        Assert.True(r.On);
        foreach (UpgradeArea a in System.Enum.GetValues(typeof(UpgradeArea))) Assert.Equal(l.Crew(a), r.Crew(a), 0.0001f);
        Assert.Equal(l.CrewVsFaction(2), r.CrewVsFaction(2), 0.0001f);
        Assert.Equal(l.CrewVsFaction(5), r.CrewVsFaction(5), 0.0001f);
        Assert.Equal(5, r.EnemySteps);
        Assert.Equal(l.ToWire(), r.ToWire());
        Assert.False(Ledger.FromWire(new Ledger().ToWire()).On);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x;y")]
    [InlineData("SCL1;1;NaN,0,0,0;;0")]
    [InlineData("SCL1;1;0,0,0,0;2:NaN;0")]
    [InlineData("SCL1;1;0,0,0,0;;-4")]
    [InlineData("SCL1;1;9,0,0,0;;0")]
    [InlineData("SCL1;1;0,0;;0")]
    public void Ledger_FromWire_IgnoresGarbage(string s)
    {
        var l = Ledger.FromWire(s);
        Assert.False(l.On);
        Assert.Equal(0f, l.Crew(UpgradeArea.Weapons));
        Assert.Equal(0, l.EnemySteps);
    }
}

public class LedgerHardeningTests
{
    [Fact]
    public void AddFactionScan_IgnoresNewTeamsBeyondSixteen()
    {
        var l = new Ledger();
        for (int t = 0; t < 20; t++) l.AddFactionScan(t);
        Assert.Equal(0.05f, l.CrewVsFaction(15), 0.0001f);
        Assert.Equal(0f, l.CrewVsFaction(16));
        l.AddFactionScan(3); // a known team still counts
        Assert.Equal(0.10f, l.CrewVsFaction(3), 0.0001f);
        Assert.Equal(0.05f, Ledger.FromWire(l.ToWire()).CrewVsFaction(15), 0.0001f);
    }
}

public class HostileTeamTests
{
    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, true)] [InlineData(3, false)]
    [InlineData(4, true)] [InlineData(5, true)] [InlineData(6, true)] [InlineData(7, false)] [InlineData(-1, false)]
    public void IsHostileTeam(int team, bool hostile)
    {
        Assert.Equal(hostile, Ledger.IsHostileTeam(team));
    }
}
