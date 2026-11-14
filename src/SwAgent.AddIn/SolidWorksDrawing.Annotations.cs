using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using GtolFrame = SwAgent.Contracts.GtolFrame;

namespace SwAgent.AddIn;

/// <summary>
/// The drawing's annotations and custom properties (design 611, Dimensions and annotations), through IView, IAnnotation
/// and its specific interfaces. Every entity an annotation attaches to is selected through its view (IView.SelectEntity),
/// never picked by sheet coordinates; positions are sheet metres in SOLIDWORKS and sheet mm here. No array of COM objects
/// is passed to SOLIDWORKS (IAnnotation.SetAttachedEntities is never called). Covered by the drawings live suite.
/// </summary>
internal sealed partial class SolidWorksDrawing
{
    static readonly IReadOnlyList<AnnotationFacts> NoAnnotations = new List<AnnotationFacts>();

    /// <summary>
    /// The view that holds a view's annotations, or a sheet's own: the sheet's own view is the first of its sheet's views
    /// as IDrawingDoc.GetViews lists them. Null when there is none.
    /// </summary>
    IView? Owner(string sheet, string? view)
    {
        if (view != null) return Find(view);
        if (!(_drawing.GetViews() is object[] sheets)) return null;
        foreach (var s in sheets)
        {
            if (s is object[] views && views.Length > 0 && views[0] is IView own && string.Equals(own.Sheet?.GetName() ?? own.GetName2(), sheet, StringComparison.Ordinal))
                return own;
        }
        return null;
    }

    IEnumerable<IAnnotation> AnnotationsOf(IView owner) =>
        owner.GetAnnotations() is object[] all ? all.OfType<IAnnotation>() : Enumerable.Empty<IAnnotation>();

    IAnnotation? FindAnnotation(string sheet, string? view, string name)
    {
        var owner = Owner(sheet, view);
        return owner == null ? null : AnnotationsOf(owner).FirstOrDefault(a => string.Equals(a.GetName(), name, StringComparison.Ordinal));
    }

    public IReadOnlyList<AnnotationFacts> Annotations(string sheet, string? view)
    {
        var owner = Owner(sheet, view);
        return owner == null ? NoAnnotations : AnnotationsOf(owner).Select(a => Read(a, sheet, view, view == null ? null : owner)).ToList();
    }

    static AnnotationFacts Read(IAnnotation a, string sheet, string? view, IView? inView)
    {
        var facts = new AnnotationFacts
        {
            Name = a.GetName() ?? "",
            Sheet = sheet,
            View = view,
            Kind = KindOf(a.GetType()),
            Position = a.GetPosition() is double[] p && p.Length >= 2 ? new SheetPoint(p[0] * Mm, p[1] * Mm) : null,
            Attached = a.GetAttachedEntities3() is object[] attached ? attached.Where(o => o != null).ToList() : new List<object>(),
        };
        switch (a.GetSpecificAnnotation())
        {
            case IDisplayDimension d:
                int type = d.GetType();
                facts.DimensionKind = DimensionKindOf(type);
                if (d.GetDimension2(0) is IDimension dim)
                {
                    facts.Value = dim.SystemValue * (type == (int)swDimensionType_e.swAngularDimension ? 180 / Math.PI : Mm);
                    facts.DimensionName = dim.FullName;
                    facts.Driven = dim.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriven;
                }
                break;
            case INote note:
                facts.Text = note.GetText();
                facts.LinkedText = note.PropertyLinkedText;
                break;
            case IDatumTag tag:
                facts.Label = tag.GetLabel();
                break;
            case IGtol gtol:
                facts.Gtol = FrameOf(gtol.GetFrameValues(1), gtol.GetFrameSymbols(1));
                break;
            case ICenterMark mark:
                // The 5.C live runs: IAnnotation.GetPosition put the hole's mark at (117.226, 175.84) for a centre at
                // (120, 180), and ICenterMark.GetPosition(0) gave (-10, 0): the view's own frame (model mm about the view's
                // position, as its sketch), so a centre mark in a view is placed by that, drawn onto the sheet; both are kept.
                var (index, raw) = MarkPosition(mark);
                var own = raw;
                if (own != null && inView != null && inView.Position is double[] at && at.Length >= 2)
                {
                    var ratio = inView.ScaleRatio as double[];
                    double scale = ratio != null && ratio.Length >= 2 && ratio[1] != 0 ? ratio[0] / ratio[1] : inView.ScaleDecimal;
                    own = ViewCutGeometry.FromViewSketch(new SheetPoint(at[0] * Mm, at[1] * Mm), scale, new Point2(own.X, own.Y));
                }
                facts.PositionNote = FormattableString.Invariant($"IAnnotation.GetPosition {facts.Position?.ToString() ?? "nothing"}; ICenterMark.GetPosition({index}) {raw?.ToString() ?? "nothing"}{(inView == null || own == null ? "" : $", drawn at {own}")}");
                if (own != null) facts.Position = own;
                break;
        }
        return facts;
    }

