using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIMeter.Core;

namespace AIMeter.App;

/// <summary>
/// Draws one gauge: a 270° arc (gap at the bottom), an optional inner arc, the number
/// in the middle and the provider label just below it. Never draws a background; the
/// optional dark edge is an outline that follows each shape.
/// </summary>
public static class GaugeVisual
{
    private const double StartAngle = 135;
    private const double TotalSweep = 270;
    private const double MinLabelSize = 11;
    private const double EdgeWidth = 1.25; // visible outline width, each side

    private static readonly Brush Teal = Freeze(new SolidColorBrush(Color.FromRgb(0x5D, 0xCA, 0xA5)));
    private static readonly Brush Amber = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x9F, 0x27)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A)));
    private static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromArgb(0x8C, 120, 128, 140)));
    private static readonly Brush DashedTrack = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 200, 206, 214)));
    private static readonly Brush NumberBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF7)));
    private static readonly Brush NoDataBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC8, 0xCE, 0xD6)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD0, 0xD5, 0xDC)));
    private static readonly Brush Edge = Freeze(new SolidColorBrush(Color.FromArgb(0xD0, 0, 0, 0)));
    private static readonly Typeface NumberFace = new(new FontFamily("Segoe UI Variable Display, Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Display, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static FrameworkElement Create(GaugeModel model, AppSettings settings)
    {
        var size = settings.GaugeSize;
        var edge = settings.DarkEdge;
        var c = size / 2;
        var w = size * 0.08;
        var r1 = c - w;
        var r2 = r1 - w * 1.6;
        var canvas = new Canvas { Width = size, Height = size };

        if (model.State == GaugeState.NoData)
        {
            var thickness = w * 0.6;
            if (edge) canvas.Children.Add(Dashed(Arc(c, r1, 1, thickness + EdgeWidth * 2, Edge), thickness + EdgeWidth * 2));
            canvas.Children.Add(Dashed(Arc(c, r1, 1, thickness, DashedTrack), thickness));
        }
        else
        {
            if (edge) canvas.Children.Add(Arc(c, r1, 1, w + EdgeWidth * 2, Edge));
            canvas.Children.Add(Arc(c, r1, 1, w, Track));
            if (model.Outer is { } outer && outer > 0)
                canvas.Children.Add(Arc(c, r1, outer / 100, w, ColorFor(outer, settings)));
            if (model.Inner is { } inner)
            {
                if (edge) canvas.Children.Add(Arc(c, r2, 1, w * 0.8 + EdgeWidth * 2, Edge));
                canvas.Children.Add(Arc(c, r2, 1, w * 0.8, Track));
                if (inner > 0) canvas.Children.Add(Arc(c, r2, inner / 100, w * 0.8, ColorFor(inner, settings)));
            }
        }

        var numberSize = Math.Round(size * 0.25);
        var labelSize = Math.Max(MinLabelSize, Math.Round(size * 0.17));
        var number = model.Center is { } pct ? Math.Round(pct).ToString("0", CultureInfo.InvariantCulture) : "–";
        var numberGeo = TextGeometry(number, NumberFace, numberSize);
        var labelGeo = TextGeometry(ProviderInfo.Abbreviation(model.Provider), LabelFace, labelSize);

        // Number's ink centered slightly above the middle; label just below it, in the arc's gap.
        Place(numberGeo, c, c - numberSize * 0.2, centerVertically: true);
        Place(labelGeo, c, numberGeo.Bounds.Bottom + Math.Max(2, numberSize * 0.22), centerVertically: false);

        AddText(canvas, numberGeo, model.State == GaugeState.NoData ? NoDataBrush : NumberBrush, edge);
        AddText(canvas, labelGeo, LabelBrush, edge);

        if (model.State == GaugeState.Stale) canvas.Opacity = 0.45;
        return canvas;
    }

    private static Brush ColorFor(double pct, AppSettings s) =>
        !s.ColorByUsage ? Teal : pct >= s.RedAt ? Red : pct >= s.AmberAt ? Amber : Teal;

    private static System.Windows.Shapes.Path Arc(double c, double r, double fraction, double thickness, Brush stroke)
    {
        var sweep = TotalSweep * Math.Clamp(fraction, 0.001, 1);
        var start = Point(c, r, StartAngle);
        var end = Point(c, r, StartAngle + sweep);
        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return new System.Windows.Shapes.Path
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
    }

    private static System.Windows.Shapes.Path Dashed(System.Windows.Shapes.Path arc, double thickness)
    {
        // Dash lengths are in multiples of the stroke thickness; keep the pattern the same
        // for the outline and the dashes so they line up: 3px dash, 4px gap at the dash width.
        var unit = thickness;
        arc.StrokeDashArray = [3 / unit, 4 / unit];
        arc.StrokeDashCap = PenLineCap.Round;
        return arc;
    }

    private static Point Point(double c, double r, double degrees)
    {
        var a = degrees * Math.PI / 180;
        return new Point(c + r * Math.Cos(a), c + r * Math.Sin(a));
    }

    private static Geometry TextGeometry(string text, Typeface face, double size)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size,
            Brushes.White, pixelsPerDip: 1.0);
        return ft.BuildGeometry(new Point(0, 0));
    }

    /// <summary>Moves the geometry so its ink is centered on x and centered on / starts at y.</summary>
    private static void Place(Geometry g, double x, double y, bool centerVertically)
    {
        var b = g.Bounds;
        var dy = centerVertically ? y - (b.Top + b.Height / 2) : y - b.Top;
        g.Transform = new TranslateTransform(x - (b.Left + b.Width / 2), dy);
    }

    private static void AddText(Canvas canvas, Geometry geometry, Brush fill, bool edge)
    {
        if (edge)
        {
            // Stroke underneath, fill on top: only the outer half of the stroke shows.
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geometry,
                Stroke = Edge,
                StrokeThickness = EdgeWidth * 2,
                StrokeLineJoin = PenLineJoin.Round,
            });
        }
        canvas.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Fill = fill });
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}
