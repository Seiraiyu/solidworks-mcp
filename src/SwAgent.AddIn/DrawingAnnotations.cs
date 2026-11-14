using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// Reads a drawing's annotations as results report them (design 611, Dimensions and annotations), through the seam: every
/// sheet's own and every view's, each with its reference, its dimension named as the model names it and said to be the
/// model's or the drawing's, and the model entities it is attached to as references issued in its view's model.
/// </summary>
public static class DrawingAnnotations
{
    /// <summary>Every annotation of the drawing, sheet by sheet: the sheet's own, then each view's in SOLIDWORKS' order.</summary>
    public static List<AnnotationFacts> All(ISwDrawing drawing)
    {
        var all = new List<AnnotationFacts>();
        foreach (var sheet in drawing.SheetNames())
        {
            all.AddRange(drawing.Annotations(sheet, null));
            foreach (var view in drawing.ViewNames(sheet)) all.AddRange(drawing.Annotations(sheet, view));
        }
        return all;
    }

    /// <summary>How many annotations the drawing has, on every sheet and in every view.</summary>
    public static int Count(ISwDrawing drawing) => All(drawing).Count;

    /// <summary>The names of the drawing's sheets and views: a dimension whose name ends @ one of them is the drawing's own.</summary>
    public static List<string> Owners(ISwDrawing drawing) => drawing.SheetNames().Concat(drawing.SheetNames().SelectMany(drawing.ViewNames)).ToList();

    /// <summary>An annotation as results report it.</summary>
    public static AnnotationInfo Info(ReferenceService references, ISwDocument doc, ISwDrawing drawing, AnnotationFacts a, IReadOnlyList<string>? owners = null)
    {
        bool dimension = a.Kind == AnnotationKind.Dimension && a.DimensionName != null;
        bool model = dimension && AnnotationGeometry.IsModelDimension(a.DimensionName!, owners ?? Owners(drawing));
        var info = new AnnotationInfo
        {
            Name = a.Name,
            Reference = references.IssueAnnotation(doc, a),
            Kind = a.Kind,
            Sheet = a.Sheet,
            View = a.View,
            Position = a.Position == null ? null : new SheetPoint(a.Position.X, a.Position.Y),
            Text = a.Text,
            LinkedText = a.LinkedText,
            DimensionKind = a.DimensionKind,
            Value = a.Value,
            DimensionName = dimension ? (model ? SolidWorksCodes.DimensionName(a.DimensionName!) : a.DimensionName) : null,
            ModelDimension = dimension ? model : (bool?)null,
            Label = a.Label,
            Gtol = a.Gtol,
        };
        var modelDoc = a.View == null ? null : drawing.ModelOf(a.View);
        foreach (var e in a.Attached) info.Attached.Add(new ViewEntity { View = a.View ?? a.Sheet, Reference = modelDoc == null ? null : Reference(references, modelDoc, e) });
        return info;
    }

    /// <summary>The reference of an entity of a view's model, or null when it is not one, or SOLIDWORKS gives it no persistent id.</summary>
    static string? Reference(ReferenceService references, ISwDocument model, object entity)
    {
        try
        {
            return model.KindOf(entity) == null ? null : references.Describe(model, entity).Reference;
        }
        catch (Exception e) when (e is SwAgentException || e is System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>The annotations in <paramref name="after"/> that <paramref name="before"/> did not have, by sheet, view and name.</summary>
    public static List<AnnotationFacts> New(IEnumerable<AnnotationFacts> before, IEnumerable<AnnotationFacts> after)
    {
        var had = new HashSet<string>(before.Select(Key), StringComparer.Ordinal);
        return after.Where(a => !had.Contains(Key(a))).ToList();
    }

    /// <summary>What names an annotation in its drawing: its sheet, view and name.</summary>
    public static string Key(AnnotationFacts a) => a.Sheet + "\n" + (a.View ?? "") + "\n" + a.Name;

    /// <summary>Where an annotation is, for messages: its view, or "sheet Sheet1".</summary>
    public static string Where(AnnotationFacts a) => a.View ?? "sheet " + a.Sheet;

    /// <summary>The annotation of that sheet, view and name, read now; null when it is gone.</summary>
    public static AnnotationFacts? Read(ISwDrawing drawing, string sheet, string? view, string name) =>
        drawing.Annotations(sheet, view).FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
}
