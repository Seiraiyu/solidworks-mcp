using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Tests;

sealed partial class FakeDocument
{
    /// <summary>What Drawing returns: set for a drawing made with <see cref="NewDrawing"/>.</summary>
    public FakeDrawing? Drw { get; set; }

    public ISwDrawing? Drawing() => Drw;

    /// <summary>A drawing as the default template makes one: Sheet1, A3 landscape, 1:1, third angle, the template's format, no view.</summary>
    public static FakeDocument NewDrawing(string title, string? path = null)
    {
        var doc = new FakeDocument(title, path, DocumentType.Drawing);
        doc.Drw = new FakeDrawing(doc);
        doc.Drw.Sheets.Add(new FakeSheet(new SheetFacts { Name = "Sheet1", WidthMm = 420, HeightMm = 297, Scale = new ScaleRatio(1, 1), Format = @"C:\formats\a3 - iso.slddrt" }));
        doc.Drw.Active = "Sheet1";
        return doc;
    }
}

/// <summary>A sheet of a <see cref="FakeDrawing"/>: its values and its views, in order.</summary>
sealed class FakeSheet
{
    public FakeSheet(SheetFacts facts)
    {
        Facts = facts;
    }

    public SheetFacts Facts { get; set; }

    public List<FakeView> Views { get; } = new List<FakeView>();
}

/// <summary>A view of a <see cref="FakeDrawing"/>: what ReadView reports, the model it shows, and how it draws it.</summary>
sealed class FakeView
{
    public FakeView(ViewFacts facts, FakeDocument? model, ViewTransform? transform)
    {
        Facts = facts;
        Model = model;
        Transform = transform;
    }

    public ViewFacts Facts { get; }

    public FakeDocument? Model { get; set; }

    /// <summary>Its model-to-sheet transform; the fake keeps it consistent with its position and scale.</summary>
    public ViewTransform? Transform { get; set; }

    /// <summary>When set, Polylines returns these numbers instead of the model's box drawn through the transform.</summary>
    public List<double>? Drawn { get; set; }

    /// <summary>When true, the view is not redrawn when its sheet's scale changes, although it says it follows it.</summary>
    public bool KeepsScale { get; set; }

    /// <summary>The model's entities the view shows (VisibleEntities), with whether each is a vertex or a silhouette.</summary>
    public List<VisibleEntity> Visible { get; } = new List<VisibleEntity>();
}

/// <summary>
/// A drawing for operation tests. It behaves as SOLIDWORKS is expected to (and the live suite proves): a model view is
/// drawn through a transform for its orientation and centred where it was put; a projected view is the parent turned 90°
/// toward the side it was put on (as the sheet's projection says) and aligned with it; a view following the sheet's scale
/// is redrawn when the sheet's scale changes; deleting a sheet deletes its views. Each way SOLIDWORKS can fail or answer
/// wrongly is a flag the tests set.
/// </summary>
sealed class FakeDrawing : ISwDrawing
{
    readonly FakeDocument _doc;
    int _views;

    public FakeDrawing(FakeDocument doc)
    {
        _doc = doc;
    }

    public List<FakeSheet> Sheets { get; } = new List<FakeSheet>();

    public string? Active { get; set; }

    /// <summary>Every call that changed something, in order.</summary>
    public List<string> Calls { get; } = new List<string>();

    /// <summary>The models a view can show, by full path; when <see cref="Sw"/> is set a model not open is loaded there, hidden.</summary>
    public Dictionary<string, FakeDocument> Models { get; } = new Dictionary<string, FakeDocument>(StringComparer.OrdinalIgnoreCase);

    public FakeSolidWorks? Sw { get; set; }

    /// <summary>True while the user edits a sheet format.</summary>
    public bool EditingFormat { get; set; }

    public IEnumerable<FakeView> AllViews => Sheets.SelectMany(s => s.Views);

    public FakeSheet Sheet(string name) => Sheets.Single(s => s.Facts.Name == name);

    public FakeView View(string name) => AllViews.Single(v => v.Facts.Name == name);

    public IReadOnlyList<string> SheetNames() => Sheets.Select(s => s.Facts.Name).ToList();

    public string? ActiveSheet() => Active;

    /// <summary>
    /// A sheet as SolidWorksDrawing.ReadSheet reads it: <see cref="FakeSheet.Facts"/>'s Format holds the name SOLIDWORKS
    /// gives (ISheet.GetTemplateName, "*.drt" for a template's own format), read through the same rule.
    /// </summary>
    public SheetFacts? ReadSheet(string name)
    {
        var facts = Sheets.FirstOrDefault(s => s.Facts.Name == name)?.Facts.Copy();
        if (facts != null) facts.Format = SheetSetups.FormatOf(facts.Format);
        return facts;
    }

