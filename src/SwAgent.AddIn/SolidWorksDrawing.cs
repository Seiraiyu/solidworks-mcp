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
            Format = SheetSetups.FormatOf(sheet.GetTemplateName()),
            Template = (int)p[1],
        };
    }

    public bool EditingSheetFormat() => !_drawing.GetEditSheet();

    public bool AddSheet(SheetFacts sheet)
    {
        int before = _drawing.GetSheetCount();
        // A template's own format has no file: the new sheet is made as the first sheet reads, its type and its "*.drt" (5.A review).
        var step = SheetSetups.Step(sheet);
        int template = step == SheetFormatStep.Remove ? (int)swDwgTemplates_e.swDwgTemplateNone
            : step == SheetFormatStep.Keep && sheet.Template is int own ? own : (int)swDwgTemplates_e.swDwgTemplateCustom;
        bool made = _drawing.NewSheet4(sheet.Name, PaperSize(sheet.WidthMm, sheet.HeightMm), template,
            sheet.Scale.Numerator, sheet.Scale.Denominator, sheet.Projection == Projection.FirstAngle, sheet.Format ?? "", sheet.WidthMm / Mm, sheet.HeightMm / Mm,
            "", 0, 0, 0, 0, 1, 1);
        return made && _drawing.GetSheetCount() > before;
    }

    public void SetSheet(string name, SheetFacts sheet)
    {
        var target = _drawing.get_Sheet(name) ?? throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS gives no sheet {name}");
        string format = target.GetTemplateName() ?? "";
        var step = SheetSetups.Step(sheet);
        bool hasFormat = step == SheetFormatStep.UseFile;
        // No format asked takes the format off; a template's own format ("*.drt") has no file to give, so the sheet keeps the
        // type it has: it keeps that format if it has it, and one that lost it stays as it is for the read-back to report (5.A review).
        int template = step == SheetFormatStep.UseFile ? (int)swDwgTemplates_e.swDwgTemplateCustom
            : step == SheetFormatStep.Remove ? (int)swDwgTemplates_e.swDwgTemplateNone
            : target.GetProperties2() is double[] p && p.Length > 1 ? (int)p[1] : (int)swDwgTemplates_e.swDwgTemplateCustom;
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

    /// <summary>
    /// The view's edges without its crosshatch (GetPolylines5's option 0): a section view's hatch lines did not read as
    /// edges in the 5.B live run, and lie inside the faces they fill, so they add nothing to what a view draws.
    /// </summary>
    public IReadOnlyList<double> Polylines(string view) =>
        View(view).GetPolylines5(0) is double[] numbers ? numbers : new double[0];

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
        // SOLIDWORKS selects a view by name only on the sheet it shows (5.B live run: a detail on Sheet1 was not
        // selected while Details was shown), so each sheet's views are deleted with that sheet shown. Showing a sheet is
        // not put back, as view.create does not.
        foreach (var onSheet in views.GroupBy(v => Find(v)?.Sheet?.GetName()))
        {
            if (onSheet.Key != null) ShowSheet(onSheet.Key);
            _doc.ClearSelection2(true);
            try
            {
                bool first = true;
                foreach (string view in onSheet)
                {
                    if (!_doc.Extension.SelectByID2(view, "DRAWINGVIEW", 0, 0, 0, !first, 0, null, 0)) return false;
                    first = false;
                }
                if (!_doc.Extension.DeleteSelection2(0)) return false;
            }
            finally
            {
                _doc.ClearSelection2(true);
            }
        }
        return true;
    }

    /// <summary>Shows a sheet when another is shown: SOLIDWORKS selects a sheet's views and what they hold only while it is shown.</summary>
    void ShowSheet(string sheet)
    {
        if (ActiveSheet() != sheet) _drawing.ActivateSheet(sheet);
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

    public string? CutSection(string parent, SheetPoint from, SheetPoint to, SheetPoint at, string label, bool reversed)
    {
        var line = DrawInView(parent, (sketch, position, scale) =>
        {
            var a = ViewCutGeometry.ToViewSketch(position, scale, from);
            var b = ViewCutGeometry.ToViewSketch(position, scale, to);
            return sketch.CreateLine(a.X / Mm, a.Y / Mm, 0, b.X / Mm, b.Y / Mm, 0);
        });
        if (line == null) return null;
        int options = reversed ? (int)swCreateSectionViewAtOptions_e.swCreateSectionView_ChangeDirection : 0;
        return MadeFrom(line, () => _drawing.CreateSectionViewAt5(at.X / Mm, at.Y / Mm, 0, label, options, null, 0));
    }

    public SectionFacts? ReadSection(string view) =>
        Find(view)?.GetSection() is IDrSection section
            ? new SectionFacts { Label = section.GetLabel() ?? "", Reversed = section.GetReversedCutDirection(), Line = section.GetName() ?? "" }
            : null;

    public IReadOnlyList<CutFace> CutFaces(string view)
    {
        var faces = new List<CutFace>();
        if (!(View(view).GetFaceHatches() is object[] hatches)) return faces;
        foreach (var hatch in hatches.OfType<IFaceHatch>())
        {
            if (!(hatch.Face is IFace2 face))
            {
                faces.Add(new CutFace(null, null, 0));
                continue;
            }
            bool planar = face.GetSurface() is ISurface surface && surface.IsPlane();
            var n = face.Normal as double[];
            var p = face.GetClosestPointOn(0, 0, 0) as double[];
            faces.Add(new CutFace(
                planar && n != null && n.Length >= 3 ? new Vector3(n[0], n[1], n[2]) : null,
                p != null && p.Length >= 3 ? new Point3(p[0] * Mm, p[1] * Mm, p[2] * Mm) : null,
                face.GetArea() * Mm * Mm));
        }
        return faces;
    }

    public IReadOnlyList<string> CutLabels(string view)
    {
        var v = Find(view);
        if (v == null) return new List<string>();
        var labels = Sections(v).Select(s => s.GetLabel() ?? "").ToList();
        labels.AddRange(Details(v).Select(d => d.GetLabel() ?? ""));
        return labels;
    }

    public bool DeleteSectionLine(string parent, string label)
    {
        var v = Find(parent);
        var line = v == null ? null : Sections(v).FirstOrDefault(s => string.Equals(s.GetLabel(), label, StringComparison.OrdinalIgnoreCase));
        if (line == null) return true;
        if (v!.Sheet?.GetName() is string sheet) ShowSheet(sheet);
        _doc.ClearSelection2(true);
        try
        {
            return DeleteNamed(line.GetName(), "SECTIONLINE", parent);
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    /// <summary>
    /// Selects a drawing item by its name and type (SelectByID2), as SOLIDWORKS names it and then qualified by its view
    /// (name@view), and deletes the selection. False when neither selects or the delete is refused.
    /// </summary>
    bool DeleteNamed(string? name, string type, string view)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (string id in name!.Contains("@") ? new[] { name } : new[] { name, name + "@" + view })
        {
            _doc.ClearSelection2(true);
            if (_doc.Extension.SelectByID2(id, type, 0, 0, 0, false, 0, null, 0)) return _doc.Extension.DeleteSelection2(0);
        }
        return false;
    }

    public string? DetailView(string parent, SheetPoint centre, double radiusMm, SheetPoint at, ScaleRatio scale, string label)
    {
        var circle = DrawInView(parent, (sketch, position, viewScale) =>
        {
            var c = ViewCutGeometry.ToViewSketch(position, viewScale, centre);
            return sketch.CreateCircleByRadius(c.X / Mm, c.Y / Mm, 0, radiusMm / viewScale / Mm);
        });
        if (circle == null) return null;
        return MadeFrom(circle, () => _drawing.CreateDetailViewAt4(at.X / Mm, at.Y / Mm, 0, (int)swDetViewStyle_e.swDetViewSTANDARD, scale.Numerator, scale.Denominator, label,
            (int)swDetCircleShowType_e.swDetCircleCIRCLE, true, false, false, 3) as IView);
    }

    public DetailFacts? ReadDetail(string view) =>
        Find(view)?.GetDetail() is IDetailCircle circle ? new DetailFacts { Label = circle.GetLabel() ?? "", Circle = circle.GetName() ?? "" } : null;

    public bool DeleteDetailCircle(string parent, string label)
    {
        var v = Find(parent);
        var circle = v == null ? null : Details(v).FirstOrDefault(d => string.Equals(d.GetLabel(), label, StringComparison.OrdinalIgnoreCase));
        if (circle == null) return true;
        if (v!.Sheet?.GetName() is string sheet) ShowSheet(sheet);
        _doc.ClearSelection2(true);
        try
        {
            // IDetailCircle.Select, then by its name (5.B live run: the first left the circle in the parent).
            if (circle.Select(false, null) && _doc.Extension.DeleteSelection2(0) && !Details(v!).Any(d => string.Equals(d.GetLabel(), label, StringComparison.OrdinalIgnoreCase)))
                return true;
            return DeleteNamed(circle.GetName(), "DETAILCIRCLE", parent);
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    public string? FlatPatternView(string modelPath, string configuration, SheetPoint at)
    {
        var view = _drawing.CreateFlatPatternViewFromModelView3(modelPath, configuration, at.X / Mm, at.Y / Mm, 0, false, false);
        _doc.ClearSelection2(true);
        return view?.GetName2();
    }

    public int BendLines(string view) => View(view).GetBendLineCount();

    public bool BendNotes(string view) => View(view).ShowSheetMetalBendNotes;

    public void SetBendNotes(string view, bool show)
    {
        View(view).ShowSheetMetalBendNotes = show;
        _doc.GraphicsRedraw2();
    }

    static IEnumerable<IDrSection> Sections(IView view) => view.GetSectionLines() is object[] lines ? lines.OfType<IDrSection>() : Enumerable.Empty<IDrSection>();

    static IEnumerable<IDetailCircle> Details(IView view) => view.GetDetailCircles() is object[] circles ? circles.OfType<IDetailCircle>() : Enumerable.Empty<IDetailCircle>();

    /// <summary>
    /// Draws one sketch segment in a view's own sketch: the view activated, its position (sheet mm) and scale given to
    /// <paramref name="draw"/>, with AddToDB on so the segment lands exactly where it is put rather than snapping to what
    /// the view shows, and AddToDB put back. Null when the view could not be activated or nothing was drawn.
    /// </summary>
    ISketchSegment? DrawInView(string view, Func<ISketchManager, SheetPoint, double, SketchSegment?> draw)
    {
        var v = View(view);
        if (!_drawing.ActivateView(view)) return null;
        var position = v.Position as double[];
        var ratio = v.ScaleRatio as double[];
        if (position == null || position.Length < 2) return null;
        double scale = ratio != null && ratio.Length >= 2 && ratio[1] != 0 ? ratio[0] / ratio[1] : v.ScaleDecimal;
        var sketch = _doc.SketchManager;
        bool addToDb = sketch.AddToDB;
        sketch.AddToDB = true;
        try
        {
            _doc.ClearSelection2(true);
            return draw(sketch, new SheetPoint(position[0] * Mm, position[1] * Mm), scale);
        }
        finally
        {
            sketch.AddToDB = addToDb;
        }
    }

    /// <summary>Selects the segment just drawn and makes a view from it; when SOLIDWORKS makes none, the segment is deleted again.</summary>
    string? MadeFrom(ISketchSegment segment, Func<IView?> make)
    {
        try
        {
            _doc.ClearSelection2(true);
            var made = segment.Select4(false, null) ? make() : null;
            if (made != null) return made.GetName2();
            _doc.ClearSelection2(true);
            if (segment.Select4(false, null)) _doc.Extension.DeleteSelection2(0);
            return null;
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
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
