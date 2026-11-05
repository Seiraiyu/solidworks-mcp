using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class SketchCreateOperation : ModelChangeOperation<SketchCreateRequest, SketchCreateResult>
{
    public SketchCreateOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(SketchCreateRequest request) => request.Document;

    protected override SketchCreateResult Change(ISwDocument doc, SketchCreateRequest request, ModelTransaction tx, IOperationContext context)
    {
        var draft = SketchDraft.Check(request.Geometry(), "");
        if (request.Name != null) Sketches.CheckNewName(doc, request.Name, "name");
        var (on, kind) = Services.References.Resolve(doc, request.On, "on", ReferenceKind.Plane, ReferenceKind.Face);
        if (kind == ReferenceKind.Face && doc.Describe(on).Geometry != GeometryType.Plane)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument, $"on: a sketch needs a plane or a planar face, and this is a {WireNames.ForEnum(doc.Describe(on).Geometry)} face",
                new JsonObject { ["field"] = "on" });
        }
        var external = Sketches.ResolveExternal(Services.References, doc, draft);

        var editor = doc.NewSketch(on);
        var made = new CreatedItem { Name = editor.Name, Kind = ReferenceKind.Sketch };
        tx.Made(made, () =>
        {
            doc.DeleteFeature(editor.Sketch);
            doc.Rebuild(false);
        });
        BuiltSketch built;
        try
        {
            if (request.Name != null) editor.Rename(request.Name);
            made.Name = editor.Name;
            built = SketchBuilder.Draw(editor, draft, t => external[t.Field]);
        }
        finally
        {
            editor.Close();
        }
        var result = new SketchCreateResult();
        Sketches.Fill(Services.References, doc, editor.Sketch, built, result);
        made.Reference = result.Sketch.Reference;
        return result;
    }

    /// <summary>The sketch holds what was asked, each dimension reads back at its value, and it is not over defined.</summary>
    protected override void Check(ISwDocument doc, SketchCreateRequest request, SketchCreateResult result)
    {
        var draft = SketchDraft.Check(request.Geometry(), "");
        var sketch = doc.FeatureByName(result.Sketch.Name)
            ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"the sketch {result.Sketch.Name} is not in the part after it was closed");
        var contents = doc.ReadSketch(sketch);
        Sketches.CheckCounts(contents, null, draft.Expected);
        Sketches.CheckDimensions(contents, draft.Dimensions, result.Dimensions);
        Sketches.CheckSolved(contents);
    }
}

public sealed class SketchReadOperation : Operation<SketchReadRequest, SketchReadResult>
{
    readonly AddInServices _services;

    public SketchReadOperation(AddInServices services)
    {
        _services = services;
    }

    protected override SketchReadResult Run(SketchReadRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.FindPart(_services.SolidWorks, request.Document, "sketch.read");
        var (sketch, _) = _services.References.Resolve(doc, request.Sketch, "sketch", ReferenceKind.Sketch);
        var c = doc.ReadSketch(sketch);
        var references = _services.References;
        string Name(object entity)
        {
            if (entity is SketchOrigin || doc.KindOf(entity) == null) return entity is SketchOrigin ? "origin" : "?";
            var info = references.Describe(doc, entity);
            return info.Reference ?? info.Name ?? "?";
        }
        return new SketchReadResult
        {
            Document = doc.Info(),
            Sketch = Sketches.Summary(references, doc, c),
            Segments = c.Segments.Select(s => new SketchSegmentInfo
            {
                Kind = s.Kind,
                Name = s.Name,
                Reference = references.Describe(doc, s.Entity).Reference,
                Construction = s.Construction,
                Start = s.Start,
                End = s.End,
                Center = s.Center,
                RadiusMm = s.RadiusMm,
                LengthMm = s.LengthMm,
            }).ToList(),
            Points = c.Points.Select(p => new SketchPointInfo { Reference = references.Describe(doc, p.Entity).Reference, At = p.At }).ToList(),
            Relations = c.Relations.Select(r => new SketchRelationInfo { Type = SolidWorksCodes.RelationName(r.Type), Entities = r.Entities.Select(Name).ToList() }).ToList(),
            Dimensions = c.Dimensions.Select(d => Sketches.Info(references, doc, d)).ToList(),
        };
    }

