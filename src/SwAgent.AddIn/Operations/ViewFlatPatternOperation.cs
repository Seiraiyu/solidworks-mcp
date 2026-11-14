using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>
/// view.flat_pattern (design 611, Flat-pattern views): a sheet-metal part's flat pattern as a drawing view, checked
/// against phase 6's flat size from the cut list, with one bend line per bend and the part's state unchanged. The part
/// is opened when it is not (CADapter: SOLIDWORKS makes no flat-pattern view of a part that is not open). SOLIDWORKS adds
/// a derived configuration to the part to draw it; when that marks a saved part as changed, the part's volume, bends and
/// flat size are measured unchanged and the result says marked_dirty, as 6.D's exports do, and nothing is saved.
/// </summary>
public sealed class ViewFlatPatternOperation : DrawingChangeOperation<ViewFlatPatternRequest, ViewFlatPatternResult>
{
    /// <summary>The call that makes the view, for messages.</summary>
    const string Call = "CreateFlatPatternViewFromModelView3";

    public ViewFlatPatternOperation(AddInServices services)
        : base(services)
    {
    }

    string _model = "";
    string _configuration = "";
    ISwDocument? _part;
    FlatSize _flat = new FlatSize();
    double _roundingMm;
    double _mmPerUnit = 1;
    SheetState _state;
    bool _dirtyBefore;
    ExportFlatPatternOperation.Geometry? _geometry;
    bool _markedMeasured;

    protected override string? DocumentOf(ViewFlatPatternRequest request) => request.Document;

