using System.Globalization;
using System.Text.RegularExpressions;
using Silo.Plugins;

namespace Silo.Server.Core;

/// <summary>
/// Finds where a report is about from coordinates written in it: decimal degrees, degrees-minutes-seconds,
/// and MGA/AMG/UTM eastings and northings with a zone. Keeps points inside Australia, drops outliers, and
/// returns the centre and extent. Good enough to put a report on a map; the enricher's footprint wins.
/// </summary>
public static class GeoText
{
    const RegexOptions O = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly Regex Decimal = new(@"(-?\b\d{1,2}\.\d{3,7})\s*°?\s*([SN])?\s*[,;/]?\s+(\b1[1-5]\d\.\d{3,7})\s*°?\s*([EW])?", O);
    static readonly Regex LatLabel = new(@"\blat(?:itude)?\b\s*[:=]?\s*(-?\d{1,2}\.\d{2,7})\s*°?\s*([SN])?", O);
    static readonly Regex LonLabel = new(@"\blong?(?:itude)?\b\s*[:=]?\s*(1[1-5]\d\.\d{2,7})\s*°?\s*([EW])?", O);
    static readonly Regex Dms = new(@"(\d{1,2})\s*[°º˚o]\s*(\d{1,2}(?:\.\d+)?)\s*['’′`]\s*(?:(\d{1,2}(?:\.\d+)?)\s*(?:""|”|″|''))?\s*([SN])\b[\s,;/and]{0,12}(1[1-5]\d)\s*[°º˚o]\s*(\d{1,2}(?:\.\d+)?)\s*['’′`]\s*(?:(\d{1,2}(?:\.\d+)?)\s*(?:""|”|″|''))?\s*([EW])\b", O);
    static readonly Regex Zone = new(@"\b(?:MGA|GDA|AMG|UTM|GDA94|GDA2020|MGA94|MGA2020|AMG84|AMG66)\s*(?:94|2020|84|66)?\s*[,(]?\s*(?:zone|z)?\s*(49|5[0-6])\b|\bzone\s*(49|5[0-6])\b", O);
    static readonly Regex EastNorth = new(@"\b(\d{6}(?:\.\d+)?)\s*(?:m\s*)?E\b[^\d]{0,25}?(\d{7}(?:\.\d+)?)\s*(?:m\s*)?N\b", O);
    static readonly Regex NorthEast = new(@"\b(\d{7}(?:\.\d+)?)\s*(?:m\s*)?N\b[^\d]{0,25}?(\d{6}(?:\.\d+)?)\s*(?:m\s*)?E\b", O);

    // Map-frame graticule labels written one axis at a time: 24°28'0"S … 148°42'0"E.
    static readonly Regex LatTick = new(@"(?<![\d.])([1-4]\d)\s*[°º˚]\s*(\d{1,2}(?:\.\d+)?)\s*['’′]\s*(?:(\d{1,2}(?:\.\d+)?)\s*(?:""|”|″|''))?\s*S\b", O);
    static readonly Regex LonTick = new(@"(?<![\d.])(1[1-5]\d)\s*[°º˚]\s*(\d{1,2}(?:\.\d+)?)\s*['’′]\s*(?:(\d{1,2}(?:\.\d+)?)\s*(?:""|”|″|''))?\s*E\b", O);
    // Map grid labels: round eastings (6 digits) and northings (7 digits) printed along a map frame.
    static readonly Regex GridNum = new(@"(?<![\d.,])(\d{6,7})(?![\d.,])", O);
    // 1:250 000 map sheet codes on the international grid, e.g. SH50-03, SG 51-05.
    static readonly Regex Sheet = new(@"\bS([A-I])\s?(49|5[0-6])\s?[-–]\s?(0?[1-9]|1[0-6])\b", O);

    static bool InAustralia(double lat, double lon) => lat is >= -44.5 and <= -9 && lon is >= 112 and <= 154.5;