    public bool EditingSheetFormat() => EditingFormat;

    /// <summary>When true, AddSheet makes nothing.</summary>
    public bool AddSheetFails { get; set; }

    /// <summary>When set, a new sheet reads back with this scale whatever was asked (SOLIDWORKS ignoring it).</summary>
    public ScaleRatio? SheetScaleIgnored { get; set; }

    public bool AddSheet(SheetFacts sheet)
    {
        Calls.Add($"add sheet {sheet.Name}");
        if (AddSheetFails) return false;
        var facts = sheet.Copy();
        if (facts.Name.Length == 0) facts.Name = "Sheet" + (Sheets.Count + 1);
        if (SheetScaleIgnored != null) facts.Scale = SheetScaleIgnored;
        if (TemplateFormatNotRestored && SheetSetups.InTemplate(facts.Format)) facts.Format = null;
        Sheets.Add(new FakeSheet(facts));
        Active = facts.Name;
        return true;
    }

    /// <summary>
    /// When true, a template's own format ("*.drt") cannot be given to a sheet that does not have it: AddSheet makes the
    /// sheet with none, and SetSheet leaves the format the sheet has.
    /// </summary>
    public bool TemplateFormatNotRestored { get; set; }

    /// <summary>When true, SetSheet changes nothing.</summary>
    public bool SetSheetIgnored { get; set; }

    public void SetSheet(string name, SheetFacts sheet)
    {
        Calls.Add($"set sheet {name}");
        if (SetSheetIgnored) return;
        var target = Sheet(name);
        var old = target.Facts;
        target.Facts = sheet.Copy();
        if (TemplateFormatNotRestored && SheetSetups.InTemplate(sheet.Format) && !string.Equals(old.Format, sheet.Format, StringComparison.OrdinalIgnoreCase)) target.Facts.Format = old.Format;
        if (target.Facts.Name.Length == 0) target.Facts.Name = name;
        foreach (var v in target.Views) v.Facts.Sheet = target.Facts.Name;
        if (Active == name) Active = target.Facts.Name;
        double was = old.Scale.Numerator / old.Scale.Denominator, now = sheet.Scale.Numerator / sheet.Scale.Denominator;
        if (Math.Abs(was - now) > 1e-12)
        {
            foreach (var v in target.Views.Where(v => v.Facts.SheetScale && !v.KeepsScale)) Rescale(v, new ScaleRatio(sheet.Scale.Numerator, sheet.Scale.Denominator));
        }
    }

    /// <summary>When true, ActivateSheet shows nothing and says so.</summary>
    public bool ActivateFails { get; set; }

    public bool ActivateSheet(string name)
    {
        Calls.Add($"activate {name}");
        if (ActivateFails || !Sheets.Any(s => s.Facts.Name == name)) return false;
        Active = name;
        return true;
    }

    /// <summary>When true, DeleteSheet and DeleteViews delete nothing and say so.</summary>
    public bool DeleteFails { get; set; }

    public bool DeleteSheet(string name)
    {
        Calls.Add($"delete sheet {name}");
        if (DeleteFails) return false;
        var sheet = Sheets.FirstOrDefault(s => s.Facts.Name == name);
        if (sheet == null) return false;
        Sheets.Remove(sheet);
        if (Active == name) Active = Sheets.FirstOrDefault()?.Facts.Name;
        return true;
    }

    public IReadOnlyList<string> ViewNames(string sheet) =>
        Sheets.FirstOrDefault(s => s.Facts.Name == sheet)?.Views.Select(v => v.Facts.Name).ToList() ?? new List<string>();

    public ViewFacts? ReadView(string name) => AllViews.FirstOrDefault(v => v.Facts.Name == name)?.Facts.Copy();

    public ViewTransform? Transform(string view) => View(view).Transform;

    public IReadOnlyList<double> Polylines(string view)
    {
        var v = View(view);
        if (v.Drawn != null) return v.Drawn;
        if (v.Transform == null || v.Model?.ExactBox == null) return new double[0];
        // The model box's outline as one closed polyline, about the view's position at its scale, in metres.
        var box = ViewGeometry.Project(v.Transform, v.Model.ExactBox);
        var corners = new[] { box.Min, new SheetPoint(box.Max.X, box.Min.Y), box.Max, new SheetPoint(box.Min.X, box.Max.Y), box.Min };
        var numbers = new List<double> { 1, 0, 0, 1, 2, 3, 4, 5, corners.Length };
        foreach (var c in corners)
        {
            numbers.Add((c.X - v.Facts.Position.X) / v.Transform.Scale / 1000);
            numbers.Add((c.Y - v.Facts.Position.Y) / v.Transform.Scale / 1000);
            numbers.Add(0);
        }
        return numbers;
    }

