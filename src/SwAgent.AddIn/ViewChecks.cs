using System;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// How a view is checked (design 611, Views): its scale and orientation read from its model-to-sheet transform, and what
/// it draws (its edges, IView.GetPolylines5) against its model's precise bounding box projected through that transform
/// and scaled: equal within 0.01 mm for a view that faces one of the six orthographic directions, inside it (and not
/// empty) for any other, since a box's projection is larger than a curved part's silhouette in a pictorial view. Each
/// failure is verification_failed naming the check, what was expected and what was measured.
/// </summary>
public static class ViewChecks
{
    /// <summary>The transform's scale equals <paramref name="scale"/> exactly (within 1e-9 of it).</summary>
    public static ViewTransform Scale(ISwDrawing drawing, string view, double scale)
    {
        var t = drawing.Transform(view) ?? throw Failed(view, "transform", "a model-to-sheet transform", "none");
        if (Math.Abs(t.Scale - scale) > 1e-9 * Math.Max(1, scale))
            throw Failed(view, "scale", Invariant(scale), Invariant(t.Scale));
        return t;
    }

    /// <summary>
    /// The view is drawn at <paramref name="scale"/>, faces <paramref name="orientation"/> (when given), and draws its
    /// model's box as design 611 checks it. Returns what it draws.
    /// </summary>
    public static SheetBox Drawn(ISwDrawing drawing, string view, double scale, string? orientation)
    {
        var facts = drawing.ReadView(view) ?? throw Failed(view, "exists", "the view on its sheet", "no such view");
        var t = Scale(drawing, view, scale);
        if (orientation != null && ViewGeometry.OrientationMismatch(orientation, t) is string why)
            throw Failed(view, "orientation", orientation, why);
        var drawn = DrawingReader.Extents(drawing, facts) ?? throw Failed(view, "extents", "edges drawn", "nothing drawn");
        var box = drawing.ModelOf(view)?.BoundingBox(precise: true);
        if (box == null) return drawn;
        var projected = ViewGeometry.Project(t, box);
        string? facing = ViewGeometry.Orientation(ViewGeometry.Facing(t));
        bool orthographic = facing != null && facing != "isometric";
        if (orthographic && !ViewGeometry.SameBox(projected, drawn))
        {
            string what = ViewGeometry.SameSize(projected, drawn) ? "the right size but not where its transform puts the model" : "not the model's box projected and scaled";
            throw Failed(view, "extents", projected.ToString(), $"{drawn} ({what})");
        }
        if (!orthographic && !ViewGeometry.Within(drawn, projected))
            throw Failed(view, "extents", $"within {projected}", drawn.ToString());
        return drawn;
    }

    static SwAgentException Failed(string view, string check, string expected, string measured) =>
        new SwAgentException(ErrorCode.VerificationFailed,
            $"the view {view} failed its {check} check: expected {expected}, measured {measured}",
            new JsonObject { ["check"] = check, ["view"] = view, ["expected"] = expected, ["measured"] = measured });

    static string Invariant(double value) => value.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture);
}
