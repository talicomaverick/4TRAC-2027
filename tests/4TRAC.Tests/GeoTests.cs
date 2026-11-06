using FourTrac.Core.Geo;
using Xunit;

namespace FourTrac.Tests;

/// <summary>
/// V0 code verification for the geodesy layer (PLAN.md §7):
/// ECEF/LLA/ENU/UTM round trips, exact identities, Vincenty against published
/// arc-length constants.
/// </summary>
public class GeoTests
{
    [Fact]
    public void EcefLla_RoundTrip_GlobalGrid()
    {
        double[] lats = { -89.9, -60.0, -45.5, -0.001, 0.0, 0.001, 33.7, 60.0, 89.9 };
        double[] lons = { -179.9, -120.0, -45.0, 0.0, 45.0, 120.0, 179.9 };
        double[] hs = { 0.0, 100.0, 1000.0, 8848.0, 100000.0 };

        foreach (var lat in lats)
        foreach (var lon in lons)
        foreach (var h in hs)
        {
            var ecef = Geodetic.LlaToEcef(lat, lon, h);
            Geodetic.EcefToLla(ecef, out double lat2, out double lon2, out double h2);
            Assert.True(Math.Abs(lat - lat2) < 1e-9, $"lat {lat} lon {lon} h {h}: dlat={lat - lat2}");
            Assert.True(Math.Abs(lon - lon2) < 1e-9, $"lat {lat} lon {lon} h {h}: dlon={lon - lon2}");
            Assert.True(Math.Abs(h - h2) < 1e-6, $"lat {lat} lon {lon} h {h}: dh={h - h2}");
        }
    }

    [Fact]
    public void EcefLla_EquatorAndPoleIdentities()
    {
        // Equator at h=0: |r| = a exactly.
        var equator = Geodetic.LlaToEcef(0.0, 42.0, 0.0);
        Assert.Equal(Wgs84.A, equator.Length, 9);

        // Pole at h=0: |r| = b exactly.
        var pole = Geodetic.LlaToEcef(90.0, 0.0, 0.0);
        Assert.Equal(Wgs84.B, pole.Z, 9);

        // Height at the pole.
        var poleHigh = Geodetic.LlaToEcef(90.0, 0.0, 1234.5);
        Geodetic.EcefToLla(poleHigh, out double lat, out _, out double h);
        Assert.Equal(90.0, lat, 9);
        Assert.Equal(1234.5, h, 6);
    }

    [Fact]
    public void EcefLla_GeodeticVsGeocentricLatitudeRelation()
    {
        // On the ellipsoid surface the geodetic and geocentric latitudes obey
        // tan(phi_geo) = tan(phi_gc)/(1 - e^2). Verify both directions: forward
        // ECEF construction gives the geodetic latitude via the inverse, and the
        // geocentric latitude of the same point satisfies the identity.
        foreach (double latGeo in new[] { 10.0, 30.0, 45.0, 60.0, 80.0, -33.7 })
        {
            var ecef = Geodetic.LlaToEcef(latGeo, 40.0, 0.0);
            double latGc = Math.Atan2(ecef.Z, Math.Sqrt(ecef.X * ecef.X + ecef.Y * ecef.Y)) * 180.0 / Math.PI;
            double expectedGeo = Math.Atan(Math.Tan(latGc * Math.PI / 180.0) / (1.0 - Wgs84.E2)) * 180.0 / Math.PI;
            Assert.Equal(latGeo, expectedGeo, 9);
        }
    }

    [Fact]
    public void Enu_RoundTrip_AndEastVector()
    {
        var frame = EnuFrame.AtOrigin(52.0, 13.4, 40.0);

        foreach (var (lat, lon, h) in new[] { (52.0, 13.4, 40.0), (51.0, 14.5, 120.0), (53.2, 12.0, -10.0) })
        {
            var ecef = Geodetic.LlaToEcef(lat, lon, h);
            var back = frame.ToEcef(frame.ToEnu(ecef));
            Assert.True((back - ecef).Length < 1e-6, $"ENU round trip failed at {lat},{lon},{h}");
        }

        // Point due east of the origin: ENU east ~ full offset, N and U ~ 0.
        var east = Geodetic.LlaToEcef(52.0, 13.41, 40.0);
        var enu = frame.ToEnu(east);
        Assert.True(enu.X > 600 && enu.X < 800, $"east component {enu.X}");
        Assert.True(Math.Abs(enu.Y) < 1.0, $"north component {enu.Y}");
        Assert.True(Math.Abs(enu.Z) < 1.0, $"up component {enu.Z}");
    }

