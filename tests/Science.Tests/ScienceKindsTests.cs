using StbcRevival.Science;
using Xunit;

public class ScienceKindsTests
{
    [Theory]
    [InlineData(0, UpgradeArea.Weapons)] [InlineData(1, UpgradeArea.Repair)]
    [InlineData(2, UpgradeArea.Shields)] [InlineData(3, UpgradeArea.Impulse)]
    public void ReadingArea_ByProp(int kind, UpgradeArea area)
    {
        Assert.Equal(area, ScienceKinds.ReadingArea(kind));
    }

    [Theory] [InlineData(-1)] [InlineData(4)]
    public void ReadingArea_UnknownPropGivesNone(int kind)
    {
        Assert.Null(ScienceKinds.ReadingArea(kind));
    }

    [Theory]
    [InlineData(2, "Klingon debris")] [InlineData(4, "Raider debris")] [InlineData(5, "Romulan debris")]
    [InlineData(6, "Borg debris")] [InlineData(7, "Pirate debris")] [InlineData(8, "Pirate debris")] [InlineData(9, "Hostile debris")]
    public void DebrisName_ByTeam(int team, string name)
    {
        Assert.Equal(name, ScienceKinds.DebrisName(team));
    }

    [Theory]
    [InlineData(0, 1, 1, 0, 90)] [InlineData(0, 1, -1, 0, -90)] [InlineData(0, 1, 0, 5, 0)] [InlineData(1, 0, 0, 1, -90)]
    public void Bearing_KnownTurns(double fx, double fz, double dx, double dz, double degrees)
    {
        Assert.Equal(degrees, ScienceKinds.Bearing(fx, fz, dx, dz), 6);
    }

    // Turned the way Unity's Quaternion.AngleAxis(angle, up) turns a level vector, the forward points along the offset.
    [Fact]
    public void Bearing_TurnsForwardOntoOffset()
    {
        var r = new System.Random(5);
        for (int i = 0; i < 200; i++)
        {
            double fx = r.NextDouble() * 2 - 1, fz = r.NextDouble() * 2 - 1, dx = r.NextDouble() * 2 - 1, dz = r.NextDouble() * 2 - 1;
            double a = ScienceKinds.Bearing(fx, fz, dx, dz) * System.Math.PI / 180;
            double x = fx * System.Math.Cos(a) + fz * System.Math.Sin(a), z = -fx * System.Math.Sin(a) + fz * System.Math.Cos(a);
            double fl = System.Math.Sqrt(fx * fx + fz * fz), dl = System.Math.Sqrt(dx * dx + dz * dz);
            Assert.Equal(dx / dl, x / fl, 6);
            Assert.Equal(dz / dl, z / fl, 6);
        }
    }
}