    /// <summary>When true, CreateModelView and ProjectView make nothing.</summary>
    public bool CreateFails { get; set; }

    /// <summary>When set, a new view lands this far (sheet mm, x) from where it was put.</summary>
    public double LandsOffByMm { get; set; }

    /// <summary>When set, a new model view faces this orientation whatever was asked.</summary>
    public string? FacesInstead { get; set; }

    /// <summary>Called with each view CreateModelView or ProjectView makes (a test's way to make SOLIDWORKS draw it wrongly).</summary>
    public Action<FakeView>? AfterCreate { get; set; }

    /// <summary>When true, making a view marks its model as having unsaved changes.</summary>
    public bool DirtiesModel { get; set; }

    public string? CreateModelView(string modelPath, string orientation, SheetPoint at)
    {
        Calls.Add($"model view {System.IO.Path.GetFileName(modelPath)} {orientation}");
        if (CreateFails || Active == null) return null;
        var model = Load(modelPath);
        if (model == null) return null;
        var sheet = Sheet(Active);
        var (right, up, facing) = Axes(FacesInstead ?? orientation);
        var scale = new ScaleRatio(sheet.Facts.Scale.Numerator, sheet.Facts.Scale.Denominator);
        var facts = new ViewFacts
        {
            Name = "Drawing View" + ++_views, Sheet = sheet.Facts.Name, Type = ViewType.Model, Model = model.Document.Path, Configuration = "Default",
            Orientation = orientation, Scale = scale, SheetScale = true,
            Display = orientation == "front" || orientation == "top" || orientation == "right" ? ViewDisplay.HiddenLinesVisible : ViewDisplay.HiddenLinesRemoved,
        };
        var view = new FakeView(facts, model, null);
        Place(view, right, up, facing, new SheetPoint(at.X + LandsOffByMm, at.Y), Ratio(scale));
        sheet.Views.Add(view);
        if (DirtiesModel) model.Document.Dirty = true;
        AfterCreate?.Invoke(view);
        return facts.Name;
    }

    public string? ProjectView(string parent, SheetPoint at)
    {
        Calls.Add($"project {parent}");
        if (CreateFails) return null;
        var p = View(parent);
        var sheet = Sheets.Single(s => s.Views.Contains(p));
        if (p.Transform == null) return null;
        double dx = at.X - p.Facts.Position.X, dy = at.Y - p.Facts.Position.Y;
        bool horizontal = Math.Abs(dx) >= Math.Abs(dy);
        int sign = sheet.Facts.Projection == Projection.ThirdAngle ? 1 : -1;
        var direction = horizontal ? (dx * sign > 0 ? ProjectDirection.Right : ProjectDirection.Left) : (dy * sign > 0 ? ProjectDirection.Above : ProjectDirection.Below);
        var facing = ViewGeometry.ProjectedFacing(p.Transform, direction);
        Vector3 right, up;
        if (horizontal)
        {
            up = ViewGeometry.Up(p.Transform);
            right = Cross(up, facing);
        }
        else
        {
            right = ViewGeometry.Right(p.Transform);
            up = Cross(facing, right);
        }
        var facts = new ViewFacts
        {
            Name = "Drawing View" + ++_views, Sheet = sheet.Facts.Name, Type = ViewType.Projected, Model = p.Facts.Model, Configuration = p.Facts.Configuration,
            Parent = parent, Scale = new ScaleRatio(p.Facts.Scale.Numerator, p.Facts.Scale.Denominator), SheetScale = p.Facts.SheetScale, Display = p.Facts.Display,
        };
        var view = new FakeView(facts, p.Model, null);
        Place(view, right, up, facing, new SheetPoint(at.X + LandsOffByMm, at.Y), p.Transform.Scale);
        // Aligned as SOLIDWORKS aligns a projected view: the model's origin on the parent's centre line.
        var origin = ViewGeometry.ToSheet(p.Transform, new Point3(0, 0, 0));
        var mine = ViewGeometry.ToSheet(view.Transform!, new Point3(0, 0, 0));
        Shift(view, horizontal ? 0 : origin.X - mine.X, horizontal ? origin.Y - mine.Y : 0);
        sheet.Views.Add(view);
        AfterCreate?.Invoke(view);
        return facts.Name;
    }