    /// <summary>Read again, the sketch has the same segments and dimensions.</summary>
    protected override void Verify(SketchReadRequest request, SketchReadResult result, IOperationContext context)
    {
        var doc = DocumentLocator.Find(_services.SolidWorks, DocumentKeys.Of(result.Document));
        var sketch = doc.FeatureByName(result.Sketch.Name)
            ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"{result.Sketch.Name} is gone; ask again");
        var again = doc.ReadSketch(sketch);
        if (again.Segments.Count != result.Segments.Count || !again.Dimensions.Select(d => d.FullName).SequenceEqual(result.Dimensions.Select(d => d.Name)))
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{result.Sketch.Name} changed while it was read; ask again");
    }
}

/// <summary>What the sketch operations share: names, external references, filling a result, and the checks on a sketch read back.</summary>
public static class Sketches
{
    public static void CheckNewName(ISwDocument doc, string name, string field)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.IndexOfAny(new[] { '@', '/', '\\', '"' }) >= 0)
            throw new SwAgentException(ErrorCode.InvalidArgument, $"{field}: '{name}' is not a feature name SOLIDWORKS can take (1 to 64 characters, no @ / \\ \")", new JsonObject { ["field"] = field });
        if (doc.FeatureByName(name) != null)
            throw new SwAgentException(ErrorCode.InvalidArgument, $"{field}: the part already has a feature named '{name}'", new JsonObject { ["field"] = field });
    }

    /// <summary>Every reference a relation or dimension names, resolved before anything is drawn, so a bad one changes nothing.</summary>
    public static Dictionary<string, object> ResolveExternal(ReferenceService references, ISwDocument doc, SketchDraft draft) =>
        draft.Relations.SelectMany(r => r.Targets).Concat(draft.Dimensions.SelectMany(d => d.Targets))
            .Where(t => t.IsReference)
            .ToDictionary(t => t.Field, t => references.Resolve(doc, t.Text, t.Field,
                ReferenceKind.Edge, ReferenceKind.Vertex, ReferenceKind.SketchEntity, ReferenceKind.SketchPoint).Entity, StringComparer.Ordinal);

    public static void Fill(ReferenceService references, ISwDocument doc, object sketch, BuiltSketch built, SketchCreateResult result)
    {
        var contents = doc.ReadSketch(sketch);
        result.Sketch = Summary(references, doc, contents);
        result.Ids = built.Order.Select(id => new SketchIdReferences
        {
            Id = id,
            References = built.Drawn[id].Select(e => references.Describe(doc, e).Reference).Where(r => r != null).Select(r => r!).ToList(),
        }).ToList();
        result.Dimensions = built.Dimensions.Select(name =>
        {
            var record = contents.Dimensions.FirstOrDefault(d => string.Equals(d.FullName, name, StringComparison.OrdinalIgnoreCase));
            return record == null ? new DimensionInfo { Name = name } : Info(references, doc, record);
        }).ToList();
    }

    public static SketchSummary Summary(ReferenceService references, ISwDocument doc, SketchContents c) => new SketchSummary
    {
        Name = c.Name,
        Reference = references.Describe(doc, c.Feature).Reference,
        Solve = c.Solve,
        Origin = c.Origin,
        XAxis = c.XAxis,
        YAxis = c.YAxis,
        Normal = c.Normal,
    };

    public static DimensionInfo Info(ReferenceService references, ISwDocument doc, DimensionRecord d)
    {
        string? reference = null;
        if (doc.KindOf(d.Dimension) != null) reference = references.Describe(doc, d.Dimension).Reference;
        return new DimensionInfo
        {
            Name = d.FullName,
            Value = d.Value,
            Unit = d.Angle ? "deg" : "mm",
            Driving = d.Driving,
            Feature = d.Feature,
            Reference = reference,
        };
    }

    public static SketchCounts Count(SketchContents c) => new SketchCounts
    {
        Lines = c.Segments.Count(s => s.Kind == SketchSegmentKind.Line && !s.Construction),
        ConstructionLines = c.Segments.Count(s => s.Kind == SketchSegmentKind.Line && s.Construction),
        Arcs = c.Segments.Count(s => s.Kind == SketchSegmentKind.Arc && !s.Construction),
        Circles = c.Segments.Count(s => s.Kind == SketchSegmentKind.Circle && !s.Construction),
        Splines = c.Segments.Count(s => s.Kind == SketchSegmentKind.Spline && !s.Construction),
    };

    /// <summary>The sketch gained (since <paramref name="before"/>, or in all) exactly the segments asked for; construction lines at least as many.</summary>
    public static void CheckCounts(SketchContents after, SketchCounts? before, SketchCounts expected)
    {
        var now = Count(after);
        var was = before ?? new SketchCounts();
        var wrong = new List<string>();
        void Exactly(string what, int measured, int wanted)
        {
            if (measured != wanted) wrong.Add($"{measured} {what}, not {wanted}");
        }
        Exactly("lines", now.Lines - was.Lines, expected.Lines);
        Exactly("arcs", now.Arcs - was.Arcs, expected.Arcs);
        Exactly("circles", now.Circles - was.Circles, expected.Circles);
        Exactly("splines", now.Splines - was.Splines, expected.Splines);
        if (now.ConstructionLines - was.ConstructionLines < expected.ConstructionLines)
            wrong.Add($"{now.ConstructionLines - was.ConstructionLines} construction lines, not at least {expected.ConstructionLines}");
        if (wrong.Count > 0)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{after.Name} holds {string.Join(", ", wrong)}",
                new JsonObject { ["check"] = "entity_counts", ["measured"] = wrong.Select(w => (object?)w).ToList() });
        }
    }

    /// <summary>Each dimension the request added is in the sketch, drives it, and reads back at its value.</summary>
    public static void CheckDimensions(SketchContents c, IReadOnlyList<DraftDimension> asked, IReadOnlyList<DimensionInfo> made)
    {
        for (int i = 0; i < asked.Count; i++)
        {
            string name = i < made.Count ? made[i].Name : "?";
            var record = c.Dimensions.FirstOrDefault(d => string.Equals(d.FullName, name, StringComparison.OrdinalIgnoreCase));
            if (record == null)
                throw new SwAgentException(ErrorCode.VerificationFailed, $"{asked[i].Field}: the dimension {name} is not in {c.Name}", new JsonObject { ["field"] = asked[i].Field, ["check"] = "dimension" });
            if (!record.Driving)
                throw new SwAgentException(ErrorCode.VerificationFailed, $"{asked[i].Field}: {name} was added as a driven dimension, so it over-defines {c.Name}", new JsonObject { ["field"] = asked[i].Field, ["check"] = "dimension_driving" });
            if (Math.Abs(record.Value - asked[i].Value) > 1e-6 * Math.Max(1, Math.Abs(asked[i].Value)))
            {
                throw new SwAgentException(ErrorCode.VerificationFailed, $"{asked[i].Field}: {name} reads {record.Value} {(record.Angle ? "deg" : "mm")}, not {asked[i].Value}",
                    new JsonObject { ["field"] = asked[i].Field, ["check"] = "dimension_value", ["expected"] = asked[i].Value, ["measured"] = record.Value });
            }
        }
    }

    /// <summary>Over defined, or a sketch with no solution, is a failed change; under defined is allowed.</summary>
    public static void CheckSolved(SketchContents c)
    {
        if (c.Solve == SolveState.OverDefined || c.Solve == SolveState.NoSolution)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{c.Name} is {WireNames.ForEnum(c.Solve).Replace('_', ' ')}: remove a relation or dimension that conflicts",
                new JsonObject { ["check"] = "solve", ["solve"] = WireNames.ForEnum(c.Solve) });
        }
    }
}
