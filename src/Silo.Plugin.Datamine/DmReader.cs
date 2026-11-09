#nullable disable
// Ported from DmProbe.ps1 (datamine/ in the project files). Format assumptions are unconfirmed on real files.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Silo.Plugins.Datamine
{
  public class DmField
  {
    public string Name { get; set; }
    public string Type { get; set; }        // A = text, N = number
    public int Words { get; set; }          // storage words (text width = Words * 4 chars)
    public int Position { get; set; }       // first word in the record, 0 = implicit (constant)
    public bool Implicit { get; set; }
    public string Default { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public int? Distinct { get; set; }
    public string[] Values { get; set; }
  }

  public class DmInfo
  {
    public string Path { get; set; }
    public long Bytes { get; set; }
    public string Modified { get; set; }
    public string Status { get; set; }
    public string Note { get; set; }
    public string Precision { get; set; }   // single (4-byte words) / extended (8-byte words)
    public int PageBytes { get; set; }
    public string InternalName { get; set; }
    public string Database { get; set; }
    public string Description { get; set; }
    public double[] HeaderNumbers { get; set; }
    public int DescriptorStartWord { get; set; }
    public int FieldCount { get; set; }
    public int RecordWords { get; set; }
    public long? Records { get; set; }
    public long RecordsMaxFromSize { get; set; }
    public string FileType { get; set; }
    public string Extents { get; set; }      // "xmin,ymin,zmin,xmax,ymax,zmax"
    public string ExtentsSource { get; set; }
    public string BlockSize { get; set; }
    public string BlockCounts { get; set; }
    public bool Scanned { get; set; }
    public List<DmField> Fields { get; set; }
    public string HeaderHex { get; set; }
  }

  public static class DmProbe
  {
    const double ABSENT = 1e29; // Datamine absent data is -1e30, top/bottom +-1e30

    static string Txt(byte[] b, int off, int len)
    {
      var sb = new StringBuilder();
      for (int i = 0; i < len && off + i < b.Length; i++)
      {
        byte c = b[off + i];
        if (c >= 32 && c < 127) sb.Append((char)c);
      }
      return sb.ToString().Trim();
    }

    // Text spread over several words. Extended files may hold 4 chars + 4 blanks per 8-byte word.
    static string TxtW(byte[] b, int off, int words, int w)
    {
      var sb = new StringBuilder();
      for (int k = 0; k < words; k++)
      {
        int o = off + k * w;
        if (o + w > b.Length) break;
        int take = w;
        if (w == 8)
        {
          bool pad = true;
          for (int i = 4; i < 8; i++) if (b[o + i] != 32 && b[o + i] != 0) { pad = false; break; }
          if (pad) take = 4;
        }
        for (int i = 0; i < take; i++) { byte c = b[o + i]; if (c >= 32 && c < 127) sb.Append((char)c); }
      }
      return sb.ToString().Trim();
    }

    static double Num(byte[] b, int off, int w)
    {
      if (off + w > b.Length) return double.NaN;
      return w == 4 ? (double)BitConverter.ToSingle(b, off) : BitConverter.ToDouble(b, off);
    }

    static long? IntWord(byte[] b, int off, int w)
    {
      // Integers may be stored as reals or as ints; accept whichever is sane.
      double d = Num(b, off, w);
      if (!double.IsNaN(d) && !double.IsInfinity(d) && Math.Abs(d) < 1e12 && d == Math.Floor(d)) return (long)d;
      long i = w == 4 ? BitConverter.ToInt32(b, off) : BitConverter.ToInt64(b, off);
      if (i >= 0 && i < 1000000000000L) return i;
      return null;
    }

    static bool NameOk(string s)
    {
      if (s.Length == 0 || s.Length > 24) return false;
      foreach (char c in s) if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '#' || c == '%' || c == '$' || c == ' ')) return false;
      return char.IsLetter(s[0]) || s[0] == '_' || s[0] == '#' || s[0] == '%' || s[0] == '$';
    }

    // One descriptor = 7 words: name(2), type(1), stored word(1), word-in-field(1), spare(1), default(1)
    static bool DescOk(byte[] b, int word, int w)
    {
      int o = word * w;
      if (o + 7 * w > b.Length) return false;
      string name = TxtW(b, o, 2, w);
      string type = TxtW(b, o + 2 * w, 1, w);
      if (!NameOk(name) || (type != "A" && type != "N")) return false;
      long? pos = IntWord(b, o + 3 * w, w);
      long? wif = IntWord(b, o + 4 * w, w);
      return pos.HasValue && pos.Value >= 0 && pos.Value <= 8192 && wif.HasValue && wif.Value >= 0 && wif.Value <= 256;
    }

    public static DmInfo Probe(string path, long scanMaxBytes, bool hex)
    {
      var info = new DmInfo();
      info.Path = path;
      info.Fields = new List<DmField>();
      var fi = new FileInfo(path);
      info.Bytes = fi.Length;
      info.Modified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
      byte[] head;
      using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536))
      {
        int n = (int)Math.Min(65536L, fs.Length);
        head = new byte[n];
        int got = 0; while (got < n) { int r = fs.Read(head, got, n - got); if (r <= 0) break; got += r; }
      }
      if (hex) info.HeaderHex = BitConverter.ToString(head, 0, Math.Min(head.Length, 1024)).Replace("-", " ");

      // Find word size and where the field descriptors start: the longest run of valid 7-word descriptors.
      int bestW = 0, bestS = 0, bestRun = 0;
      foreach (int w in new int[] { 4, 8 })
      {
        for (int s = 4; s <= 80; s++)
        {
          int run = 0;
          while (DescOk(head, s + 7 * run, w)) run++;
          if (run > bestRun) { bestRun = run; bestW = w; bestS = s; }
        }
      }
      if (bestRun == 0)
      {
        info.Status = "unrecognised";
        info.Note = "No Datamine field descriptors found (possibly .dmx/compressed, or not a Datamine table).";
        if (info.HeaderHex == null) info.HeaderHex = BitConverter.ToString(head, 0, Math.Min(head.Length, 256)).Replace("-", " ");
        return info;
      }
      int W = bestW, S = bestS, nd = bestRun;
      int pageWords = 512, pageBytes = pageWords * W;
      info.Precision = W == 4 ? "single" : "extended";
      info.PageBytes = pageBytes;
      info.DescriptorStartWord = S;

      // Header text and numbers before the descriptors: name(2) db(2) description(...) then 4 numbers.
      info.InternalName = TxtW(head, 0, 2, W);
      info.Database = TxtW(head, 2 * W, 2, W);
      int nums = Math.Min(4, S - 4);
      info.Description = TxtW(head, 4 * W, S - 4 - nums, W);
      info.HeaderNumbers = new double[nums];
      for (int i = 0; i < nums; i++) { long? v = IntWord(head, (S - nums + i) * W, W); info.HeaderNumbers[i] = v.HasValue ? v.Value : Num(head, (S - nums + i) * W, W); }

      // Build fields (a text field wider than one word has one descriptor per word).
      var byName = new Dictionary<string, DmField>();
      int recWords = 0;
      for (int k = 0; k < nd; k++)
      {
        int o = (S + 7 * k) * W;
        string name = TxtW(head, o, 2, W);
        string type = TxtW(head, o + 2 * W, 1, W);
        int pos = (int)IntWord(head, o + 3 * W, W).Value;
        DmField f;
        if (!byName.TryGetValue(name, out f))
        {
          f = new DmField();
          f.Name = name; f.Type = type; f.Position = pos; f.Implicit = pos == 0; f.Words = 0; f.Default = "";
          byName[name] = f; info.Fields.Add(f);
        }
        f.Words++;
        if (type == "A") f.Default = (f.Default + TxtW(head, o + 6 * W, 1, W)).Trim();
        else { double d = Num(head, o + 6 * W, W); f.Default = Math.Abs(d) >= ABSENT ? "" : d.ToString("R"); }
        if (pos > 0) recWords = Math.Max(recWords, pos);
      }
      info.FieldCount = info.Fields.Count;
      info.RecordWords = recWords;

      int headerPages = (int)Math.Ceiling((S + 7.0 * nd) / pageWords);
      long totalPages = info.Bytes / pageBytes;
      int rpp = recWords > 0 ? pageWords / recWords : 0;
      info.RecordsMaxFromSize = rpp > 0 ? Math.Max(0, (totalPages - headerPages) * rpp) : 0;
      // Header numbers are (date?, fields, last page, records in last page). Use them if they agree with the file size.
      if (nums == 4 && rpp > 0)
      {
        double lastPage = info.HeaderNumbers[2], lastRec = info.HeaderNumbers[3];
        if (lastPage >= headerPages && lastPage <= totalPages + 1 && lastRec >= 0 && lastRec <= rpp)
          info.Records = (long)((lastPage - headerPages - 1) * rpp + lastRec);
      }
      if (!info.Records.HasValue) info.Records = info.RecordsMaxFromSize;

      Classify(info, byName);

      // Block model extents from the prototype (no record scan needed).
      if (info.FileType == "block model")
      {
        double xo, yo, zo, nx, ny, nz, dx, dy, dz;
        if (D(byName, "XMORIG", out xo) && D(byName, "YMORIG", out yo) && D(byName, "ZMORIG", out zo) &&
            D(byName, "NX", out nx) && D(byName, "NY", out ny) && D(byName, "NZ", out nz) &&
            D(byName, "XINC", out dx) && D(byName, "YINC", out dy) && D(byName, "ZINC", out dz))
        {
          info.Extents = string.Format("{0},{1},{2},{3},{4},{5}", xo, yo, zo, xo + nx * dx, yo + ny * dy, zo + nz * dz);
          info.ExtentsSource = "prototype";
          info.BlockSize = string.Format("{0}x{1}x{2}", dx, dy, dz);
          info.BlockCounts = string.Format("{0}x{1}x{2}", nx, ny, nz);
        }
      }

      if (scanMaxBytes > 0 && info.Bytes <= scanMaxBytes && recWords > 0 && info.Records > 0)
      {
        try { Scan(path, info, W, pageWords, headerPages, rpp); info.Scanned = true; }
        catch (Exception ex) { info.Note = "scan failed: " + ex.Message; }
      }
      info.Status = "ok";
      return info;
    }

    static bool D(Dictionary<string, DmField> f, string n, out double v)
    {
      v = 0; DmField x;
      return f.TryGetValue(n, out x) && double.TryParse(x.Default, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
    }

    static bool Has(Dictionary<string, DmField> f, params string[] names)
    {
      foreach (var n in names) if (!f.ContainsKey(n)) return false;
      return true;
    }

    static void Classify(DmInfo info, Dictionary<string, DmField> f)
    {
      string t = "table";
      if (Has(f, "XC", "YC", "ZC") && (Has(f, "XMORIG") || Has(f, "IJK") || Has(f, "XINC"))) t = "block model";
      else if (Has(f, "TRIANGLE") || Has(f, "PID1", "PID2", "PID3")) t = "wireframe triangles";
      else if (Has(f, "XP", "YP", "ZP", "PID")) t = "wireframe points";
      else if (Has(f, "XP", "YP", "ZP") && (Has(f, "PVALUE") || Has(f, "PTN"))) t = "strings";
      else if (Has(f, "BHID", "XCOLLAR", "YCOLLAR")) t = "drillhole collars";
      else if (Has(f, "BHID", "AT") && (Has(f, "BRG") || Has(f, "DIP"))) t = "drillhole surveys";
      else if (Has(f, "BHID", "FROM", "TO") && Has(f, "X", "Y", "Z")) t = "drillholes (desurveyed)";
      else if (Has(f, "BHID", "FROM", "TO")) t = "drillhole intervals";
      else if (Has(f, "XPT", "YPT", "ZPT")) t = "points";
      else if (Has(f, "XC", "YC", "ZC")) t = "points (XC/YC/ZC)";
      else if (Has(f, "X", "Y", "Z")) t = "points (X/Y/Z)";
      info.FileType = t;
    }

    static readonly string[] KeyText = { "BHID", "ROCK", "ROCKTYPE", "LITH", "DOMAIN", "ZONE", "RESCAT", "CLASS", "PIT", "STAGE", "MINE", "OREBODY", "GEOL" };

    static void Scan(string path, DmInfo info, int W, int pageWords, int headerPages, int rpp)
    {
      var stored = new List<DmField>();
      foreach (var f in info.Fields) if (!f.Implicit) stored.Add(f);
      var sets = new Dictionary<string, HashSet<string>>();
      foreach (var f in stored) if (f.Type == "A" && Array.IndexOf(KeyText, f.Name.ToUpperInvariant()) >= 0) sets[f.Name] = new HashSet<string>();
      int pageBytes = pageWords * W;
      long left = info.Records.Value;
      var page = new byte[pageBytes];
      using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
      {
        fs.Seek((long)headerPages * pageBytes, SeekOrigin.Begin);
        while (left > 0)
        {
          int got = 0; while (got < pageBytes) { int r = fs.Read(page, got, pageBytes - got); if (r <= 0) break; got += r; }
          if (got < pageBytes) break;
          int n = (int)Math.Min(rpp, left);
          for (int r = 0; r < n; r++)
          {
            int rec = r * info.RecordWords * W;
            foreach (var f in stored)
            {
              int o = rec + (f.Position - 1) * W;
              if (f.Type == "N")
              {
                double v = Num(page, o, W);
                if (double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) >= ABSENT) continue;
                if (!f.Min.HasValue || v < f.Min) f.Min = v;
                if (!f.Max.HasValue || v > f.Max) f.Max = v;
              }
              else
              {
                HashSet<string> hs;
                if (sets.TryGetValue(f.Name, out hs) && hs.Count <= 5000) { string s = TxtW(page, o, f.Words, W); if (s.Length > 0) hs.Add(s); }
              }
            }
          }
          left -= n;
        }
      }
      foreach (var kv in sets)
      {
        foreach (var f in stored) if (f.Name == kv.Key)
        {
          f.Distinct = kv.Value.Count;
          var l = new List<string>(kv.Value); l.Sort();
          if (l.Count > 200) l = l.GetRange(0, 200);
          f.Values = l.ToArray();
        }
      }
      if (info.Extents == null)
      {
        string[][] xyz = { new[] { "XP", "YP", "ZP" }, new[] { "XC", "YC", "ZC" }, new[] { "X", "Y", "Z" }, new[] { "XCOLLAR", "YCOLLAR", "ZCOLLAR" }, new[] { "XPT", "YPT", "ZPT" } };
        foreach (var set in xyz)
        {
          DmField a = null, b = null, c = null;
          foreach (var f in info.Fields) { if (f.Name == set[0]) a = f; if (f.Name == set[1]) b = f; if (f.Name == set[2]) c = f; }
          if (a != null && b != null && c != null && a.Min.HasValue && b.Min.HasValue && c.Min.HasValue)
          {
            info.Extents = string.Format("{0},{1},{2},{3},{4},{5}", a.Min, b.Min, c.Min, a.Max, b.Max, c.Max);
            info.ExtentsSource = "records " + set[0] + "/" + set[1] + "/" + set[2];
            break;
          }
        }
      }
    }
  }
}
