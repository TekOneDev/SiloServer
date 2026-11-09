using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Xmp;

namespace Silo.Plugins.Image;

/// <summary>
/// Photos and scanned images: camera details, captions and GPS position from EXIF/IPTC/XMP. The text in the
/// image itself is read by the core's OCR (core photos, scanned maps, TIF report pages).
/// </summary>
public sealed class ImageFilter : FilterPlugin
{
    public override PluginManifest Manifest { get; } = new()
    {
        Id = "silo.filter.image",
        Name = "Images and scans",
        Version = "1.0.0",
        Kind = "filter",
        Description = "JPEG, PNG, TIFF and other images: EXIF/GPS metadata; text is read by OCR.",
        Publisher = "Silo",
        Extensions = [".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif", ".webp", ".heic"],
        Concurrency = 8,
        TimeoutSec = 120,
        MaxFileMB = 500,
        Fields =
        [
            new FieldSpec { Name = "camera", Label = "Camera", Type = "keyword", Facet = false },
        ],
    };

    public override ExtractResult Extract(ExtractArgs a, CancellationToken ct)
    {
        var r = new ExtractResult { OcrImage = true };
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(a.Path);
            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            var lines = new List<string>();
            if (ifd0 != null)
            {
                var cam = $"{ifd0.GetDescription(ExifDirectoryBase.TagMake)} {ifd0.GetDescription(ExifDirectoryBase.TagModel)}".Trim();
                if (cam.Length > 0) r.Fields.Add("camera", cam);
                r.Author = ifd0.GetDescription(ExifDirectoryBase.TagArtist);
                var desc = ifd0.GetDescription(ExifDirectoryBase.TagImageDescription);
                if (!string.IsNullOrWhiteSpace(desc)) { r.Title = desc.Trim(); lines.Add(desc); }
            }
            var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (sub != null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var taken)) r.Created = taken;
            var iptc = dirs.OfType<IptcDirectory>().FirstOrDefault();
            if (iptc != null)
                foreach (var tag in new[] { IptcDirectory.TagCaption, IptcDirectory.TagHeadline, IptcDirectory.TagKeywords, IptcDirectory.TagObjectName })
                    if (iptc.GetDescription(tag) is { Length: > 0 } v) lines.Add(v);
            var gps = dirs.OfType<GpsDirectory>().FirstOrDefault()?.GetGeoLocation();
            if (gps != null && !gps.IsZero)
                r.Geo = new Geo { Lat = gps.Latitude, Lon = gps.Longitude, Bbox = [gps.Longitude, gps.Latitude, gps.Longitude, gps.Latitude], Basis = "exif" };
            if (lines.Count > 0) r.Pages.Add(new PageText { N = 1, Text = string.Join("\n", lines) });
        }
        catch (Exception e) { r.Warnings.Add(e.Message); }
        return r;
    }
}
