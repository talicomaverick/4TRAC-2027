using FourTrac.Core.Atm;
using Xunit;

namespace FourTrac.Tests;

/// <summary>
/// V0 code verification for the atmosphere model (PLAN.md §7):
/// ISA anchor points, humid-air density, speed of sound, density altitude.
/// </summary>
public class AtmosphereTests
{
    [Fact]
    public void IsaAtSeaLevel_Anchors()
    {
        var atm = Atmosphere.IsaAtElevation(0.0);
        Assert.Equal(288.15, atm.AnchorTemperatureK, 9);
        Assert.Equal(101325.0, atm.AnchorPressurePa, 6);
        Assert.Equal(1.2250, atm.Density(0.0), 4);
    }

    [Fact]
    public void Isa_TropopauseAnchors()
    {
        var atm = Atmosphere.IsaAtElevation(0.0);
        Assert.Equal(216.65, atm.Temperature(11000.0), 9);              // 288.15 - 0.0065*11000
        Assert.Equal(22632.06, atm.Pressure(11000.0), 0);               // published ISA value
        Assert.Equal(0.36392, atm.Density(11000.0), 4);                 // published ISA value
    }

    [Fact]
    public void Isa_AtElevationAnchorsConsistent()
    {
        // At 1000 m site elevation: T = 281.65 K, and density must fall off the
        // standard table value (~1.112 kg/m^3).
        var atm = Atmosphere.IsaAtElevation(1000.0);
        Assert.Equal(281.65, atm.AnchorTemperatureK, 9);
        double rho = atm.Density(1000.0);
        Assert.True(Math.Abs(rho - 1.112) < 0.002, $"rho(1000 m) = {rho}");
    }

    [Fact]
    public void Measurements_DryAirDensities()
    {
        var dry20 = Atmosphere.FromMeasurements(20.0, 101325.0, 0.0, 0.0);
        Assert.Equal(1.2041, dry20.Density(0.0), 3);

        var sat20 = Atmosphere.FromMeasurements(20.0, 101325.0, 1.0, 0.0);
        // Humid air is less dense: published ~1.1936 kg/m^3 at 20 deg C / 100 % RH.
        Assert.Equal(1.1936, sat20.Density(0.0), 3);
        Assert.True(sat20.Density(0.0) < dry20.Density(0.0));
    }

    [Fact]
    public void SpeedOfSound_At15C()
    {
        var atm = Atmosphere.IsaAtElevation(0.0);
        Assert.Equal(340.29, atm.SpeedOfSound(0.0), 1);
    }

    [Fact]
    public void DensityAltitude_IsInverseOfDensityProfile()
    {
        var atm = Atmosphere.IsaAtElevation(0.0);
        foreach (double h in new[] { 0.0, 500.0, 2000.0, 5000.0, 11000.0 })
        {
            double da = Atmosphere.DensityAltitude(atm.Density(h));
            Assert.True(Math.Abs(da - h) < 2.0, $"DA(rho({h})) = {da}");
        }
    }

    [Fact]
    public void DensityAltitude_HotDayRisesAboveFieldElevation()
    {
        // 30 deg C, 1013 hPa at sea level: density altitude ~ 530 m.
        var hot = Atmosphere.FromMeasurements(30.0, 101325.0, 0.0, 0.0);
        double da = Atmosphere.DensityAltitude(hot.Density(0.0));
        Assert.True(da > 450 && da < 650, $"hot-day DA = {da}");
    }

    [Fact]
    public void InvalidInputs_Rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Atmosphere.FromMeasurements(15, -1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Atmosphere.FromMeasurements(15, 101325, 1.5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Atmosphere.FromMeasurements(15, 0, 0, 0));
    }
}
