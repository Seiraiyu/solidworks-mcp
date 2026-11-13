using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.Live.Tests;

/// <summary>
/// Plan 6.D's export cases: the flat pattern written as DXF and DWG and checked against the flat size the bend
/// arithmetic gives (design 454, Flat-pattern export), a part with unsaved changes exported as it is in memory, and a
/// part never saved refused before SOLIDWORKS is called. Each DXF is also read here, independently of the add-in, for
/// its $EXTMIN and $EXTMAX.
/// </summary>
public sealed partial class SheetMetalSuite
{
    /// <summary>What a 20 mm flange (inner virtual sharp, R 3, T 2, K 0.5) adds across the flat: its bend's 2π and 12 mm straight (17 less the 5 taken back).</summary>
    static readonly double Across20 = 2 * Math.PI + 12;

    /// <summary>What a 25 mm flange (inner virtual sharp, R 3, T 2) adds across the flat at K 0.5: 17 mm straight (22 less the 5 taken back) and its bend's π/2 × 4.</summary>
    static readonly double Across25 = 22 - 5 + HalfK;

    /// <summary>The same at K 0.1: its bend π/2 × (3 + 0.1 × 2); CADapter's 82.0265 less the tab's 60.</summary>
    static readonly double Across25AtK01 = 22 - 5 + Math.PI / 2 * (3 + 0.1 * 2);

    /// <summary>Within <paramref name="mm"/> millimetres, absolutely.</summary>
    static void Within(double expected, double actual, double mm, string what) =>
        Assert.True(Math.Abs(expected - actual) <= mm, $"{what}: expected {expected:0.####} ± {mm} mm, got {actual:0.####}");

    Task<ExportFlatPatternResult> ExportFlat(ExportFlatPatternRequest request) => Call<ExportFlatPatternRequest, ExportFlatPatternResult>(request);

    string ScratchFile(string name) => Path.Combine(_run.Guard.ScratchDirectory, name);

