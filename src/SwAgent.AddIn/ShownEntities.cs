using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// References for what a drawing view shows (view.entities, an annotation's attachments), and the way back. SP4 hands
/// back proxies for a view's entities whose persistent ids are twins (5.C live run, round 2: the front view's top and
/// bottom edges had one reference, its left and right edges another, and the part resolved none of them), and its
/// correspondence (IView.GetCorresponding, ISldWorks.IsSame against the proxies) verified none (round 3). So the part's
/// own entity for a proxy is the one entity of the part (ISwDocument.Topology, what model.find names) with the proxy's
/// form (kind, shape, feature, length, radius, direction) at the proxy's position. Round 4 measured that position not
/// always in the part's frame: with the view activated, the front view's line and vertex proxies read shifted by the
/// view's position ((130, 180) mm; the corner (60, 40, 20) at (190, 220, 20)) while its circle did not. So a position
/// matches as given or shifted by the one offset the view's proxies share with the part (found by vote over the whole
/// view). Its reference is kept only when it resolves back to that entity and no other entity of the view would be given
/// it. Anything else is not issued: null, never a twin or a guess.
/// <para>
/// An instance reads each view it is asked about once: what the view shows, the part's topology described, and every
/// shown entity's verified reference, so a call that resolves or describes many entities (annotation.list, an annotation
/// on several edges) pays for each view once rather than once per entity (PR #32 review). An instance lives for one read
/// of the drawing and is never kept across a change: activating a view moves its proxies (round 4). Not measured on a
/// part larger than the 60 x 40 x 20 block (at most 116 ms per view there).
/// </para>
/// </summary>
public sealed class ShownEntities
{
    /// <summary>How close two lengths or positions must be to be one entity, mm (the part's own numbers, read twice).</summary>
    const double Tol = 1e-4;

    readonly ReferenceService _references;
    readonly ISwDrawing _drawing;
    readonly Dictionary<string, (ISwDocument Model, List<object> Shown, IReadOnlyList<string?> Refs)> _views = new Dictionary<string, (ISwDocument, List<object>, IReadOnlyList<string?>)>(StringComparer.Ordinal);
    readonly List<(ISwDocument Model, Part Part)> _parts = new List<(ISwDocument, Part)>();

    public ShownEntities(ReferenceService references, ISwDrawing drawing)
    {
        _references = references;
        _drawing = drawing;
    }

    /// <summary>How many times this instance read a view (what it shows and their references): once per view asked about.</summary>
    public int ViewsRead { get; private set; }

    /// <summary>The verified reference for <paramref name="shown"/>, or null.</summary>
    public string? Reference(ISwDocument model, string view, object shown)
    {
        var (all, refs) = Of(model, view);
        int at = all.FindIndex(s => ReferenceEquals(s, shown) || _drawing.SameEntity(s, shown));
        if (at >= 0) return refs[at];
        // Not among what the view lists: judged with them, as one more of the view's entities.
        var with = new List<object>(all) { shown };
        return References(_references, model, _drawing, view, with, PartOf(model))[with.Count - 1];
    }

    /// <summary>
    /// The entity <paramref name="view"/> shows for the part's own <paramref name="entity"/>: the one shown entity whose
    /// verified reference is the part entity's own; null when the view shows none.
    /// </summary>
    public object? InView(ISwDocument model, string view, object entity)
    {
        string? own;
        try
        {
            own = _references.Describe(model, entity).Reference;
        }
        catch (Exception e) when (e is SwAgentException || e is System.Runtime.InteropServices.COMException)
        {
            return null;
        }
        if (own == null) return null;
        var (shown, refs) = Of(model, view);
        for (int i = 0; i < shown.Count; i++)
        {
            if (refs[i] != null && string.Equals(refs[i], own, StringComparison.Ordinal)) return shown[i];
        }
        return null;
    }

    (List<object> Shown, IReadOnlyList<string?> Refs) Of(ISwDocument model, string view)
    {
        if (_views.TryGetValue(view, out var read) && ReferenceEquals(read.Model, model)) return (read.Shown, read.Refs);
        ViewsRead++;
        var shown = Shown(_drawing, view);
        var refs = References(_references, model, _drawing, view, shown, PartOf(model));
        _views[view] = (model, shown, refs);
        return (shown, refs);
    }

    Part PartOf(ISwDocument model)
    {
        foreach (var (m, p) in _parts)
        {
            if (ReferenceEquals(m, model)) return p;
        }
        var part = new Part(model);
        _parts.Add((model, part));
        return part;
    }