    /// <summary>A centre mark's own position, sheet mm (ICenterMark.GetPosition, index 0, else 1), with the index that gave it.</summary>
    static (int Index, SheetPoint? Position) MarkPosition(ICenterMark mark)
    {
        foreach (int index in new[] { 0, 1 })
        {
            try
            {
                if (mark.GetPosition(index) is double[] p && p.Length >= 2) return (index, new SheetPoint(p[0] * Mm, p[1] * Mm));
            }
            catch (COMException)
            {
            }
            catch (ArgumentException)
            {
            }
        }
        return (0, null);
    }

    /// <summary>
    /// A tolerance frame from what IGtol.GetFrameValues gives (tolerance 1, tolerance 2, datums 1 to 3, as text) and
    /// GetFrameSymbols (the numbers SetFrameSymbols takes: characteristic, diameter 1, condition 1, …).
    /// </summary>
    internal static GtolFrame FrameOf(object? values, object? symbols)
    {
        var text = values is Array v ? v.Cast<object?>().Select(o => o?.ToString() ?? "").ToList() : new List<string>();
        var numbers = symbols is Array s ? s.Cast<object?>().Select(Number).ToList() : new List<double?>();
        return new GtolFrame
        {
            Characteristic = numbers.Count > 0 && numbers[0] is double gcs ? CharacteristicOf((int)gcs) : null,
            ToleranceMm = text.Count > 0 ? AnnotationGeometry.ParseTolerance(text[0]) : null,
            Diameter = numbers.Count > 1 && numbers[1] is double dia && dia != 0,
            MaterialCondition = numbers.Count > 2 && numbers[2] is double mc ? ConditionOf((int)mc) : null,
            Datums = text.Skip(2).Take(3).Where(t => t.Trim().Length > 0).Select(t => t.Trim()).ToList(),
        };
    }

