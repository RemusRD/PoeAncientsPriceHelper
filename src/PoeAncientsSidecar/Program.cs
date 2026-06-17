using System.Text.Json;
using PoeAncientsPriceHelper.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// Headless engine sidecar. Speaks newline-delimited JSON over stdio:
//   request  (stdin):  {"path": "/abs/path/to/capture.png"}
//   response (stdout): {"rows":[{"top":..,"bottom":..,"centerY":..,"kind":"Tall","visibility":"Full"}],"rowCount":4,"confidence":0.7,"pitch":null}
//   startup  (stdout): {"event":"ready"}
// Anything diagnostic goes to stderr so it never corrupts the JSON stream on stdout.

var detector = new RuneshapeRowDetector();
Write(new { @event = "ready" });

string? line;
while ((line = Console.In.ReadLine()) is not null)
{
    line = line.Trim();
    if (line.Length == 0) continue;

    try
    {
        using var doc = JsonDocument.Parse(line);
        if (!doc.RootElement.TryGetProperty("path", out var pathProp) || pathProp.GetString() is not { } path)
        {
            Write(new { @event = "error", message = "missing 'path'" });
            continue;
        }
        if (!File.Exists(path))
        {
            Write(new { @event = "error", message = $"file not found: {path}" });
            continue;
        }

        var gray = LoadGray(path);
        var detection = detector.Detect(gray);

        Write(new
        {
            rows = detection.Rows.Select(r => new
            {
                top = r.Top,
                bottom = r.Bottom,
                centerY = r.CenterY,
                textCenterY = r.TextCenterY,
                kind = r.Kind.ToString(),
                visibility = r.Visibility.ToString()
            }),
            rowCount = detection.Rows.Count,
            confidence = detection.Confidence,
            pitch = detection.RowPitch,
            hasUsableRows = detection.HasUsableRows
        });
    }
    catch (Exception ex)
    {
        Write(new { @event = "error", message = ex.Message });
    }
}

static byte[,] LoadGray(string path)
{
    using var image = Image.Load<Rgb24>(path);
    var gray = new byte[image.Height, image.Width];
    image.ProcessPixelRows(accessor =>
    {
        for (int y = 0; y < accessor.Height; y++)
        {
            var row = accessor.GetRowSpan(y);
            for (int x = 0; x < accessor.Width; x++)
            {
                var p = row[x];
                gray[y, x] = (byte)((p.R * 30 + p.G * 59 + p.B * 11) / 100);
            }
        }
    });
    return gray;
}

static void Write(object payload)
{
    Console.Out.WriteLine(JsonSerializer.Serialize(payload));
    Console.Out.Flush();
}