    protected override ViewFlatPatternResult Change(ISwDocument doc, ISwDrawing drawing, ViewFlatPatternRequest request, ModelTransaction tx, IOperationContext context)
    {
        _markedMeasured = false;
        _model = SavedModels.Require(Services.SolidWorks, request.Model, "/model", out var open);
        if (PathRules.TypeOf(_model) != DocumentType.Part)
        {
            throw new SwAgentException(ErrorCode.WrongDocumentType,
                $"/model: view.flat_pattern draws a sheet-metal part's flat pattern, and {Path.GetFileName(_model)} is an assembly; draw it with sw_view_create",
                new JsonObject { ["field"] = "/model", ["document"] = _model, ["type"] = "assembly", ["needs"] = "sheet_metal" });
        }
        ViewRequests.Scale(request.Scale, "/scale");
        if (request.Configuration != null && request.Configuration.Trim().Length == 0)
            throw ViewRequests.Invalid("/configuration", "/configuration: give a configuration's name, or leave it out for the part's active one");
        string sheet = request.Sheet != null ? Sheet(doc, drawing, request.Sheet, "/sheet") : drawing.ActiveSheet() ?? drawing.SheetNames().First();
        ViewRequests.OnSheet(request.At, drawing.ReadSheet(sheet)!, "/at");

        bool opened = false;
        if (open == null)
        {
            var outcome = Services.SolidWorks.OpenDocument(_model, DocumentType.Part, readOnly: false);
            var loaded = outcome.Document ?? throw new SwAgentException(ErrorCode.OperationFailed,
                $"SOLIDWORKS did not open {_model} to draw its flat pattern (it makes no flat-pattern view of a part that is not open): {string.Join(", ", SolidWorksCodes.LoadErrors(outcome.Errors).DefaultIfEmpty("no error reported"))}",
                new JsonObject { ["model"] = _model });
            opened = true;
            Services.Runtime.DocumentOpened(DocumentKeys.Of(loaded.Info()), owned: true);
            tx.Edited(new ChangedItem { Name = loaded.Info().Title, Property = "open", Before = "closed", After = "open" }, () => Services.SolidWorks.Close(loaded));
            doc.Activate();
            open = loaded;
        }
        var part = open;
        _part = part;
        SheetMetalParts.Require(part, Name);
        var info = part.Info();
        _configuration = request.Configuration?.Trim() ?? part.ActiveConfiguration();
        var before = part.ConfigurationNames().ToList();
        if (!before.Contains(_configuration, StringComparer.OrdinalIgnoreCase))
            throw ViewRequests.Invalid("/configuration", $"/configuration: {info.Title} has no configuration {_configuration}; it has {string.Join(", ", before)}");
        var sm = part.SheetMetal;
        var cutList = sm.CutList();
        _mmPerUnit = sm.MmPerUnit();
        int bends = sm.Bends().Count;
        _flat = SheetMetalParts.Flat(cutList, _mmPerUnit, bends) ?? throw new SwAgentException(ErrorCode.OperationFailed,
            $"SOLIDWORKS' cut list for {info.Title} gives no flat size ({SheetMetalParts.LengthProperty} and {SheetMetalParts.WidthProperty}), so a flat-pattern view could not be checked against it. Nothing was drawn",
            new JsonObject { ["cut_list"] = cutList.Select(p => (object?)$"{p.Key} = {p.Value}").ToList() });
        _roundingMm = FlatPatternExports.ToleranceMm(cutList, _mmPerUnit) - FlatPatternFileCheck.ExtentsToleranceMm;
        _state = SheetMetalParts.State(part);
        _dirtyBefore = info.Dirty;
        _geometry = ExportFlatPatternOperation.Geometry.Of(part, _mmPerUnit);

        if (drawing.ActiveSheet() != sheet && !drawing.ActivateSheet(sheet))
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not show the sheet {sheet} to put the view on", new JsonObject { ["sheet"] = sheet });
        string name = drawing.FlatPatternView(_model, _configuration, request.At)
            ?? throw new SwAgentException(ErrorCode.OperationFailed,
                $"SOLIDWORKS made no flat-pattern view of {info.Title} ({_configuration})",
                new JsonObject { ["model"] = _model, ["configuration"] = _configuration });
        var added = part.ConfigurationNames().Except(before, StringComparer.OrdinalIgnoreCase).ToList();
        if (added.Count > 0)
        {
            tx.Edited(new ChangedItem { Name = info.Title, Property = "configurations", Before = string.Join(", ", before), After = string.Join(", ", part.ConfigurationNames()) }, () =>
            {
                foreach (string c in added)
                {
                    if (part.ConfigurationNames().Contains(c, StringComparer.OrdinalIgnoreCase) && !part.DeleteConfiguration(c))
                        throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not delete the configuration {c} it added to {info.Title}");
                }
            });
        }
        MadeView(doc, drawing, tx, name);
        drawing.SetScale(name, request.Scale);
        drawing.SetBendNotes(name, request.BendNotes != false);
        drawing.SetPosition(name, request.At);
        context.Log.Info(FormattableString.Invariant($"view.flat_pattern: {name} of {_model} ({_configuration}) on {sheet} at {request.At}, scale {(request.Scale?.ToString() ?? "the sheet's")}; flat {_flat.LengthMm:0.####} × {_flat.WidthMm:0.####}, {_flat.Bends} bends; {(opened ? "opened; " : "")}added {string.Join(", ", added.DefaultIfEmpty("no configuration"))}"));
        return new ViewFlatPatternResult
        {
            View = new ViewInfo { Name = name, Sheet = sheet },
            Flat = _flat,
            State = _state,
            Opened = opened,
            ConfigurationsAdded = added,
            ModelUnsaved = _dirtyBefore,
        };
    }

    /// <summary>
    /// Design 611, Flat-pattern views: a flat_pattern view of the part where asked, at the scale asked, drawing the flat
    /// size × scale, one bend line per bend, bend notes as asked, and the part as folded or flattened as it was. A part
    /// SOLIDWORKS marked as changed is measured unchanged otherwise (6.D's measures).
    /// </summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, ViewFlatPatternRequest request, ViewFlatPatternResult result)
    {
        string name = result.View.Name;
        var part = _part!;
        var facts = drawing.ReadView(name) ?? throw ViewCreateOperation.Failed(name, "exists", "the view on its sheet", "no such view");
        if (facts.Type != ViewType.FlatPattern) throw ViewCreateOperation.Failed(name, "type", "flat_pattern", WireNames.ForEnum(facts.Type));
        if (facts.Model == null || !DocumentKeys.Same(Path.GetFullPath(facts.Model), _model)) throw ViewCreateOperation.Failed(name, "model", _model, facts.Model ?? "none");
        // The view shows the configuration asked, or the one SOLIDWORKS derives from it to draw the flat pattern (…SM-FLAT-PATTERN).
        string shown = facts.Configuration ?? "";
        bool configuration = string.Equals(shown, _configuration, StringComparison.OrdinalIgnoreCase)
            || (shown.StartsWith(_configuration, StringComparison.OrdinalIgnoreCase) && part.ConfigurationNames().Contains(shown, StringComparer.OrdinalIgnoreCase));
        if (!configuration) throw ViewCreateOperation.Failed(name, "configuration", $"{_configuration} or the configuration SOLIDWORKS derived from it", facts.Configuration ?? "none");
        if (!ViewGeometry.Near(facts.Position, request.At)) throw ViewCreateOperation.Failed(name, "position", request.At.ToString(), facts.Position.ToString());
        var sheetScale = drawing.ReadSheet(facts.Sheet)!.Scale;
        var scale = request.Scale ?? sheetScale;
        if (facts.SheetScale != (request.Scale == null) || !DrawingReader.SameScale(facts.Scale, scale))
            throw ViewCreateOperation.Failed(name, "scale", request.Scale == null ? $"the sheet's, {sheetScale}" : scale.ToString(), $"{facts.Scale}{(facts.SheetScale ? " (the sheet's)" : "")}");
        double s = DrawingReader.Ratio(scale);
        ViewChecks.Scale(drawing, name, s);
        bool notes = drawing.BendNotes(name);
        if (notes != (request.BendNotes != false)) throw ViewCreateOperation.Failed(name, "bend_notes", request.BendNotes != false ? "shown" : "hidden", notes ? "shown" : "hidden");
        int lines = drawing.BendLines(name);
        if (lines != _flat.Bends) throw ViewCreateOperation.Failed(name, "bend_lines", $"{_flat.Bends} (one per bend)", lines.ToString());
        var drawn = DrawingReader.Extents(drawing, facts) ?? throw ViewCreateOperation.Failed(name, "extents", "the flat pattern drawn", "nothing drawn");
        double w = ViewGeometry.Width(drawn), h = ViewGeometry.Height(drawn);
        double longSide = Math.Max(_flat.LengthMm, _flat.WidthMm) * s, shortSide = Math.Min(_flat.LengthMm, _flat.WidthMm) * s;
        double tolerance = ViewGeometry.PositionToleranceMm + _roundingMm * s;
        if (Math.Abs(Math.Max(w, h) - longSide) > tolerance || Math.Abs(Math.Min(w, h) - shortSide) > tolerance)
        {
            throw ViewCreateOperation.Failed(name, "extents",
                FormattableString.Invariant($"{longSide:0.####} × {shortSide:0.####} mm within {tolerance:0.####} (the flat size {_flat.LengthMm:0.####} × {_flat.WidthMm:0.####} × {s:0.####})"),
                FormattableString.Invariant($"{Math.Max(w, h):0.####} × {Math.Min(w, h):0.####} mm ({drawn})"));
        }
        var state = SheetMetalParts.State(part);
        if (state != _state) throw ViewCreateOperation.Failed(name, "state", WireNames.ForEnum(_state), WireNames.ForEnum(state));
        var now = part.Info();
        if (!_dirtyBefore && now.Dirty)
        {
            _geometry!.Unchanged(now, Call, ExportFlatPatternOperation.Geometry.Of(part, _mmPerUnit));
            _markedMeasured = true;
            result.MarkedDirty = true;
        }
        result.BendLines = lines;
        result.BendNotes = notes;
        result.View = Describe(doc, drawing, name);
    }

    /// <summary>The part, once Check measured it unchanged but for the flag SOLIDWORKS set.</summary>
    protected override bool MayBeMarked(string path) =>
        _markedMeasured && _part?.Info().Path is string p && DocumentKeys.Same(Path.GetFullPath(p), Path.GetFullPath(path));
}