    [Fact]
    public void Enu_AzimuthAndElevationHelpers()
    {
        var e = EnuFrame.DirectionFromAzimuthElevation(90.0, 0.0);
        Assert.Equal(1.0, e.X, 12);
        Assert.Equal(0.0, e.Y, 12);
        Assert.Equal(0.0, e.Z, 12);

        Assert.Equal(90.0, EnuFrame.AzimuthDeg(new Cartesian3(1.0, 0.0, 0.0)), 12);
        Assert.Equal(89.942704, EnuFrame.AzimuthDeg(new Cartesian3(1.0, 0.001, 0.0)), 6); // atan2(1, 0.001)
        Assert.Equal(0.0, EnuFrame.AzimuthDeg(new Cartesian3(0.0, 1.0, 5.0)), 12);
        Assert.Equal(45.0, EnuFrame.ElevationDeg(new Cartesian3(0.0, 1.0, 1.0)), 12);

        var ne = EnuFrame.DirectionFromAzimuthElevation(359.0, 0.0);
        Assert.True(EnuFrame.AzimuthDeg(ne) > 358.9 && EnuFrame.AzimuthDeg(ne) < 359.1);
    }

    [Fact]
    public void Utm_CentralMeridianAndEquatorIdentities()
    {
        Assert.Equal(31, Utm.ZoneOf(0.0));
        Assert.Equal(31, Utm.ZoneOf(2.9999));
        Assert.Equal(31, Utm.ZoneOf(3.0));   // zone 31 spans [0, 6)
        Assert.Equal(32, Utm.ZoneOf(6.0));   // zone 32 spans [6, 12)
        Assert.Equal(1, Utm.ZoneOf(-179.9));
        Assert.Equal(60, Utm.ZoneOf(179.9));

        // On the central meridian of zone 31 (3 deg E) at the equator:
        var c = Utm.LatLonToUtm(0.0, 3.0);
        Assert.Equal(500000.0, c.Easting, 6);
        Assert.Equal(0.0, c.Northing, 6);
        Assert.False(c.South);

        // Southern hemisphere gets the false northing.
        var s = Utm.LatLonToUtm(-10.0, 3.0);
        Assert.True(s.South);
        Assert.True(s.Northing > 8_800_000 && s.Northing < 8_900_000, $"northing {s.Northing}");
    }

    [Fact]
    public void Utm_RoundTrip()
    {
        double[] lats = { -55.0, -33.7, -0.5, 0.0, 0.5, 33.7, 51.4778, 69.6 };
        double[] lons = { -179.0, -120.0, -45.0, -0.5, 0.0, 12.5, 60.0, 178.0 };

        foreach (var lat in lats)
        foreach (var lon in lons)
        {
            var c = Utm.LatLonToUtm(lat, lon, 0.0);
            Utm.UtmToLatLon(c, out double lat2, out double lon2);
            Assert.True(Math.Abs(lat - lat2) < 1e-8, $"lat {lat} lon {lon}: dlat={lat - lat2}");
            Assert.True(Math.Abs(lon - lon2) < 1e-8, $"lat {lat} lon {lon}: dlon={lon - lon2}");
        }
    }

    [Fact]
    public void Utm_KnownPoint_Greenwich()
    {
        // (0, 0) sits 3 deg west of the central meridian of zone 31 (3 deg E).
        // Hand check: 500000 - k0*A*dlambda*(1 + A^2/6) ~ 500000 - 333825 - 154.
        var c = Utm.LatLonToUtm(0.0, 0.0);
        Assert.Equal(31, c.Zone);
        Assert.InRange(c.Easting, 166020.0, 166023.0);
        Assert.Equal(0.0, c.Northing, 6);
    }

    [Fact]
    public void Vincenty_EquatorialArc_MatchesExactValue()
    {
        // Along the equator the geodesic arc is exactly A * dlambda.
        double expected = Wgs84.A * (Math.PI / 180.0);
        var r = Geodesic.Inverse(0.0, 0.0, 0.0, 1.0);
        Assert.Equal(expected, r.DistanceM, 3);
        Assert.Equal(90.0, r.ForwardAzimuthDeg, 9);
    }

    [Fact]
    public void Vincenty_QuarterMeridian_MatchesPublishedConstant()
    {
        // Published WGS84 quarter meridian (equator to pole): 10001965.7293 m.
        var r = Geodesic.Inverse(0.0, 0.0, 90.0, 0.0);
        Assert.Equal(10001965.7293, r.DistanceM, 0);
    }

    [Fact]
    public void Utm_ScaleAtCentralMeridian_IsK0()
    {
        // 0.01 deg of longitude at the equator near the central meridian.
        var a = Utm.LatLonToUtm(0.0, 2.995);
        var b = Utm.LatLonToUtm(0.0, 3.005);
        double projected = Math.Abs(b.Easting - a.Easting);
        double trueDistance = Geodesic.Inverse(0.0, 2.995, 0.0, 3.005).DistanceM;
        double ratio = projected / trueDistance;
        Assert.True(Math.Abs(ratio - 0.9996) < 0.0002, $"scale ratio {ratio}");
    }
}
