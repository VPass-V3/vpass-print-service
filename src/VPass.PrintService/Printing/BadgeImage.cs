using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace VPass.PrintService.Printing;

/// <summary>
/// Turns the server-rendered badge (base64 JPEG from PrinterTemplate.getBaseTemplateAsync, i.e. the
/// template chosen in adminapp → Badge Printing Templates) into a file the backend can print.
/// The service never composes badge content; it only decodes, optionally rotates landscape
/// templates (3, 4) by 90° so the long edge runs along a continuous label roll — the same rule
/// the kiosk applies — and re-encodes losslessly as PNG.
/// </summary>
public static class BadgeImage
{
    public static bool IsLandscapeTemplate(int? templateType) => templateType == 3 || templateType == 4;

    public static string PrepareFile(string base64, int? templateType, bool rotateLandscape, string tempDir)
    {
        byte[] bytes;
        try
        {
            string data = base64.Trim();
            int comma = data.IndexOf(',');
            if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            {
                data = data[(comma + 1)..];
            }
            bytes = Convert.FromBase64String(data);
        }
        catch (FormatException ex)
        {
            throw new PrintException("Badge image is not valid base64", ex);
        }

        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, $"badge-{Guid.NewGuid():N}.png");
        try
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(bytes);
            if (rotateLandscape && IsLandscapeTemplate(templateType))
            {
                image.Mutate(x => x.Rotate(RotateMode.Rotate90));
            }
            image.SaveAsPng(path);
        }
        catch (Exception ex) when (ex is not PrintException)
        {
            throw new PrintException("Badge image could not be decoded: " + ex.Message, ex);
        }
        return path;
    }

    /// <summary>A printable test pattern (no text, no fonts needed): border, VPass-blue bar and a size grid.</summary>
    public static string CreateTestPattern(string tempDir, bool landscape)
    {
        int w = landscape ? 400 : 257, h = landscape ? 257 : 400;
        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, $"test-{Guid.NewGuid():N}.png");
        using var image = new Image<Rgba32>(w, h, Color.White);
        var black = new Rgba32(0, 0, 0);
        var blue = new Rgba32(0x15, 0xA1, 0xDC);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    bool border = x < 4 || y < 4 || x >= w - 4 || y >= h - 4;
                    bool bar = y >= 20 && y < 60 && x >= 20 && x < w - 20;
                    bool grid = (x % 40 == 0 || y % 40 == 0) && y > 80;
                    bool block = y > 100 && y < h - 30 && x > 30 && x < w - 30 && ((x / 30) + (y / 30)) % 2 == 0 && (x / 30) % 3 == 0 && (y / 30) % 3 == 0;
                    if (border || block) row[x] = black;
                    else if (bar) row[x] = blue;
                    else if (grid) row[x] = new Rgba32(160, 160, 160);
                }
            }
        });
        image.SaveAsPng(path);
        return path;
    }
}
