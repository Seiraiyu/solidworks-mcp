using System;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// Reads a drawing's sheets and views as results report them (design 611, Sheets and Views), through the seam: sheet
/// values in sheet mm with the standard size they match, each view with its reference and its drawn extents (its edges
/// as drawn, IView.GetPolylines5, not its border).
/// </summary>
public static class DrawingReader
{
    /// <summary>A sheet as results report it, with its views when <paramref name="withViews"/>.</summary>
    public static SheetInfo Sheet(ReferenceService references, ISwDocument doc, ISwDrawing drawing, SheetFacts facts, bool withViews)
    {
        var info = new SheetInfo
        {
            Name = facts.Name,
            Reference = references.IssueSheet(doc, facts.Name),
            Size = SheetSizes.Match(facts.WidthMm, facts.HeightMm),
            Orientation = SheetSizes.OrientationOf(facts.WidthMm, facts.HeightMm),
            WidthMm = facts.WidthMm,
            HeightMm = facts.HeightMm,
            Scale = new ScaleRatio(facts.Scale.Numerator, facts.Scale.Denominator),
            Projection = facts.Projection,
            Format = facts.Format,
            Active = string.Equals(drawing.ActiveSheet(), facts.Name, StringComparison.Ordinal),
        };
        if (withViews)
        {
            foreach (var name in drawing.ViewNames(facts.Name))
            {
                if (drawing.ReadView(name) is ViewFacts view) info.Views.Add(View(references, doc, drawing, view));
            }
        }
        return info;
    }

    /// <summary>A view as results report it: its facts, its reference and its drawn extents.</summary>
    public static ViewInfo View(ReferenceService references, ISwDocument doc, ISwDrawing drawing, ViewFacts facts) => new ViewInfo
    {
        Name = facts.Name,
        Reference = references.IssueView(doc, facts),
        Sheet = facts.Sheet,
        Type = facts.Type,
        Model = facts.Model,
        Configuration = facts.Configuration,
        Orientation = facts.Orientation,
        Parent = facts.Parent,
        Position = new SheetPoint(facts.Position.X, facts.Position.Y),
        Scale = new ScaleRatio(facts.Scale.Numerator, facts.Scale.Denominator),
        SheetScale = facts.SheetScale,
        Display = facts.Display,
        Exploded = facts.Exploded,
        Extents = Extents(drawing, facts),
        Annotations = facts.Annotations,
        Tables = facts.Tables,
    };

    /// <summary>
    /// The box around what a view draws, sheet mm: its polylines placed at its position and scale (ViewGeometry.DrawnExtents);
    /// null when it draws nothing. api_error when SOLIDWORKS' numbers do not read as polylines.
    /// </summary>
    public static SheetBox? Extents(ISwDrawing drawing, ViewFacts facts)
    {
        try
        {
            return ViewGeometry.DrawnExtents(drawing.Polylines(facts.Name), facts.Position, Ratio(facts.Scale));
        }
        catch (FormatException e)
        {
            throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS' polylines for {facts.Name} do not read as edges: {e.Message}",
                new JsonObject { ["view"] = facts.Name });
        }
    }

    /// <summary>Every point a view draws, sheet mm (ViewGeometry.DrawnPoints); api_error when SOLIDWORKS' numbers do not read as polylines.</summary>
    public static System.Collections.Generic.List<SheetPoint> Points(ISwDrawing drawing, ViewFacts facts)
    {
        try
        {
            return ViewGeometry.DrawnPoints(drawing.Polylines(facts.Name), facts.Position, Ratio(facts.Scale));
        }
        catch (FormatException e)
        {
            throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS' polylines for {facts.Name} do not read as edges: {e.Message}",
                new JsonObject { ["view"] = facts.Name });
        }
    }

    /// <summary>A scale as a number: 1:2 is 0.5.</summary>
    public static double Ratio(ScaleRatio scale) => scale.Numerator / scale.Denominator;

    /// <summary>Two scales draw the same size (1:2 and 2:4 alike), within 1e-9.</summary>
    public static bool SameScale(ScaleRatio a, ScaleRatio b) => Math.Abs(Ratio(a) - Ratio(b)) <= 1e-9 * Math.Max(1, Ratio(b));

    /// <summary>How many views the drawing has, every sheet's.</summary>
    public static int ViewCount(ISwDrawing drawing) => drawing.SheetNames().Sum(s => drawing.ViewNames(s).Count);
}