    /// <summary>When true, SetPosition, SetScale, SetDisplay and SetConfiguration change nothing.</summary>
    public bool ViewSetIgnored { get; set; }

    public void SetPosition(string view, SheetPoint at)
    {
        Calls.Add($"move {view}");
        if (ViewSetIgnored) return;
        var v = View(view);
        double dx = at.X - v.Facts.Position.X, dy = at.Y - v.Facts.Position.Y;
        // SOLIDWORKS keeps an aligned view on its parent's centre line (CADapter measured).
        if (v.Facts.Type == ViewType.Projected && v.Facts.Parent != null)
        {
            var parent = View(v.Facts.Parent);
            var origin = new Point3(0, 0, 0);
            if (Math.Abs(ViewGeometry.ToSheet(v.Transform!, origin).Y - ViewGeometry.ToSheet(parent.Transform!, origin).Y) < 1e-9) dy = 0;
            else dx = 0;
        }
        // Views projected from it move with it the whole way, as SOLIDWORKS drags them (5.A live run: the front moved
        // 10 mm along x and its right view moved 10 mm along x too), so they stay aligned.
        foreach (var child in AllViews.Where(c => c.Facts.Parent == view && c.Facts.Type == ViewType.Projected && c.Transform != null && v.Transform != null).ToList())
            Shift(child, dx, dy);
        Shift(v, dx, dy);
        AfterMove?.Invoke(view);
    }

    /// <summary>Called with the name of each view SetPosition moved.</summary>
    public Action<string>? AfterMove { get; set; }

    public void SetScale(string view, ScaleRatio? scale)
    {
        Calls.Add($"scale {view}");
        if (ViewSetIgnored) return;
        var v = View(view);
        v.Facts.SheetScale = scale == null;
        var sheet = Sheets.Single(s => s.Views.Contains(v));
        var to = scale ?? new ScaleRatio(sheet.Facts.Scale.Numerator, sheet.Facts.Scale.Denominator);
        Rescale(v, to);
        // Views projected from it are drawn at its scale, and stay on its centre lines.
        foreach (var child in AllViews.Where(c => c.Facts.Parent == view && c.Facts.Type == ViewType.Projected && c.Transform != null).ToList())
        {
            var origin = new Point3(0, 0, 0);
            var before = (Parent: ViewGeometry.ToSheet(v.Transform!, origin), Child: ViewGeometry.ToSheet(child.Transform!, origin));
            bool horizontal = Math.Abs(before.Parent.Y - before.Child.Y) <= 1e-9;
            Rescale(child, to);
            var p = ViewGeometry.ToSheet(v.Transform!, origin);
            var c = ViewGeometry.ToSheet(child.Transform!, origin);
            Shift(child, horizontal ? 0 : p.X - c.X, horizontal ? p.Y - c.Y : 0);
        }
    }

    public void SetDisplay(string view, ViewDisplay display)
    {
        Calls.Add($"display {view}");
        if (!ViewSetIgnored) View(view).Facts.Display = display;
    }