    /// <summary>The verified references for each of <paramref name="shown"/>, in order; any two would share are dropped from both.</summary>
    public static IReadOnlyList<string?> References(ReferenceService references, ISwDocument model, ISwDrawing drawing, string view, IReadOnlyList<object> shown) =>
        References(references, model, drawing, view, shown, new Part(model));

    static IReadOnlyList<string?> References(ReferenceService references, ISwDocument model, ISwDrawing drawing, string view, IReadOnlyList<object> shown, Part part)
    {
        var seen = shown.Select(s => Describe(model, s)).ToList();
        var forms = seen.Select(p => p == null ? new List<(object, EntityInfo Info)>() : part.OfKind(p.Kind).Where(c => SameForm(c.Info, p)).ToList()).ToList();
        // Where the proxy is as given wins; only proxies with no entity of their form there vote for the view's offset,
        // which lies in the view's plane (round 4: (130, 180, 0) for the front view), so a front edge and the back edge
        // behind it are never taken for one another.
        var exact = seen.Select((p, i) => p == null ? new List<(object, EntityInfo Info)>() : forms[i].Where(c => At(c.Info, p, null)).ToList()).ToList();
        var facing = drawing.Transform(view) is Core.ViewTransform t ? Cross(Core.ViewGeometry.Right(t), Core.ViewGeometry.Up(t)) : null;
        var offset = facing == null ? null : Offset(seen, forms, exact, facing);
        var refs = new List<string?>();
        for (int i = 0; i < shown.Count; i++)
        {
            var p = seen[i];
            if (p == null)
            {
                refs.Add(null);
                continue;
            }
            var at = exact[i].Count > 0 ? exact[i] : offset == null ? exact[i] : forms[i].Where(c => Shifted(c.Info, p, offset)).ToList();
            refs.Add(at.Count == 1 ? Verified(references, model, at[0].Item1, at[0].Item2) : null);
        }
        var twins = new HashSet<string>(refs.Where(r => r != null).GroupBy(r => r!, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.Ordinal);
        return refs.Select(r => r != null && twins.Contains(r) ? null : r).ToList();
    }

    static List<object> Shown(ISwDrawing drawing, string view) =>
        drawing.VisibleEntities(view, edges: true, vertices: true).Select(v => v.Entity).Concat(drawing.VisibleFaces(view)).ToList();

    /// <summary>
    /// The offset the view's proxies share with the part, beyond none: the most common difference between a proxy's
    /// position and that of a part entity of its form, when more proxies agree on it than on any other; null otherwise.
    /// </summary>
    static Point3? Offset(IReadOnlyList<EntityInfo?> seen, IReadOnlyList<List<(object, EntityInfo Info)>> forms, IReadOnlyList<List<(object, EntityInfo Info)>> exact, Vector3 facing)
    {
        var votes = new List<(Point3 D, int Count)>();
        for (int i = 0; i < seen.Count; i++)
        {
            var p = seen[i]?.Origin;
            if (p == null || exact[i].Count > 0) continue;
            var mine = new List<Point3>();
            foreach (var (_, c) in forms[i])
            {
                if (c.Origin == null) continue;
                var d = new Point3(p.X - c.Origin.X, p.Y - c.Origin.Y, p.Z - c.Origin.Z);
                if (Zero(d) || !InPlane(d, facing) || mine.Any(m => Same(m, d))) continue;
                mine.Add(d);
            }
            foreach (var d in mine)
            {
                int k = votes.FindIndex(v => Same(v.D, d));
                if (k < 0) votes.Add((d, 1));
                else votes[k] = (votes[k].D, votes[k].Count + 1);
            }
        }
        if (votes.Count == 0) return null;
        var best = votes.OrderByDescending(v => v.Count).ToList();
        return best[0].Count >= 2 && (best.Count == 1 || best[1].Count < best[0].Count) ? best[0].D : null;
    }

    /// <summary>True when the proxy <paramref name="p"/> is where the part entity <paramref name="c"/> is, as given.</summary>
    static bool At(EntityInfo c, EntityInfo p, Point3? offset)
    {
        if (c.Origin == null || p.Origin == null) return true;
        var d = new Point3(p.Origin.X - c.Origin.X, p.Origin.Y - c.Origin.Y, p.Origin.Z - c.Origin.Z);
        return Zero(d) || (offset != null && Same(d, offset));
    }

    /// <summary>True when the proxy <paramref name="p"/> is where the part entity <paramref name="c"/> is, shifted by the view's offset.</summary>
    static bool Shifted(EntityInfo c, EntityInfo p, Point3 offset) =>
        c.Origin != null && p.Origin != null && Same(new Point3(p.Origin.X - c.Origin.X, p.Origin.Y - c.Origin.Y, p.Origin.Z - c.Origin.Z), offset);

    static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>True when <paramref name="d"/> lies in the view's plane: nothing of it along the view's facing.</summary>
    static bool InPlane(Point3 d, Vector3 facing)
    {
        double l = Math.Sqrt(facing.X * facing.X + facing.Y * facing.Y + facing.Z * facing.Z);
        return l > 0 && Math.Abs((d.X * facing.X + d.Y * facing.Y + d.Z * facing.Z) / l) <= 1e-3;
    }

    /// <summary>The reference issued for the part's own <paramref name="entity"/> when it resolves back to an entity of its form at its position; null otherwise.</summary>
    static string? Verified(ReferenceService references, ISwDocument model, object entity, EntityInfo info)
    {
        try
        {
            string? reference = references.Describe(model, entity).Reference;
            if (reference == null) return null;
            var back = references.Resolve(model, reference, "").Entity;
            return Describe(model, back) is EntityInfo b && SameForm(b, info) && At(info, b, null) ? reference : null;
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
            if (model.KindOf(entity) is not ReferenceKind kind || !(kind == ReferenceKind.Edge || kind == ReferenceKind.Vertex || kind == ReferenceKind.Face)) return null;
            var info = model.Describe(entity);
            info.Kind = kind;
            return info;
        }
        catch (Exception e) when (e is SwAgentException || e is System.Runtime.InteropServices.COMException || e is InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The part's own faces, edges and vertices with their descriptions, read once per kind.</summary>
    sealed class Part
    {
        readonly ISwDocument _model;
        readonly Dictionary<ReferenceKind, List<(object, EntityInfo Info)>> _own = new Dictionary<ReferenceKind, List<(object, EntityInfo Info)>>();

        public Part(ISwDocument model)
        {
            _model = model;
        }

        public List<(object, EntityInfo Info)> OfKind(ReferenceKind kind)
        {
            if (_own.TryGetValue(kind, out var list)) return list;
            var topology = kind == ReferenceKind.Edge ? TopologyKind.Edge : kind == ReferenceKind.Vertex ? TopologyKind.Vertex : TopologyKind.Face;
            list = new List<(object, EntityInfo Info)>();
            foreach (var e in _model.Topology(topology))
            {
                if (Describe(_model, e) is EntityInfo info) list.Add((e, info));
            }
            _own[kind] = list;
            return list;
        }
    }

    /// <summary>True when two descriptions have one form, wherever they are: kind, shape, feature, length, radius, area and direction.</summary>
    public static bool SameForm(EntityInfo a, EntityInfo b)
    {
        if (a.Kind != b.Kind || a.Geometry != b.Geometry) return false;
        if (a.Feature != null && b.Feature != null && !string.Equals(a.Feature, b.Feature, StringComparison.Ordinal)) return false;
        return Near(a.LengthMm, b.LengthMm) && Near(a.RadiusMm, b.RadiusMm) && Near(a.AreaMm2, b.AreaMm2) && Parallel(a.Axis, b.Axis) && Parallel(a.Normal, b.Normal);
    }

    static bool Near(double? a, double? b) => a == null || b == null || Math.Abs(a.Value - b.Value) <= Tol;

    static bool Zero(Point3 d) => Math.Abs(d.X) <= Tol && Math.Abs(d.Y) <= Tol && Math.Abs(d.Z) <= Tol;

    static bool Same(Point3 a, Point3 b) => Math.Abs(a.X - b.X) <= 1e-3 && Math.Abs(a.Y - b.Y) <= 1e-3 && Math.Abs(a.Z - b.Z) <= 1e-3;

    static bool Parallel(Vector3? a, Vector3? b)
    {
        if (a == null || b == null) return true;
        double la = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z), lb = Math.Sqrt(b.X * b.X + b.Y * b.Y + b.Z * b.Z);
        if (la == 0 || lb == 0) return la == lb;
        return Math.Abs(Math.Abs((a.X * b.X + a.Y * b.Y + a.Z * b.Z) / (la * lb)) - 1) <= 1e-9;
    }
}
