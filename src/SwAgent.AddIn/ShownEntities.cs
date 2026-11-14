using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// References for what a drawing view shows (view.entities, an annotation's attachments), and the way back. SP4 hands
/// back proxies for a view's entities whose persistent ids are twins (5.C live run, round 2: the front view's top and
/// bottom edges had one reference, its left and right edges another, and the part resolved none of them), and round 3
/// found SP4's correspondence (IView.GetCorresponding, ISldWorks.IsSame against the proxies) verifying none. So the
/// part's own entity for a proxy is the one entity of the part (ISwDocument.Topology, what model.find names, or what
/// SOLIDWORKS' correspondence offers) whose description, in the part's coordinates, is the proxy's: same kind, shape,
/// feature, position, direction and size. Its reference is kept only when it resolves back to that shape and no other
/// entity of the view would be given it. Anything else is not issued: null, never a twin or a guess.
/// </summary>
public static class ShownEntities
{
    /// <summary>How close two descriptions' lengths and positions must be to be one entity, mm (the part's own numbers, read twice).</summary>
    const double Tol = 1e-4;

    /// <summary>The verified reference for <paramref name="shown"/>, or null.</summary>
    public static string? Reference(ReferenceService references, ISwDocument model, ISwDrawing drawing, string view, object shown) =>
        References(references, model, drawing, view, new[] { shown })[0];

    /// <summary>The verified references for each of <paramref name="shown"/>, in order; any two would share are dropped from both.</summary>
    public static IReadOnlyList<string?> References(ReferenceService references, ISwDocument model, ISwDrawing drawing, string view, IReadOnlyList<object> shown)
    {
        var own = new Dictionary<ReferenceKind, List<(object Entity, EntityInfo Info)>>();
        List<(object, EntityInfo)> Own(ReferenceKind kind)
        {
            if (own.TryGetValue(kind, out var list)) return list;
            var topology = kind == ReferenceKind.Edge ? TopologyKind.Edge : kind == ReferenceKind.Vertex ? TopologyKind.Vertex : TopologyKind.Face;
            list = model.Topology(topology).Select(e => (e, Describe(model, e))).Where(x => x.Item2 != null).Select(x => (x.e, x.Item2!)).ToList();
            own[kind] = list;
            return list;
        }

        var refs = new List<string?>();
        foreach (var s in shown)
        {
            var seen = Describe(model, s);
            if (seen == null || !(seen.Kind == ReferenceKind.Edge || seen.Kind == ReferenceKind.Vertex || seen.Kind == ReferenceKind.Face))
            {
                refs.Add(null);
                continue;
            }
            var candidates = drawing.ModelEntities(view, s).Select(e => (e, Describe(model, e))).Where(x => x.Item2 != null).Select(x => (x.e, x.Item2!))
                .Concat(Own(seen.Kind)).Where(c => SameShape(c.Item2, seen)).ToList();
            var issued = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (entity, _) in candidates)
            {
                if (Verified(references, model, entity, seen) is string r) issued.Add(r);
            }
            refs.Add(issued.Count == 1 ? issued.First() : null);
        }
        var twins = new HashSet<string>(refs.Where(r => r != null).GroupBy(r => r!, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.Ordinal);
        return refs.Select(r => r != null && twins.Contains(r) ? null : r).ToList();
    }

    /// <summary>
    /// The entity <paramref name="view"/> shows for the part's own <paramref name="entity"/>: SOLIDWORKS' correspondence
    /// (ISwDrawing.InView) when it has the same shape, otherwise the one entity the view shows with that shape; null when
    /// the view shows none, or more than one.
    /// </summary>
    public static object? InView(ISwDocument model, ISwDrawing drawing, string view, object entity)
    {
        var want = Describe(model, entity);
        if (want == null) return null;
        if (drawing.InView(view, entity) is object drawn && Describe(model, drawn) is EntityInfo d && SameShape(d, want)) return drawn;
        var shown = drawing.VisibleEntities(view, edges: true, vertices: true).Select(v => v.Entity).Concat(drawing.VisibleFaces(view));
        var same = shown.Where(s => Describe(model, s) is EntityInfo i && SameShape(i, want)).ToList();
        return same.Count == 1 ? same[0] : null;
    }

    /// <summary>The reference issued for <paramref name="entity"/> when it resolves back to an entity of <paramref name="seen"/>'s shape; null otherwise.</summary>
    static string? Verified(ReferenceService references, ISwDocument model, object entity, EntityInfo seen)
    {
        try
        {
            string? reference = references.Describe(model, entity).Reference;
            if (reference == null) return null;
            var back = references.Resolve(model, reference, "").Entity;
            return Describe(model, back) is EntityInfo b && SameShape(b, seen) ? reference : null;
        }
        catch (Exception e) when (e is SwAgentException || e is System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    static EntityInfo? Describe(ISwDocument model, object entity)
    {
        try
        {
            if (model.KindOf(entity) is not ReferenceKind kind) return null;
            var info = model.Describe(entity);
            info.Kind = kind;
            return info;
        }
        catch (Exception e) when (e is SwAgentException || e is System.Runtime.InteropServices.COMException || e is InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>True when two descriptions are of one entity: kind, shape and feature, and every position, direction and size both give.</summary>
    public static bool SameShape(EntityInfo a, EntityInfo b)
    {
        if (a.Kind != b.Kind || a.Geometry != b.Geometry) return false;
        if (a.Feature != null && b.Feature != null && !string.Equals(a.Feature, b.Feature, StringComparison.Ordinal)) return false;
        return Near(a.LengthMm, b.LengthMm) && Near(a.RadiusMm, b.RadiusMm) && Near(a.AreaMm2, b.AreaMm2)
            && Near(a.Origin, b.Origin) && Parallel(a.Axis, b.Axis) && Parallel(a.Normal, b.Normal)
            && Near(a.Box?.Min, b.Box?.Min) && Near(a.Box?.Max, b.Box?.Max);
    }

    static bool Near(double? a, double? b) => a == null || b == null || Math.Abs(a.Value - b.Value) <= Tol;

    static bool Near(Point3? a, Point3? b) => a == null || b == null || (Math.Abs(a.X - b.X) <= Tol && Math.Abs(a.Y - b.Y) <= Tol && Math.Abs(a.Z - b.Z) <= Tol);

    static bool Parallel(Vector3? a, Vector3? b)
    {
        if (a == null || b == null) return true;
        double la = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z), lb = Math.Sqrt(b.X * b.X + b.Y * b.Y + b.Z * b.Z);
        if (la == 0 || lb == 0) return la == lb;
        return Math.Abs(Math.Abs((a.X * b.X + a.Y * b.Y + a.Z * b.Z) / (la * lb)) - 1) <= 1e-9;
    }
}
