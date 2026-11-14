using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// The real drawing, through SOLIDWORKS' IDrawingDoc, ISheet and IView. UI thread only. Converts SOLIDWORKS' metres to
/// millimetres at this boundary and nowhere else (with ViewTransform.FromArrayData and ViewGeometry.DrawnExtents, which
/// read SOLIDWORKS' arrays). Sheets and views are selected by name, never passed to SOLIDWORKS in an array. Covered by the
/// drawings live suite, not by unit tests.
/// </summary>
internal sealed class SolidWorksDrawing : ISwDrawing
{
    const double Mm = 1000.0;

    readonly ISldWorks _sw;
    readonly IModelDoc2 _doc;
    readonly IDrawingDoc _drawing;

    public SolidWorksDrawing(ISldWorks sw, IModelDoc2 doc)
    {
        _sw = sw;
        _doc = doc;
        _drawing = (IDrawingDoc)doc;
    }

    public IReadOnlyList<string> SheetNames() =>
        _drawing.GetSheetNames() is object[] names ? names.OfType<string>().ToList() : new List<string>();

    public string? ActiveSheet() => (_drawing.GetCurrentSheet() as ISheet)?.GetName();

    public SheetFacts? ReadSheet(string name)
    {
        if (!SheetNames().Contains(name, StringComparer.Ordinal)) return null;
        var sheet = _drawing.get_Sheet(name);
        if (sheet == null || !(sheet.GetProperties2() is double[] p) || p.Length < 7) return null;
        return new SheetFacts
        {
            Name = sheet.GetName(),
            WidthMm = p[5] * Mm,
            HeightMm = p[6] * Mm,
            Scale = new ScaleRatio(p[2], p[3]),
            Projection = p[4] != 0 ? Projection.FirstAngle : Projection.ThirdAngle,
            // The default template's sheet names its format "*.drt": held in the template, no file (5.A live run).
            Format = SheetSetups.FormatFile(sheet.GetTemplateName()),
        };
    }

    public bool EditingSheetFormat() => !_drawing.GetEditSheet();

    public bool AddSheet(SheetFacts sheet)
    {
        int before = _drawing.GetSheetCount();
        bool made = _drawing.NewSheet4(sheet.Name, PaperSize(sheet.WidthMm, sheet.HeightMm), sheet.Format == null ? (int)swDwgTemplates_e.swDwgTemplateNone : (int)swDwgTemplates_e.swDwgTemplateCustom,
            sheet.Scale.Numerator, sheet.Scale.Denominator, sheet.Projection == Projection.FirstAngle, sheet.Format ?? "", sheet.WidthMm / Mm, sheet.HeightMm / Mm,
            "", 0, 0, 0, 0, 1, 1);
        return made && _drawing.GetSheetCount() > before;
    }

    public void SetSheet(string name, SheetFacts sheet)
    {
        var target = _drawing.get_Sheet(name) ?? throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS gives no sheet {name}");
        string format = target.GetTemplateName() ?? "";
        bool hasFormat = sheet.Format != null;
        // With no format file asked, the sheet keeps the format it has (a template's own one has no file: 5.A live run).
        int template = hasFormat ? (int)swDwgTemplates_e.swDwgTemplateCustom
            : target.GetProperties2() is double[] p && p.Length > 1 ? (int)p[1] : (int)swDwgTemplates_e.swDwgTemplateNone;
        target.SetProperties2(PaperSize(sheet.WidthMm, sheet.HeightMm), template,
            sheet.Scale.Numerator, sheet.Scale.Denominator, sheet.Projection == Projection.FirstAngle, sheet.WidthMm / Mm, sheet.HeightMm / Mm, true);
        // CADapter measured SetupSheet5 leaving the scale as it was; ISheet.SetScale applies it (design 611, Sheets).
        target.SetScale(sheet.Scale.Numerator, sheet.Scale.Denominator, false, false);
        if (hasFormat && !string.Equals(format, sheet.Format, StringComparison.OrdinalIgnoreCase))
        {
            target.SetTemplateName(sheet.Format);
            target.ReloadTemplate(true);
        }
        if (!string.Equals(target.GetName(), sheet.Name, StringComparison.Ordinal) && sheet.Name.Length > 0) target.SetName(sheet.Name);
        _doc.GraphicsRedraw2();
    }

    public bool ActivateSheet(string name) => _drawing.ActivateSheet(name);