    public static Geo? Find(string text, string? state = null)
    {
        var pts = new List<(double lat, double lon, int weight)>();
        static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        foreach (Match m in Decimal.Matches(text))
        {
            double lat = D(m.Groups[1].Value), lon = D(m.Groups[3].Value);
            if (m.Groups[2].Value.Equals("N", StringComparison.OrdinalIgnoreCase)) continue;
            lat = -Math.Abs(lat);
            if (InAustralia(lat, lon)) pts.Add((lat, lon, 2));
        }
        var lats = LatLabel.Matches(text).ToList();
        var lons = LonLabel.Matches(text).ToList();
        foreach (var la in lats)
        {
            var lo = lons.FirstOrDefault(x => Math.Abs(x.Index - la.Index) < 160);
            if (lo == null) continue;
            double lat = -Math.Abs(D(la.Groups[1].Value)), lon = D(lo.Groups[1].Value);
            if (InAustralia(lat, lon)) pts.Add((lat, lon, 2));
        }
        foreach (Match m in Dms.Matches(text))
        {
            double lat = D(m.Groups[1].Value) + D(m.Groups[2].Value) / 60 + (m.Groups[3].Success ? D(m.Groups[3].Value) / 3600 : 0);
            double lon = D(m.Groups[5].Value) + D(m.Groups[6].Value) / 60 + (m.Groups[7].Success ? D(m.Groups[7].Value) / 3600 : 0);
            if (m.Groups[4].Value.Equals("S", StringComparison.OrdinalIgnoreCase)) lat = -lat;
            if (InAustralia(lat, lon)) pts.Add((lat, lon, 3));
        }

        AddTicks(text, pts);

        // Projected coordinates need a zone stated somewhere in the text (the most common one wins).
        var zones = Zone.Matches(text).Select(m => int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)).GroupBy(z => z).OrderByDescending(g => g.Count()).ToList();
        string? raw = null;
        if (zones.Count > 0)
        {
            int zone = zones[0].Key;
            var en = EastNorth.Matches(text).Select(m => (e: D(m.Groups[1].Value), n: D(m.Groups[2].Value)))
                .Concat(NorthEast.Matches(text).Select(m => (e: D(m.Groups[2].Value), n: D(m.Groups[1].Value))));
            foreach (var (e, n) in en)
            {
                if (e is < 100_000 or > 900_000 || n is < 5_000_000 or > 9_000_000) continue;
                var (lat, lon) = UtmToLatLon(e, n, zone, south: true);
                if (InAustralia(lat, lon)) { pts.Add((lat, lon, 1)); raw ??= $"MGA zone {zone} {e:F0}E {n:F0}N"; }
            }
            if (AddGrid(text, zone, pts)) raw ??= $"map grid, MGA zone {zone}";
        }
        if (pts.Count == 0) return FromSheets(text);
        // A single projected point is too easy to get wrong (a sample number that looks like a northing).
        if (pts.Sum(p => p.weight) < 2) return FromSheets(text);

