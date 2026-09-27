namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The test's own geodesic reference: Vincenty's inverse formula on the WGS
/// 84 ellipsoid. It exists so the buffer tests measure the engine's answer
/// against an independent implementation of "how far along the ground",
/// rather than against the projection the engine used.
/// </summary>
internal static class Geodesic
{
    private const double SemiMajor = 6378137.0;
    private const double InverseFlattening = 298.257223563;

    /// <summary>The geodesic distance in metres between two points in degrees.</summary>
    public static double DistanceMetres(double longitude1, double latitude1, double longitude2, double latitude2)
    {
        var f = 1.0 / InverseFlattening;
        var b = SemiMajor * (1.0 - f);
        var l = (longitude2 - longitude1) * Math.PI / 180.0;
        var lambda = l;
        var u1 = Math.Atan((1.0 - f) * Math.Tan(latitude1 * Math.PI / 180.0));
        var u2 = Math.Atan((1.0 - f) * Math.Tan(latitude2 * Math.PI / 180.0));
        var sinU1 = Math.Sin(u1);
        var cosU1 = Math.Cos(u1);
        var sinU2 = Math.Sin(u2);
        var cosU2 = Math.Cos(u2);

        var sinSigma = 0.0;
        var cosSigma = 0.0;
        var sigma = 0.0;
        var cos2SigmaM = 0.0;
        var cosSqAlpha = 0.0;
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var sinLambda = Math.Sin(lambda);
            var cosLambda = Math.Cos(lambda);
            sinSigma = Math.Sqrt(
                Math.Pow(cosU2 * sinLambda, 2) + Math.Pow((cosU1 * sinU2) - (sinU1 * cosU2 * cosLambda), 2));
            if (sinSigma == 0.0)
            {
                return 0.0;
            }

            cosSigma = (sinU1 * sinU2) + (cosU1 * cosU2 * cosLambda);
            sigma = Math.Atan2(sinSigma, cosSigma);
            var sinAlpha = (cosU1 * cosU2 * sinLambda) / sinSigma;
            cosSqAlpha = 1.0 - (sinAlpha * sinAlpha);
            cos2SigmaM = cosSqAlpha == 0.0 ? 0.0 : cosSigma - ((2.0 * sinU1 * sinU2) / cosSqAlpha);
            var c = (1.0 / 16.0) * f * f * cosSqAlpha * (4.0 + (f * f * (4.0 - (3.0 * cosSqAlpha))));
            var previous = lambda;
            var inner = c * cosSigma * (-1.0 + (2.0 * cos2SigmaM * cos2SigmaM));
            var middle = cos2SigmaM + (c * inner);
            var outer = sigma + (c * sinSigma * middle);
            lambda = l + ((1.0 - c) * f * sinAlpha * outer);
            if (Math.Abs(lambda - previous) < 1e-13)
            {
                break;
            }
        }

        var uSq = (cosSqAlpha * ((SemiMajor * SemiMajor) - (b * b))) / (b * b);
        var bigA = 1.0 + (uSq / 16384.0 * (4096.0 + (uSq * (-768.0 + (uSq * (320.0 - (175.0 * uSq)))))));
        var bigB = (uSq / 1024.0) * (256.0 + (uSq * (-128.0 + (uSq * (74.0 - (47.0 * uSq))))));
        var head = cosSigma * (-1.0 + (2.0 * cos2SigmaM * cos2SigmaM));
        var tail = cos2SigmaM * (-3.0 + (4.0 * sinSigma * sinSigma)) * (-3.0 + (4.0 * cos2SigmaM * cos2SigmaM));
        var deltaSigma = bigB * sinSigma * (cos2SigmaM + ((bigB / 4.0) * (head - ((bigB / 6.0) * tail))));
        return (b * bigA) * (sigma - deltaSigma);
    }
}