    public bool DeleteSheet(string name)
    {
        _doc.ClearSelection2(true);
        try
        {
            if (!_doc.Extension.SelectByID2(name, "SHEET", 0, 0, 0, false, 0, null, 0)) return false;
            return _doc.Extension.DeleteSelection2(0);
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    public IReadOnlyList<string> ViewNames(string sheet)
    {
        if (!SheetNames().Contains(sheet, StringComparer.Ordinal)) return new List<string>();
        return _drawing.get_Sheet(sheet)?.GetViews() is object[] views ? views.OfType<IView>().Select(v => v.GetName2()).ToList() : new List<string>();
    }

    /// <summary>Every view of every sheet (each sheet's own view left out), as GetViews lists them: sheet view first, then its views.</summary>
    IEnumerable<IView> AllViews()
    {
        if (!(_drawing.GetViews() is object[] sheets)) yield break;
        foreach (var s in sheets)
        {
            if (!(s is object[] views)) continue;
            foreach (var v in views.Skip(1).OfType<IView>()) yield return v;
        }
    }

    IView? Find(string name) => AllViews().FirstOrDefault(v => string.Equals(v.GetName2(), name, StringComparison.Ordinal));

    IView View(string name) => Find(name) ?? throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS gives no drawing view {name}");

    public ViewFacts? ReadView(string name)
    {
        var view = Find(name);
        if (view == null) return null;
        var position = view.Position as double[];
        var scale = view.ScaleRatio as double[];
        var model = view.ReferencedDocument;
        string modelPath = model?.GetPathName() ?? view.GetReferencedModelName() ?? "";
        string configuration = view.ReferencedConfiguration ?? "";
        return new ViewFacts
        {
            Name = view.GetName2(),
            Sheet = view.Sheet?.GetName() ?? "",
            Type = TypeOf(view.Type, view.IsFlatPatternView()),
            Model = modelPath.Length == 0 ? null : modelPath,
            Configuration = configuration.Length == 0 ? null : configuration,
            Orientation = OrientationOf(view.GetOrientationName()),
            Parent = (view.GetBaseView() as IView)?.GetName2(),
            Position = position != null && position.Length >= 2 ? new SheetPoint(position[0] * Mm, position[1] * Mm) : new SheetPoint(),
            Scale = scale != null && scale.Length >= 2 ? new ScaleRatio(scale[0], scale[1]) : new ScaleRatio(view.ScaleDecimal, 1),
            SheetScale = view.UseSheetScale == 1,
            Display = DisplayOf(view.GetDisplayMode2()),
            Exploded = view.IsExploded(),
            Annotations = view.GetAnnotationCount(),
            Tables = view.GetTableAnnotationCount(),
        };
    }

    public ViewTransform? Transform(string view) =>
        ViewTransform.FromArrayData((View(view).ModelToViewTransform?.ArrayData as double[])?.ToList());

    public IReadOnlyList<double> Polylines(string view) =>
        View(view).GetPolylines5(1) is double[] numbers ? numbers : new double[0];

    public string? CreateModelView(string modelPath, string orientation, SheetPoint at)
    {
        var view = _drawing.CreateDrawViewFromModelView3(modelPath, SolidWorksOrientation(orientation), at.X / Mm, at.Y / Mm, 0);
        _doc.ClearSelection2(true);
        return view?.GetName2();
    }

    public string? ProjectView(string parent, SheetPoint at)
    {
        _doc.ClearSelection2(true);
        try
        {
            if (!_doc.Extension.SelectByID2(parent, "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0)) return null;
            return _drawing.CreateUnfoldedViewAt3(at.X / Mm, at.Y / Mm, 0, false)?.GetName2();
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    public void SetPosition(string view, SheetPoint at)
    {
        View(view).Position = new[] { at.X / Mm, at.Y / Mm };
        _doc.GraphicsRedraw2();
    }

    public void SetScale(string view, ScaleRatio? scale)
    {
        var v = View(view);
        if (scale == null)
        {
            v.UseSheetScale = 1;
        }
        else
        {
            v.UseSheetScale = 0;
            v.ScaleRatio = new[] { scale.Numerator, scale.Denominator };
        }
        _doc.GraphicsRedraw2();
    }

    public void SetDisplay(string view, ViewDisplay display)
    {
        var (mode, edges) = ModeOf(display);
        View(view).SetDisplayMode4(false, mode, false, edges, false);
        _doc.GraphicsRedraw2();
    }

    public void SetConfiguration(string view, string configuration)
    {
        View(view).ReferencedConfiguration = configuration;
        _doc.ForceRebuild3(false);
    }

    public bool DeleteViews(IReadOnlyList<string> views)
    {
        _doc.ClearSelection2(true);
        try
        {
            for (int i = 0; i < views.Count; i++)
            {
                if (!_doc.Extension.SelectByID2(views[i], "DRAWINGVIEW", 0, 0, 0, i > 0, 0, null, 0)) return false;
            }
            return _doc.Extension.DeleteSelection2(0);
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    public ISwDocument? ModelOf(string view) =>
        Find(view)?.ReferencedDocument is IModelDoc2 model ? new SolidWorksDocument(_sw, model) : null;

    public IReadOnlyList<VisibleEntity> VisibleEntities(string view, bool edges, bool vertices)
    {
        var v = View(view);
        var list = new List<VisibleEntity>();
        void Add(swViewEntityType_e type, bool vertex, bool silhouette)
        {
            if (!(v.GetVisibleEntities2(null, (int)type) is object[] found)) return;
            foreach (var o in found)
            {
                if (o != null && !list.Any(e => ReferenceEquals(e.Entity, o))) list.Add(new VisibleEntity(o, vertex, silhouette));
            }
        }
        if (edges)
        {
            Add(swViewEntityType_e.swViewEntityType_Edge, false, false);
            Add(swViewEntityType_e.swViewEntityType_SilhouetteEdge, false, true);
        }
        if (vertices) Add(swViewEntityType_e.swViewEntityType_Vertex, true, false);
        return list;
    }

    public IReadOnlyDictionary<string, bool> Settings() =>
        Toggles.ToDictionary(t => t.Key, t => _doc.Extension.GetUserPreferenceToggle((int)t.Value, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified));

    public void SetSetting(string name, bool value) =>
        _doc.Extension.SetUserPreferenceToggle((int)Toggles[name], (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified, value);

    /// <summary>DrawingSettings.All, as SOLIDWORKS' document toggles.</summary>
    internal static readonly IReadOnlyDictionary<string, swUserPreferenceToggle_e> Toggles = new Dictionary<string, swUserPreferenceToggle_e>(StringComparer.Ordinal)
    {
        ["display_origins"] = swUserPreferenceToggle_e.swDisplayOrigins,
        ["display_planes"] = swUserPreferenceToggle_e.swDisplayPlanes,
        ["display_axes"] = swUserPreferenceToggle_e.swDisplayAxes,
        ["display_temporary_axes"] = swUserPreferenceToggle_e.swDisplayTemporaryAxes,
        ["display_sketches"] = swUserPreferenceToggle_e.swDisplaySketches,
        ["display_reference_points"] = swUserPreferenceToggle_e.swDisplayReferencePoints,
        ["auto_insert_center_marks_holes"] = swUserPreferenceToggle_e.swDetailingAutoInsertCenterMarksForHoles,
        ["auto_insert_center_marks_fillets"] = swUserPreferenceToggle_e.swDetailingAutoInsertCenterMarksForFillets,
        ["auto_insert_center_marks_slots"] = swUserPreferenceToggle_e.swDetailingAutoInsertCenterMarksForSlots,
        ["auto_insert_center_lines"] = swUserPreferenceToggle_e.swDetailingAutoInsertCenterLines,
        ["auto_insert_balloons"] = swUserPreferenceToggle_e.swDetailingAutoInsertBalloons,
        ["auto_insert_dims_marked_for_drawing"] = swUserPreferenceToggle_e.swDetailingAutoInsertDimsMarkedForDrawing,
    };

    /// <summary>swDwgPaperSizes_e for a sheet of this size: the standard code when SOLIDWORKS has one for it, else user defined.</summary>
    internal static int PaperSize(double widthMm, double heightMm)
    {
        var size = SheetSizes.Match(widthMm, heightMm);
        bool portrait = SheetSizes.OrientationOf(widthMm, heightMm) == SheetOrientation.Portrait;
        var code = (size, portrait) switch
        {
            (SheetSize.A, false) => swDwgPaperSizes_e.swDwgPaperAsize,
            (SheetSize.A, true) => swDwgPaperSizes_e.swDwgPaperAsizeVertical,
            (SheetSize.B, false) => swDwgPaperSizes_e.swDwgPaperBsize,
            (SheetSize.C, false) => swDwgPaperSizes_e.swDwgPaperCsize,
            (SheetSize.D, false) => swDwgPaperSizes_e.swDwgPaperDsize,
            (SheetSize.E, false) => swDwgPaperSizes_e.swDwgPaperEsize,
            (SheetSize.A4, false) => swDwgPaperSizes_e.swDwgPaperA4size,
            (SheetSize.A4, true) => swDwgPaperSizes_e.swDwgPaperA4sizeVertical,
            (SheetSize.A3, false) => swDwgPaperSizes_e.swDwgPaperA3size,
            (SheetSize.A2, false) => swDwgPaperSizes_e.swDwgPaperA2size,
            (SheetSize.A1, false) => swDwgPaperSizes_e.swDwgPaperA1size,
            (SheetSize.A0, false) => swDwgPaperSizes_e.swDwgPaperA0size,
            _ => swDwgPaperSizes_e.swDwgPapersUserDefined,
        };
        return (int)code;
    }

    /// <summary>A view's type: swDrawingViewTypes_e, with a flat pattern recognised first.</summary>
    internal static ViewType TypeOf(int swType, bool flatPattern) => flatPattern ? ViewType.FlatPattern : swType switch
    {
        (int)swDrawingViewTypes_e.swDrawingStandardView => ViewType.Model,
        (int)swDrawingViewTypes_e.swDrawingNamedView => ViewType.Model,
        (int)swDrawingViewTypes_e.swDrawingProjectedView => ViewType.Projected,
        (int)swDrawingViewTypes_e.swDrawingSectionView => ViewType.Section,
        (int)swDrawingViewTypes_e.swDrawingDetailView => ViewType.Detail,
        _ => ViewType.Other,
    };

    /// <summary>swDisplayMode_e as design 611 names it (a faceted mode as its plain one); null for one it does not name.</summary>
    internal static ViewDisplay? DisplayOf(int mode) => mode switch
    {
        (int)swDisplayMode_e.swWIREFRAME or (int)swDisplayMode_e.swFACETED_WIREFRAME => ViewDisplay.Wireframe,
        (int)swDisplayMode_e.swHIDDEN_GREYED or (int)swDisplayMode_e.swFACETED_HIDDEN_GREYED => ViewDisplay.HiddenLinesVisible,
        (int)swDisplayMode_e.swHIDDEN or (int)swDisplayMode_e.swFACETED_HIDDEN => ViewDisplay.HiddenLinesRemoved,
        (int)swDisplayMode_e.swSHADED => ViewDisplay.Shaded,
        (int)swDisplayMode_e.swSHADED_EDGES => ViewDisplay.ShadedWithEdges,
        _ => null,
    };

    /// <summary>The swDisplayMode_e and edges flag SetDisplayMode4 takes for a display.</summary>
    internal static (int Mode, bool Edges) ModeOf(ViewDisplay display) => display switch
    {
        ViewDisplay.Wireframe => ((int)swDisplayMode_e.swWIREFRAME, false),
        ViewDisplay.HiddenLinesVisible => ((int)swDisplayMode_e.swHIDDEN_GREYED, false),
        ViewDisplay.HiddenLinesRemoved => ((int)swDisplayMode_e.swHIDDEN, false),
        ViewDisplay.Shaded => ((int)swDisplayMode_e.swSHADED, false),
        _ => ((int)swDisplayMode_e.swSHADED_EDGES, true),
    };

    /// <summary>SOLIDWORKS' orientation name as design 611 names it: *Front is front; a named view keeps its own name; empty is null.</summary>
    internal static string? OrientationOf(string? swName)
    {
        if (string.IsNullOrEmpty(swName)) return null;
        string plain = swName!.TrimStart('*').ToLowerInvariant();
        return ViewGeometry.Standard.Contains(plain) ? plain : swName;
    }

    /// <summary>The orientation name CreateDrawViewFromModelView3 takes: *Front for front; a named view as the model names it.</summary>
    internal static string SolidWorksOrientation(string orientation) =>
        ViewGeometry.Standard.Contains(orientation) ? "*" + char.ToUpperInvariant(orientation[0]) + orientation.Substring(1) : orientation;
}