    static double? Number(object? o)
    {
        if (o is bool b) return b ? 1 : 0;
        try
        {
            return o == null ? (double?)null : Convert.ToDouble(o, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }

    public void ImportAnnotations(IReadOnlyList<AnnotationImportType> types)
    {
        _doc.ClearSelection2(true);
        _drawing.InsertModelAnnotations3((int)swImportModelItemsSource_e.swImportModelItemsFromEntireModel, ImportTypes(types), true, false, false, false);
        _doc.ClearSelection2(true);
    }

    /// <summary>Selects each entity through its view, the view's sheet shown; false when one is not selected.</summary>
    bool SelectThrough(IView view, IEnumerable<object> entities)
    {
        if (view.Sheet?.GetName() is string sheet) ShowSheet(sheet);
        _doc.ClearSelection2(true);
        bool first = true;
        foreach (var e in entities)
        {
            if (!view.SelectEntity(e, !first)) return false;
            first = false;
        }
        return true;
    }

    /// <summary>Makes one annotation from what <paramref name="select"/> selected, and gives its name; the selection is cleared either way.</summary>
    string? Made(Func<bool> select, Func<IAnnotation?> make)
    {
        try
        {
            return select() && make() is IAnnotation made ? made.GetName() : null;
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    public string? AddDimension(string view, DimensionKind kind, IReadOnlyList<object> entities, SheetPoint at)
    {
        var v = View(view);
        double x = at.X / Mm, y = at.Y / Mm;
        return Made(() => SelectThrough(v, entities), () =>
        {
            object? made = kind switch
            {
                DimensionKind.Horizontal => _doc.AddHorizontalDimension2(x, y, 0),
                DimensionKind.Vertical => _doc.AddVerticalDimension2(x, y, 0),
                DimensionKind.Radius => _doc.AddRadialDimension2(x, y, 0),
                DimensionKind.Diameter => _doc.AddDiameterDimension2(x, y, 0),
                _ => _doc.AddDimension2(x, y, 0),
            };
            return (made as IDisplayDimension)?.GetAnnotation() as IAnnotation;
        });
    }

    public string? AddNote(string sheet, string? view, string text, SheetPoint at, object? attachTo)
    {
        return Made(() =>
        {
            if (attachTo != null) return SelectThrough(View(view!), new[] { attachTo });
            ShowSheet(view == null ? sheet : View(view).Sheet?.GetName() ?? sheet);
            _doc.ClearSelection2(true);
            // A note goes into the active view; one for the sheet itself needs no view active (the sheet's own view).
            if (view != null) return Activate(view);
            var own = Owner(sheet, null);
            return !(_drawing.ActiveDrawingView is IView active) || own == null || string.Equals(active.GetName2(), own.GetName2(), StringComparison.Ordinal) || _drawing.ActivateView(own.GetName2());
        }, () =>
        {
            if (!(_doc.InsertNote(text) is INote note) || !(note.GetAnnotation() is IAnnotation a)) return null;
            a.SetPosition2(at.X / Mm, at.Y / Mm, 0);
            return a;
        });
    }

    public string? AddCentreMark(string view, object edge)
    {
        var v = View(view);
        return Made(() => SelectThrough(v, new[] { edge }),
            () => _drawing.InsertCenterMark3((int)swCenterMarkStyle_e.swCenterMark_Single, false, false)?.GetAnnotation());
    }

    public string? AddCentreLine(string view, IReadOnlyList<object> entities)
    {
        var v = View(view);
        return Made(() => SelectThrough(v, entities), () => _drawing.InsertCenterLine2()?.GetAnnotation());
    }

    public string? AddDatum(string view, object entity, string label, SheetPoint? at)
    {
        var v = View(view);
        return Made(() => SelectThrough(v, new[] { entity }), () =>
        {
            if (!(_doc.InsertDatumTag2() is IDatumTag tag) || !(tag.GetAnnotation() is IAnnotation a)) return null;
            tag.SetLabel(label);
            if (at != null) a.SetPosition2(at.X / Mm, at.Y / Mm, 0);
            return a;
        });
    }

    public string? AddGtol(string view, object entity, GtolFrame frame, SheetPoint? at)
    {
        var v = View(view);
        return Made(() => SelectThrough(v, new[] { entity }), () =>
        {
            if (!(_doc.InsertGtol() is IGtol gtol) || !(gtol.GetAnnotation() is IAnnotation a)) return null;
            // The numbers, not SetFrameSymbols2's strings, which CADapter measured wanting font codes (design 611).
            gtol.SetFrameSymbols(1, (short)Gcs(frame.Characteristic ?? GtolCharacteristic.Position), frame.Diameter, (short)Mc(frame.MaterialCondition ?? MaterialCondition.None), false, 0, 0, 0, 0);
            var d = frame.Datums;
            gtol.SetFrameValues2(1, AnnotationGeometry.ToleranceText(frame.ToleranceMm ?? 0), "", d.Count > 0 ? d[0] : "", d.Count > 1 ? d[1] : "", d.Count > 2 ? d[2] : "");
            if (at != null) a.SetPosition2(at.X / Mm, at.Y / Mm, 0);
            return a;
        });
    }

    public void MoveAnnotation(string sheet, string? view, string name, SheetPoint at)
    {
        var a = FindAnnotation(sheet, view, name) ?? throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS gives no annotation {name}");
        a.SetPosition2(at.X / Mm, at.Y / Mm, 0);
        _doc.GraphicsRedraw2();
    }

    public bool SetNoteText(string sheet, string? view, string name, string text)
    {
        if (!(FindAnnotation(sheet, view, name)?.GetSpecificAnnotation() is INote note)) return false;
        bool set = note.SetText(text);
        _doc.GraphicsRedraw2();
        return set;
    }

    public bool DeleteAnnotations(string sheet, string? view, IReadOnlyList<string> names)
    {
        // SOLIDWORKS selects what a sheet holds only while that sheet is shown (5.B live run).
        ShowSheet(sheet);
        _doc.ClearSelection2(true);
        try
        {
            bool first = true;
            foreach (string name in names)
            {
                var a = FindAnnotation(sheet, view, name);
                if (a == null || !a.Select3(!first, null)) return false;
                first = false;
            }
            _doc.EditDelete();
            return true;
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    public bool Shows(string view, object entity, object? component)
    {
        var v = View(view);
        foreach (var type in new[] { swViewEntityType_e.swViewEntityType_Edge, swViewEntityType_e.swViewEntityType_SilhouetteEdge, swViewEntityType_e.swViewEntityType_Vertex, swViewEntityType_e.swViewEntityType_Face })
        {
            if (v.GetVisibleEntities2(component as Component2, (int)type) is object[] found && found.Any(o => o != null && SameEntity(o, entity))) return true;
        }
        return false;
    }

    public IReadOnlyList<object> VisibleFaces(string view) =>
        View(view).GetVisibleEntities2(null, (int)swViewEntityType_e.swViewEntityType_Face) is object[] found ? found.Where(o => o != null).ToList() : new List<object>();

    public IReadOnlyList<object> ModelEntities(string view, object shown)
    {
        var v = View(view);
        var found = new List<object>();
        foreach (var map in new Func<object, object?>[] { o => v.GetCorresponding(o), o => v.GetCorrespondingEntity(o) })
        {
            object? own = Try(() => map(shown));
            if (own != null && !ReferenceEquals(own, shown) && !found.Any(f => ReferenceEquals(f, own))) found.Add(own);
        }
        return found;
    }

    public object? InView(string view, object own)
    {
        var v = View(view);
        var shown = new List<object>();
        foreach (int type in new[] { (int)swViewEntityType_e.swViewEntityType_Edge, (int)swViewEntityType_e.swViewEntityType_SilhouetteEdge, (int)swViewEntityType_e.swViewEntityType_Vertex, (int)swViewEntityType_e.swViewEntityType_Face })
        {
            if (v.GetVisibleEntities2(null, type) is object[] found) shown.AddRange(found.Where(o => o != null));
        }
        foreach (var map in new Func<object, object?>[] { o => v.GetCorrespondingEntity(o), o => v.GetCorresponding(o) })
        {
            if (Try(() => map(own)) is object drawn && shown.FirstOrDefault(s => SameEntity(s, drawn)) is object match) return match;
        }
        return null;
    }

    static object? Try(Func<object?> call)
    {
        try
        {
            return call();
        }
        catch (COMException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public bool SameEntity(object a, object b) => ReferenceEquals(a, b) || _sw.IsSame(a, b) == (int)swObjectEquality.swObjectSame;

    ICustomPropertyManager? Custom() => _doc.Extension.get_CustomPropertyManager("");

    public IReadOnlyList<PropertyFacts> Properties()
    {
        var list = new List<PropertyFacts>();
        var cpm = Custom();
        if (cpm == null || !(cpm.GetNames() is object[] names)) return list;
        foreach (string name in names.OfType<string>())
        {
            // Get2 resolves linked values where Get5 does not (CADapter, design 454).
            cpm.Get2(name, out string value, out string resolved);
            list.Add(new PropertyFacts(name, value ?? "", resolved ?? ""));
        }
        return list;
    }

    public bool SetProperty(string name, string value)
    {
        var cpm = Custom();
        if (cpm == null) return false;
        bool has = cpm.GetNames() is object[] names && names.OfType<string>().Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        return has
            ? cpm.Set2(name, value) == (int)swCustomInfoSetResult_e.swCustomInfoSetResult_OK
            : cpm.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue) == (int)swCustomInfoAddResult_e.swCustomInfoAddResult_AddedOrChanged;
    }

    public bool DeleteProperty(string name) => Custom()?.Delete2(name) == (int)swCustomInfoDeleteResult_e.swCustomInfoDeleteResult_OK;

    /// <summary>swAnnotationType_e as the add-in names it.</summary>
    internal static AnnotationKind KindOf(int swType) => swType switch
    {
        (int)swAnnotationType_e.swDisplayDimension => AnnotationKind.Dimension,
        (int)swAnnotationType_e.swNote => AnnotationKind.Note,
        (int)swAnnotationType_e.swCenterMarkSym => AnnotationKind.CentreMark,
        (int)swAnnotationType_e.swCenterLine => AnnotationKind.CentreLine,
        (int)swAnnotationType_e.swDatumTag => AnnotationKind.Datum,
        (int)swAnnotationType_e.swGTol => AnnotationKind.Gtol,
        _ => AnnotationKind.Other,
    };

    /// <summary>swDimensionType_e as design 611 names it; null for a kind it does not name.</summary>
    internal static DimensionKind? DimensionKindOf(int swType) => swType switch
    {
        (int)swDimensionType_e.swLinearDimension => DimensionKind.Linear,
        (int)swDimensionType_e.swHorLinearDimension => DimensionKind.Horizontal,
        (int)swDimensionType_e.swVertLinearDimension => DimensionKind.Vertical,
        (int)swDimensionType_e.swRadialDimension => DimensionKind.Radius,
        (int)swDimensionType_e.swDiameterDimension => DimensionKind.Diameter,
        (int)swDimensionType_e.swAngularDimension => DimensionKind.Angle,
        _ => null,
    };

    /// <summary>The swInsertAnnotation_e flags InsertModelAnnotations3 takes for these types (dimensions: marked for drawing or not).</summary>
    internal static int ImportTypes(IEnumerable<AnnotationImportType> types) => types.Aggregate(0, (flags, t) => flags | t switch
    {
        AnnotationImportType.Dimensions => (int)swInsertAnnotation_e.swInsertDimensionsMarkedForDrawing | (int)swInsertAnnotation_e.swInsertDimensionsNotMarkedForDrawing,
        AnnotationImportType.HoleCallouts => (int)swInsertAnnotation_e.swInsertholeCallout,
        AnnotationImportType.Datums => (int)swInsertAnnotation_e.swInsertDatums,
        AnnotationImportType.Gtols => (int)swInsertAnnotation_e.swInsertGTols,
        _ => (int)swInsertAnnotation_e.swInsertNotes,
    });

    static readonly IReadOnlyDictionary<GtolCharacteristic, swGtolGeomCharSymbol_e> Characteristics = new Dictionary<GtolCharacteristic, swGtolGeomCharSymbol_e>
    {
        [GtolCharacteristic.Straightness] = swGtolGeomCharSymbol_e.swGcsSTRAIGHT,
        [GtolCharacteristic.Flatness] = swGtolGeomCharSymbol_e.swGcsFLAT,
        [GtolCharacteristic.Circularity] = swGtolGeomCharSymbol_e.swGcsROUND,
        [GtolCharacteristic.Cylindricity] = swGtolGeomCharSymbol_e.swGcsCYL,
        [GtolCharacteristic.ProfileOfALine] = swGtolGeomCharSymbol_e.swGcsLINEPROF,
        [GtolCharacteristic.ProfileOfASurface] = swGtolGeomCharSymbol_e.swGcsSURFPROF,
        [GtolCharacteristic.Angularity] = swGtolGeomCharSymbol_e.swGcsANG,
        [GtolCharacteristic.Perpendicularity] = swGtolGeomCharSymbol_e.swGcsPERP,
        [GtolCharacteristic.Parallelism] = swGtolGeomCharSymbol_e.swGcsPARALLEL,
        [GtolCharacteristic.Position] = swGtolGeomCharSymbol_e.swGcsPOSITION,
        [GtolCharacteristic.Concentricity] = swGtolGeomCharSymbol_e.swGcsCONC,
        [GtolCharacteristic.Symmetry] = swGtolGeomCharSymbol_e.swGcsSYMMETRY,
        [GtolCharacteristic.CircularRunout] = swGtolGeomCharSymbol_e.swGcsCIRCRUNOUT,
        [GtolCharacteristic.TotalRunout] = swGtolGeomCharSymbol_e.swGcsTOTALRUNOUT,
    };

    /// <summary>swGtolGeomCharSymbol_e for a characteristic.</summary>
    internal static int Gcs(GtolCharacteristic characteristic) => (int)Characteristics[characteristic];

    /// <summary>The characteristic of a swGtolGeomCharSymbol_e number; null for one the add-in does not name.</summary>
    internal static GtolCharacteristic? CharacteristicOf(int gcs) =>
        Characteristics.Where(p => (int)p.Value == gcs).Select(p => (GtolCharacteristic?)p.Key).FirstOrDefault();

    /// <summary>swGtolMatCondition_e for a material condition.</summary>
    internal static int Mc(MaterialCondition condition) => condition switch
    {
        MaterialCondition.Mmc => (int)swGtolMatCondition_e.swMcMMC,
        MaterialCondition.Lmc => (int)swGtolMatCondition_e.swMcLMC,
        _ => (int)swGtolMatCondition_e.swMcNONE,
    };

    /// <summary>The material condition of a swGtolMatCondition_e number (RFS and no symbol read as none); null for any other.</summary>
    internal static MaterialCondition? ConditionOf(int mc) => mc switch
    {
        (int)swGtolMatCondition_e.swMcMMC => MaterialCondition.Mmc,
        (int)swGtolMatCondition_e.swMcLMC => MaterialCondition.Lmc,
        (int)swGtolMatCondition_e.swMcNONE or (int)swGtolMatCondition_e.swMcRFS or (int)swGtolMatCondition_e.swMsNONE => MaterialCondition.None,
        _ => null,
    };
}