    Task<DocumentSaveResult> Save(string path) => Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = path });

    /// <summary>CADapter's box: the suite's tab with four 20 mm flanges (inner virtual sharp), the long edges first, as case 9 makes it.</summary>
    async Task<string> FlangedBox(string name)
    {
        var (path, _) = await Sheet(name);
        foreach (var (x, y) in new[] { (50.0, 60.0), (50.0, 0.0), (0.0, 30.0), (100.0, 30.0) })
            await EdgeFlange(new SheetMetalEdgeFlangeRequest { Document = path, Edges = new List<string> { await EdgeAt(path, x, y) }, LengthMm = 20, LengthFrom = FlangeLengthFrom.InnerVirtualSharp });
        return path;
    }

    /// <summary>A DXF's $EXTMIN and $EXTMAX read straight from its text, as (longer side, shorter side); null when the header does not give both.</summary>
    static (double Long, double Short)? HeaderSides(string path)
    {
        var lines = File.ReadAllLines(path).Select(l => l.Trim()).ToList();
        double? Value(string variable, string code)
        {
            int at = lines.IndexOf(variable);
            for (int i = at + 1; at >= 0 && i + 1 < lines.Count && lines[i] != "9" && lines[i] != "0"; i += 2)
            {
                if (lines[i] == code) return double.Parse(lines[i + 1], CultureInfo.InvariantCulture);
            }
            return null;
        }
        double? x0 = Value("$EXTMIN", "10"), y0 = Value("$EXTMIN", "20"), x1 = Value("$EXTMAX", "10"), y1 = Value("$EXTMAX", "20");
        if (x0 == null || y0 == null || x1 == null || y1 == null) return null;
        double w = x1.Value - x0.Value, h = y1.Value - y0.Value;
        return (Math.Max(w, h), Math.Min(w, h));
    }

    /// <summary>
    /// The DXF's own header, read here, as a note: whether it agrees with the entities export.flat_pattern measured. Not
    /// asserted: the 6.D live run found ExportFlatPatternView writes the drawing sheet (279.4 × 215.9) there.
    /// </summary>
    static string HeaderAgrees(ExportFlatPatternResult dxf)
    {
        var sides = HeaderSides(dxf.Path);
        if (sides == null) return "no $EXTMIN/$EXTMAX in the file";
        bool agrees = Math.Abs(dxf.Extents!.LengthMm - sides.Value.Long) <= 1e-6 && Math.Abs(dxf.Extents.WidthMm - sides.Value.Short) <= 1e-6;
        return FormattableString.Invariant($"$EXTMIN/$EXTMAX read here: {sides.Value.Long:0.####} × {sides.Value.Short:0.####} ({(agrees ? "the flat" : "not the flat: reported only")})");
    }

    [SkippableFact, Step(20)]
    public Task The_flat_pattern_goes_to_DXF_and_DWG_at_its_flat_size() => _run.CaseAsync(20, "export.flat_pattern of CADapter's box of four 20 mm flanges, saved: a DXF 136.5664 × 96.5664 (the bend arithmetic's flat) with a bend line per bend, the same without bend lines and none, a DWG with its AC10xx header; flattened, the same DXF and the part left flattened; path, flag and state kept", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = await FlangedBox("export-box");
        await Save(path);
        double length = 100 + 2 * Across20, width = 60 + 2 * Across20;
        var clock = Stopwatch.StartNew();
        var dxf = await ExportFlat(new ExportFlatPatternRequest { Document = path, Path = ScratchFile("box.dxf"), Format = FlatPatternFormat.Dxf });
        long ms = clock.ElapsedMilliseconds;
        Assert.Equal("DXF", dxf.Header);
        Assert.Equal("ExportToDWG2", dxf.Method);
        Assert.Equal("its entities", dxf.Extents!.From);
        Within(length, dxf.Extents.LengthMm, 0.01, "the DXF's length");
        Within(width, dxf.Extents.WidthMm, 0.01, "the DXF's width");
        Assert.True(dxf.BendLines >= 4, $"a bend line per bend: {dxf.BendLines} for 4 bends; layers {string.Join(", ", dxf.Layers)}");
        // ExportToDWG2 may mark the saved part changed (6.D live run); the flag then says so, and nothing saved it.
        Assert.Equal(dxf.MarkedDirty, dxf.Document.Dirty);
        Assert.Equal(SheetState.Folded, dxf.State);
        string header = HeaderAgrees(dxf);
        var marked = new List<string> { $"{dxf.Method} {(dxf.MarkedDirty ? "marked it" : "kept it clean")}" };

        // Saved again, so the next call is measured on a clean part: does ExportFlatPatternView mark it too?
        if (dxf.Document.Dirty) await Save(path);
        var plain = await ExportFlat(new ExportFlatPatternRequest { Document = path, Path = ScratchFile("box-plain.dxf"), Format = FlatPatternFormat.Dxf, BendLines = false });
        Assert.Equal(plain.MarkedDirty, plain.Document.Dirty);
        marked.Add($"{plain.Method} {(plain.MarkedDirty ? "marked it" : "kept it clean")}");
        if (plain.Document.Dirty) await Save(path);
        Assert.Equal(0, plain.BendLines);
        Assert.Equal("ExportFlatPatternView", plain.Method);
        Within(length, plain.Extents!.LengthMm, 0.01, "the DXF without bend lines, its length");
        Within(width, plain.Extents.WidthMm, 0.01, "the DXF without bend lines, its width");
        string plainHeader = HeaderAgrees(plain);

        var dwg = await ExportFlat(new ExportFlatPatternRequest { Document = path, Path = ScratchFile("box.dwg"), Format = FlatPatternFormat.Dwg });
        Assert.Matches("^AC10[0-9][0-9]$", dwg.Header);
        Assert.True(dwg.Bytes >= 1024, $"the DWG holds {dwg.Bytes} bytes");
        marked.Add($"{dwg.Method} (DWG) {(dwg.MarkedDirty ? "marked it" : "kept it clean")}");
        if (dwg.Document.Dirty) await Save(path);

        await Flatten(path, true);
        var flat = await ExportFlat(new ExportFlatPatternRequest { Document = path, Path = ScratchFile("box-flattened.dxf"), Format = FlatPatternFormat.Dxf });
        Assert.Equal(SheetState.Flattened, flat.State);
        Assert.Equal(SheetState.Flattened, (await Read(path)).State);
        Within(length, flat.Extents!.LengthMm, 0.01, "the flattened part's DXF, its length");
        await Flatten(path, false);
        Assert.Equal(SheetState.Folded, (await Read(path)).State);
        return FormattableString.Invariant($"DXF by {dxf.Method} in {ms} ms (export deadline 120 s), {dxf.Bytes} bytes: {dxf.Extents.LengthMm:0.####} × {dxf.Extents.WidthMm:0.####} from {dxf.Extents.From} (expected {length:0.####} × {width:0.####}); {header}; ") +
               $"{dxf.BendLines} bend lines; layers {string.Join(", ", dxf.Layers)}; attempts {string.Join("; ", dxf.Attempts)} | without bend lines: by {plain.Method}, {plain.BendLines}, layers {string.Join(", ", plain.Layers)}, {plainHeader} | " +
               $"DWG by {dwg.Method}: {dwg.Header}, {dwg.Bytes} bytes | flattened: by {flat.Method}, {flat.Extents.LengthMm:0.####} × {flat.Extents.WidthMm:0.####}, left flattened, folded again | " +
               $"the saved part's flag: {string.Join("; ", marked)} | checks: {string.Join("; ", dxf.Checks)}";
    });

    [SkippableFact, Step(21)]
    public Task An_unsaved_change_is_in_the_flat_pattern_and_ExportToDWG2_writes_with_sketches() => _run.CaseAsync(21, "export.flat_pattern of a 25 mm flange's tab, saved (100 × 83.2832): with sketches, through ExportToDWG2, and without, the same size; then set to K 0.1 and not saved: 100 × 82.0265 as the part is now, by both calls, and the part still unsaved", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (path, _) = await Sheet("export-unsaved");
        await EdgeFlange(new SheetMetalEdgeFlangeRequest { Document = path, Edges = new List<string> { await EdgeAt(path, 50, 60) }, LengthMm = 25, LengthFrom = FlangeLengthFrom.InnerVirtualSharp });
        await Save(path);
        var notes = new List<string>();
        foreach (var (name, sketches) in new[] { ("tab-saved-sketches.dxf", true), ("tab-saved.dxf", false) })
        {
            var saved = await ExportFlat(new ExportFlatPatternRequest { Document = path, Path = ScratchFile(name), Format = FlatPatternFormat.Dxf, Sketches = sketches });
            // Bend lines are asked for (the default), so both go through ExportToDWG2 (6.D live run).
            Assert.Equal("ExportToDWG2", saved.Method);
            Assert.True(saved.BendLines >= 1, $"{name}: a bend line for the one bend, {saved.BendLines}");
            Within(100, saved.Extents!.LengthMm, 0.01, $"{name}: its length");
            Within(60 + Across25, saved.Extents.WidthMm, 0.01, $"{name}: its width");
            Assert.Equal(saved.MarkedDirty, saved.Document.Dirty);
            notes.Add(FormattableString.Invariant($"saved, by {saved.Method}: {saved.Extents.LengthMm:0.####} × {saved.Extents.WidthMm:0.####}, {saved.BendLines} bend lines, layers {string.Join(", ", saved.Layers)}, marked_dirty {saved.MarkedDirty}; {HeaderAgrees(saved)}"));
            if (saved.Document.Dirty) await Save(path);
        }

        await Call<SheetMetalSetParametersRequest, SheetMetalSetParametersResult>(new SheetMetalSetParametersRequest { Document = path, Allowance = new BendAllowance { KFactor = 0.1 } });
        // Without bend lines and sketches ExportFlatPatternView writes; with sketches ExportToDWG2: both calls see the change.
        foreach (var (name, sketches, method) in new[] { ("tab-unsaved.dxf", false, "ExportFlatPatternView"), ("tab-unsaved-sketches.dxf", true, "ExportToDWG2") })
        {
            var unsaved = await ExportFlat(new ExportFlatPatternRequest { Document = path, Path = ScratchFile(name), Format = FlatPatternFormat.Dxf, Sketches = sketches, BendLines = sketches });
            Assert.Equal(method, unsaved.Method);
            Within(60 + Across25AtK01, unsaved.Extents!.WidthMm, 0.01, $"{name}: its width at K 0.1, as the part is now");
            Assert.True(unsaved.Document.Dirty, "the part keeps its unsaved changes");
            Assert.False(unsaved.MarkedDirty, "a part already changed is not marked by the export");
            notes.Add(FormattableString.Invariant($"unsaved at K 0.1, by {unsaved.Method}:{unsaved.Extents.LengthMm:0.####} × {unsaved.Extents.WidthMm:0.####} (expected 100 × {60 + Across25AtK01:0.####}); {HeaderAgrees(unsaved)}"));
        }
        var state = (await _run.AddinStateAsync()).Documents.Single(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        Assert.True(state.Dirty, "still unsaved after both exports");
        return string.Join(" | ", notes);
    });

    [SkippableFact, Step(22)]
    public Task A_part_never_saved_is_refused_before_SOLIDWORKS_is_asked() => _run.CaseAsync(22, "export.flat_pattern of a sheet-metal part never saved (document.new without a path): invalid_argument at document naming sw_document_save_as, no file written and no dialog open; the part closed unsaved in the same case", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        // A part with no path is never the suite's to the owner guard, so it lives only inside this case: made, refused, closed.
        var made = await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest());
        string title = made.Document.Title;
        Assert.Null(made.Document.Path);
        try
        {
            var outline = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
            {
                Document = title,
                On = "front",
                Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "sheet", Corner1 = new Point2(0, 0), Corner2 = new Point2(100, 60) } },
            });
            await Call<SheetMetalBaseFlangeRequest, SheetMetalFeatureResult>(new SheetMetalBaseFlangeRequest { Document = title, Sketch = outline.Sketch.Name, ThicknessMm = 2, BendRadiusMm = 3 });
            string file = ScratchFile("never-saved.dxf");
            var clock = Stopwatch.StartNew();
            var refused = await Refused(() => ExportFlat(new ExportFlatPatternRequest { Document = title, Path = file, Format = FlatPatternFormat.Dxf }));
            long ms = clock.ElapsedMilliseconds;
            Assert.Equal(ErrorCode.InvalidArgument, refused.Code);
            Assert.Equal("document", refused.Error.Details!["field"]);
            Assert.Contains("sw_document_save_as", refused.Message);
            Assert.False(File.Exists(file), "nothing was written");
            var state = await _run.AddinStateAsync();
            Assert.True(state.Dialog == null, $"no dialog open, but '{state.Dialog?.Title}' is");
            return $"{title}: refused in {ms} ms ({refused.Message}); no file; no dialog";
        }
        finally
        {
            await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = title, Discard = true, UsersDocument = true });
        }
    });
}