        double medLat = Median(pts.Select(p => p.lat)), medLon = Median(pts.Select(p => p.lon));
        var near = pts.Where(p => Math.Abs(p.lat - medLat) < 1.5 && Math.Abs(p.lon - medLon) < 1.5).ToList();
        if (near.Count == 0) return null;
        double w = near.Min(p => p.lon), e2 = near.Max(p => p.lon), s = near.Min(p => p.lat), n2 = near.Max(p => p.lat);
        return new Geo { Lat = Math.Round(Median(near.Select(p => p.lat)), 5), Lon = Math.Round(Median(near.Select(p => p.lon)), 5), Bbox = [w, s, e2, n2], Basis = "text", Raw = raw };
    }

    /// <summary>Graticule labels: at least two latitudes and two longitudes close together make a map frame.</summary>
    static void AddTicks(string text, List<(double lat, double lon, int weight)> pts)
    {
        static double Deg(Match m) => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 60
            + (m.Groups[3].Success ? double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) / 3600 : 0);
        var lats = LatTick.Matches(text).Where(m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) < 60).Select(m => (i: m.Index, v: -Deg(m))).ToList();
        var lons = LonTick.Matches(text).Where(m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) < 60).Select(m => (i: m.Index, v: Deg(m))).ToList();
        if (lats.Count < 2 || lons.Count < 2) return;
        // Use the labels of the densest frame: those within 3000 characters of the first longitude label.
        int at = lons[0].i;
        var la = lats.Where(x => Math.Abs(x.i - at) < 3000).Select(x => x.v).Distinct().ToList();
        var lo = lons.Where(x => Math.Abs(x.i - at) < 3000).Select(x => x.v).Distinct().ToList();
        if (la.Count < 2 || lo.Count < 2 || la.Max() - la.Min() > 3 || lo.Max() - lo.Min() > 3) return;
        foreach (var (y, x) in new[] { (la.Min(), lo.Min()), (la.Max(), lo.Max()), (la.Min(), lo.Max()), (la.Max(), lo.Min()) })
            if (InAustralia(y, x)) pts.Add((y, x, 2));
    }

    /// <summary>Map grid labels: round eastings and northings near each other (a map frame), converted with the stated zone.</summary>
    static bool AddGrid(string text, int zone, List<(double lat, double lon, int weight)> pts)
    {
        var nums = GridNum.Matches(text).Select(m => (i: m.Index, v: long.Parse(m.Value))).Where(x => x.v % 500 == 0).ToList();
        var east = nums.Where(x => x.v is >= 150_000 and <= 850_000).ToList();
        var north = nums.Where(x => x.v is >= 5_000_000 and <= 9_000_000).ToList();
        foreach (var e0 in east)
        {
            var es = east.Where(x => x.i >= e0.i && x.i - e0.i < 1500).Select(x => x.v).Distinct().ToList();
            var ns = north.Where(x => Math.Abs(x.i - e0.i) < 1500).Select(x => x.v).Distinct().ToList();
            if (es.Count < 2 || ns.Count < 2 || es.Max() - es.Min() > 150_000 || ns.Max() - ns.Min() > 150_000) continue;
            var corners = new[] { (es.Min(), ns.Min()), (es.Max(), ns.Max()) }.Select(c => UtmToLatLon(c.Item1, c.Item2, zone, true)).ToList();
            if (!corners.All(c => InAustralia(c.lat, c.lon))) continue;
            foreach (var c in corners) pts.Add((c.lat, c.lon, 2));
            return true;
        }
        return false;
    }

    /// <summary>1:250 000 sheet extents (1.5° by 1°, 16 to a 6° by 4° block, numbered from the north-west).</summary>
    public static double[]? SheetBox(char row, int zone, int n)
    {
        int r = char.ToUpperInvariant(row) - 'A';
        if (r < 0 || r > 8 || n < 1 || n > 16) return null;
        double top = -4.0 * r, west = (zone - 31) * 6.0;
        int col = (n - 1) % 4, line = (n - 1) / 4;
        double w = west + col * 1.5, nn = top - line;
        return [w, nn - 1, w + 1.5, nn];
    }

    static Geo? FromSheets(string text)
    {
        var boxes = Sheet.Matches(text).Select(m => (code: $"S{char.ToUpperInvariant(m.Groups[1].Value[0])}{m.Groups[2].Value}-{int.Parse(m.Groups[3].Value):00}",
                box: SheetBox(m.Groups[1].Value[0], int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))))
            .Where(x => x.box != null && InAustralia((x.box[1] + x.box[3]) / 2, (x.box[0] + x.box[2]) / 2)).ToList();
        if (boxes.Count == 0) return null;
        // The most mentioned sheet anchors the report; neighbouring sheets widen the extent.
        var main = boxes.GroupBy(b => b.code).OrderByDescending(g => g.Count()).First().First();
        double cx = (main.box![0] + main.box[2]) / 2, cy = (main.box[1] + main.box[3]) / 2;
        var near = boxes.Where(b => Math.Abs((b.box![0] + b.box[2]) / 2 - cx) <= 3.1 && Math.Abs((b.box[1] + b.box[3]) / 2 - cy) <= 2.1).ToList();
        double w = near.Min(b => b.box![0]), s = near.Min(b => b.box![1]), e = near.Max(b => b.box![2]), n = near.Max(b => b.box![3]);
        return new Geo { Lat = Math.Round((s + n) / 2, 4), Lon = Math.Round((w + e) / 2, 4), Bbox = [w, s, e, n], Basis = "map sheet", Raw = string.Join(", ", near.Select(b => b.code).Distinct()) + " (1:250 000)" };
    }

    static double Median(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToArray();
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    /// <summary>UTM/MGA to latitude and longitude on GRS80 (GDA94/GDA2020 ≈ WGS84 at map scale).</summary>
    public static (double lat, double lon) UtmToLatLon(double easting, double northing, int zone, bool south)
    {
        const double a = 6378137.0, f = 1 / 298.257222101, k0 = 0.9996;
        double e2 = f * (2 - f), ep2 = e2 / (1 - e2);
        double x = easting - 500000.0, y = south ? northing - 10000000.0 : northing;
        double m = y / k0;
        double mu = m / (a * (1 - e2 / 4 - 3 * e2 * e2 / 64 - 5 * e2 * e2 * e2 / 256));
        double e1 = (1 - Math.Sqrt(1 - e2)) / (1 + Math.Sqrt(1 - e2));
        double phi1 = mu + (3 * e1 / 2 - 27 * Math.Pow(e1, 3) / 32) * Math.Sin(2 * mu) + (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32) * Math.Sin(4 * mu)
                      + (151 * Math.Pow(e1, 3) / 96) * Math.Sin(6 * mu) + (1097 * Math.Pow(e1, 4) / 512) * Math.Sin(8 * mu);
        double n1 = a / Math.Sqrt(1 - e2 * Math.Sin(phi1) * Math.Sin(phi1));
        double t1 = Math.Tan(phi1) * Math.Tan(phi1);
        double c1 = ep2 * Math.Cos(phi1) * Math.Cos(phi1);
        double r1 = a * (1 - e2) / Math.Pow(1 - e2 * Math.Sin(phi1) * Math.Sin(phi1), 1.5);
        double d = x / (n1 * k0);
        double lat = phi1 - (n1 * Math.Tan(phi1) / r1) * (d * d / 2 - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * ep2) * Math.Pow(d, 4) / 24
                     + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * ep2 - 3 * c1 * c1) * Math.Pow(d, 6) / 720);
        double lon = (d - (1 + 2 * t1 + c1) * Math.Pow(d, 3) / 6 + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * ep2 + 24 * t1 * t1) * Math.Pow(d, 5) / 120) / Math.Cos(phi1);
        double lon0 = (zone - 1) * 6 - 180 + 3;
        return (lat * 180 / Math.PI, lon0 + lon * 180 / Math.PI);
    }
}