    /// <summary>The configurations a model has, by path; a configuration not listed is not taken.</summary>
    public Dictionary<string, List<string>> Configurations { get; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    public void SetConfiguration(string view, string configuration)
    {
        Calls.Add($"configuration {view}");
        var v = View(view);
        if (ViewSetIgnored || v.Facts.Model == null) return;
        if (Configurations.TryGetValue(v.Facts.Model, out var names) && names.Contains(configuration)) v.Facts.Configuration = configuration;
    }

    public bool DeleteViews(IReadOnlyList<string> views)
    {
        Calls.Add($"delete {string.Join(", ", views)}");
        if (DeleteFails) return false;
        foreach (var name in views)
        {
            foreach (var s in Sheets) s.Views.RemoveAll(v => v.Facts.Name == name);
        }
        return true;
    }

    public ISwDocument? ModelOf(string view) => AllViews.FirstOrDefault(v => v.Facts.Name == view)?.Model;

    public IReadOnlyList<VisibleEntity> VisibleEntities(string view, bool edges, bool vertices) =>
        View(view).Visible.Where(e => e.Vertex ? vertices : edges).ToList();

    /// <summary>The drawing's own document settings; a name listed in <see cref="SettingsIgnored"/> keeps its value.</summary>
    public Dictionary<string, bool> Toggles { get; } = DrawingSettings.All.ToDictionary(n => n, _ => true);

    public HashSet<string> SettingsIgnored { get; } = new HashSet<string>();

    public IReadOnlyDictionary<string, bool> Settings() => new Dictionary<string, bool>(Toggles);

    public void SetSetting(string name, bool value)
    {
        Calls.Add($"setting {name}");
        if (!SettingsIgnored.Contains(name)) Toggles[name] = value;
    }

    /// <summary>A view added as if it were already on a sheet: a model view of <paramref name="model"/> in <paramref name="orientation"/>.</summary>
    public FakeView AddView(string sheet, FakeDocument model, string orientation, SheetPoint at, ScaleRatio? scale = null)
    {
        string? before = Active;
        Active = sheet;
        Models[model.Document.Path!] = model;
        string name = CreateModelView(model.Document.Path!, orientation, at)!;
        Calls.Clear();
        Active = before;
        var view = View(name);
        if (scale != null)
        {
            view.Facts.SheetScale = false;
            Rescale(view, scale);
        }
        return view;
    }

    FakeDocument? Load(string path)
    {
        var open = Sw?.Docs.FirstOrDefault(d => string.Equals(d.Document.Path, path, StringComparison.OrdinalIgnoreCase));
        if (open != null) return Use(open);
        if (!Models.TryGetValue(path, out var model)) return null;
        if (Sw != null && !Sw.Docs.Contains(model))
        {
            model.Document.Visible = false;
            Sw.Track(model);
        }
        return Use(model);
    }

    FakeDocument Use(FakeDocument model)
    {
        if (!_doc.Uses.Contains(model.Document.Path!, StringComparer.OrdinalIgnoreCase)) _doc.Uses.Add(model.Document.Path!);
        return model;
    }

    /// <summary>Gives a view its transform: the model's box centred at <paramref name="at"/>.</summary>
    static void Place(FakeView view, Vector3 right, Vector3 up, Vector3 facing, SheetPoint at, double scale)
    {
        var rotation = new[] { right.X, up.X, facing.X, right.Y, up.Y, facing.Y, right.Z, up.Z, facing.Z };
        var untranslated = new ViewTransform(rotation, 0, 0, 0, scale);
        var box = view.Model?.ExactBox ?? new Box3();
        var centre = ViewGeometry.Centre(ViewGeometry.Project(untranslated, box));
        view.Transform = new ViewTransform(rotation, at.X - centre.X, at.Y - centre.Y, 0, scale);
        view.Facts.Position = new SheetPoint(at.X, at.Y);
    }

    static void Shift(FakeView view, double dx, double dy)
    {
        var t = view.Transform;
        view.Facts.Position = new SheetPoint(view.Facts.Position.X + dx, view.Facts.Position.Y + dy);
        if (t != null) view.Transform = new ViewTransform(t.Rotation, t.TxMm + dx, t.TyMm + dy, t.TzMm, t.Scale);
    }

    /// <summary>Redraws a view at another scale about its centre.</summary>
    static void Rescale(FakeView view, ScaleRatio scale)
    {
        view.Facts.Scale = new ScaleRatio(scale.Numerator, scale.Denominator);
        var t = view.Transform;
        if (t == null) return;
        double s = Ratio(scale);
        var p = view.Facts.Position;
        view.Transform = new ViewTransform(t.Rotation, p.X - (p.X - t.TxMm) * s / t.Scale, p.Y - (p.Y - t.TyMm) * s / t.Scale, t.TzMm, s);
    }

    static double Ratio(ScaleRatio scale) => scale.Numerator / scale.Denominator;

    static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>The sheet's right, up and toward-the-reader as model directions, for each standard orientation (any other is front).</summary>
    static (Vector3 Right, Vector3 Up, Vector3 Facing) Axes(string orientation)
    {
        double a = 1 / Math.Sqrt(2), b = 1 / Math.Sqrt(6), c = 1 / Math.Sqrt(3);
        return orientation switch
        {
            "back" => (new Vector3(-1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, -1)),
            "top" => (new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0)),
            "bottom" => (new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, -1, 0)),
            "right" => (new Vector3(0, 0, -1), new Vector3(0, 1, 0), new Vector3(1, 0, 0)),
            "left" => (new Vector3(0, 0, 1), new Vector3(0, 1, 0), new Vector3(-1, 0, 0)),
            "isometric" => (new Vector3(a, 0, -a), new Vector3(-b, 2 * b, -b), new Vector3(c, c, c)),
            _ => (new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1)),
        };
    }
}
