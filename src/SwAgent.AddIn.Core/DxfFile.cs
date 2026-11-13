using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SwAgent.AddIn.Core;

/// <summary>A rectangle in a drawing's plane, in the drawing's own units (or millimetres once scaled).</summary>
public readonly struct DxfBox
{
    public DxfBox(double minX, double minY, double maxX, double maxY)
    {
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
    }

    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;

    public DxfBox Scaled(double factor) => new DxfBox(MinX * factor, MinY * factor, MaxX * factor, MaxY * factor);

    public override string ToString() => FormattableString.Invariant($"({MinX:0.####}, {MinY:0.####}) to ({MaxX:0.####}, {MaxY:0.####})");
}

/// <summary>
/// What an ASCII DXF holds, as export.flat_pattern checks it (design 454, Flat-pattern export): the drawing extents
/// from the header's $EXTMIN and $EXTMAX, the extents of the entities themselves, the units ($INSUNITS), how
/// many entities lie on each layer and linetype, and the bend lines, found by their geometry. Only the ENTITIES
/// section is measured: blocks and what INSERT, TEXT and other annotation entities place are counted, not measured.
/// </summary>
public sealed class DxfFile
{
    /// <summary>
    /// An entity whose layer or linetype name holds this (case-insensitive) is counted in <see cref="NamedBendLines"/>.
    /// The 6.D live run found SOLIDWORKS 2026 SP4 draws its bend lines on layer 0, linetype Continuous, like the
    /// outline, so the name is reported, and <see cref="BendLines"/> goes by geometry.
    /// </summary>
    public const string BendMarker = "BEND";

    /// <summary>A straight piece of an entity (a LINE, or a polyline's straight segment), and the entity it belongs to.</summary>
    readonly struct Segment
    {
        public Segment(int entity, bool line, double x1, double y1, double x2, double y2)
        {
            Entity = entity;
            Line = line;
            X1 = x1;
            Y1 = y1;
            X2 = x2;
            Y2 = y2;
        }

        public int Entity { get; }
        public bool Line { get; }
        public double X1 { get; }
        public double Y1 { get; }
        public double X2 { get; }
        public double Y2 { get; }
    }

    /// <summary>A circular piece of an entity (an ARC, a CIRCLE, a polyline's bulge): counter-clockwise from Start, Sweep degrees.</summary>
    readonly struct Curve
    {
        public Curve(int entity, double cx, double cy, double r, double startDeg, double sweepDeg)
        {
            Entity = entity;
            Cx = cx;
            Cy = cy;
            R = r;
            StartDeg = startDeg;
            SweepDeg = sweepDeg;
        }

        public int Entity { get; }
        public double Cx { get; }
        public double Cy { get; }
        public double R { get; }
        public double StartDeg { get; }
        public double SweepDeg { get; }
    }

    readonly List<Segment> _segments = new List<Segment>();
    readonly List<Curve> _curves = new List<Curve>();

    /// <summary>The ends of every segment and curve that has ends (a circle has none), with their entity.</summary>
    readonly List<(int Entity, double X, double Y)> _ends = new List<(int, double, double)>();

    /// <summary>Header extents at or beyond this are AutoCAD's "never set" values (1e20 and -1e20).</summary>
    const double Unset = 1e19;

    double _minX = double.PositiveInfinity, _minY = double.PositiveInfinity, _maxX = double.NegativeInfinity, _maxY = double.NegativeInfinity;

    DxfFile()
    {
    }

    /// <summary>$EXTMIN and $EXTMAX, or null when the header does not give both or they were never set.</summary>
    public DxfBox? HeaderExtents { get; private set; }

    /// <summary>The extents of the measured entities, or null when there are none.</summary>
    public DxfBox? EntityExtents => Entities == 0 ? null : new DxfBox(_minX, _minY, _maxX, _maxY);

    /// <summary>$INSUNITS, or null when the header does not give it.</summary>
    public int? Units { get; private set; }

    /// <summary>How many entities were measured: lines, arcs, circles, polylines, splines and ellipses.</summary>
    public int Entities { get; private set; }

    /// <summary>How many of them are splines, whose extents are taken from their control points (a bound, not exact).</summary>
    public int Splines { get; private set; }

    /// <summary>How many measured entities lie on a layer or linetype named for bends (<see cref="BendMarker"/>).</summary>
    public int NamedBendLines { get; private set; }

    /// <summary>
    /// How many LINEs run across the outline: each end lies on another entity (inside one of its edges, or where the
    /// outline is split into two lines in line with each other and not with it), and its middle lies on none. A
    /// flat pattern's bend lines are such chords whatever layer they are on; a bend cut by a hole counts once per piece.
    /// Lines along the outline, and sketch lines that do not reach it at both ends, are not counted.
    /// </summary>
    public int BendLines { get; private set; }

    /// <summary>Entities per layer (code 8; "0" when absent).</summary>
    public SortedDictionary<string, int> Layers { get; } = new SortedDictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Entities per linetype (code 6; BYLAYER when absent).</summary>
    public SortedDictionary<string, int> Linetypes { get; } = new SortedDictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Entities counted but not measured, by type (TEXT, MTEXT, INSERT, POINT, DIMENSION and the like).</summary>
    public SortedDictionary<string, int> Skipped { get; } = new SortedDictionary<string, int>(StringComparer.Ordinal);

    public static DxfFile Read(string path) => Parse(File.ReadAllText(path));

    /// <summary>Reads an ASCII DXF; FormatException saying what is wrong when it is not one that can be read.</summary>
    public static DxfFile Parse(string text)
    {
        if (text.StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal))
            throw new FormatException("it is a binary DXF, which is not read; SOLIDWORKS writes ASCII DXF");
        var pairs = Pairs(text);
        var dxf = new DxfFile();
        bool entities = false;
        string? section = null;
        for (int i = 0; i < pairs.Count; i++)
        {
            var (code, value) = pairs[i];
            if (code != 0 && !(section == "HEADER" && code == 9)) continue;
            if (code == 0 && value == "EOF") break;
            if (code == 0 && value == "SECTION")
            {
                section = i + 1 < pairs.Count && pairs[i + 1].Code == 2 ? pairs[i + 1].Value : null;
                if (section == "ENTITIES") entities = true;
                continue;
            }
            if (code == 0 && value == "ENDSEC")
            {
                section = null;
                continue;
            }
            if (section == "HEADER" && code == 9)
            {
                dxf.Header(value, Group(pairs, i, stopAtNine: true));
            }
            else if (section == "ENTITIES" && code == 0)
            {
                if (value == "POLYLINE")
                    i = dxf.Polyline(pairs, i);
                else
                    dxf.Entity(value, Group(pairs, i, stopAtNine: false));
            }
        }
        if (!entities) throw new FormatException("it has no ENTITIES section");
        dxf.BendLines = dxf.Chords();
        return dxf;
    }

    /// <summary>How close two points are to be one, in drawing units: a millionth of the drawing's size, at least 1e-6.</summary>
    double Eps => 1e-6 * Math.Max(1, Entities == 0 ? 1 : Math.Max(_maxX - _minX, _maxY - _minY));

    int Chords()
    {
        double eps = Eps;
        int count = 0;
        foreach (var l in _segments.Where(s => s.Line))
        {
            if (Distance(l.X1, l.Y1, l.X2, l.Y2) <= eps) continue;
            double mx = (l.X1 + l.X2) / 2, my = (l.Y1 + l.Y2) / 2;
            if (OnAnother(l.Entity, mx, my, eps, inside: false)) continue;
            if (EndsOnOutline(l, l.X1, l.Y1, eps) && EndsOnOutline(l, l.X2, l.Y2, eps)) count++;
        }
        return count;
    }

    /// <summary>The end (x, y) of line <paramref name="l"/> meets the outline: inside another's edge, or at a split of two lines in line.</summary>
    bool EndsOnOutline(Segment l, double x, double y, double eps)
    {
        var others = _ends.Where(e => e.Entity != l.Entity && Distance(e.X, e.Y, x, y) <= eps).ToList();
        if (others.Count == 0) return OnAnother(l.Entity, x, y, eps, inside: true);
        if (others.Count != 2) return false;
        var meeting = _segments.Where(s => s.Entity != l.Entity && (Distance(s.X1, s.Y1, x, y) <= eps || Distance(s.X2, s.Y2, x, y) <= eps)).ToList();
        if (meeting.Count != 2) return false;
        return Parallel(meeting[0], meeting[1]) && !Parallel(meeting[0], l);
    }

    static bool Parallel(Segment a, Segment b)
    {
        double ax = a.X2 - a.X1, ay = a.Y2 - a.Y1, bx = b.X2 - b.X1, by = b.Y2 - b.Y1;
        double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
        return la > 0 && lb > 0 && Math.Abs(ax * by - ay * bx) / (la * lb) < 1e-9;
    }

    /// <summary>(x, y) lies on an entity other than <paramref name="entity"/>; with <paramref name="inside"/>, away from that piece's ends.</summary>
    bool OnAnother(int entity, double x, double y, double eps, bool inside)
    {
        foreach (var s in _segments)
        {
            if (s.Entity == entity) continue;
            if (inside && (Distance(s.X1, s.Y1, x, y) <= eps || Distance(s.X2, s.Y2, x, y) <= eps)) continue;
            if (ToSegment(s, x, y) <= eps) return true;
        }
        foreach (var c in _curves)
        {
            if (c.Entity == entity) continue;
            if (Math.Abs(Distance(c.Cx, c.Cy, x, y) - c.R) > eps) continue;
            double at = Normal(Math.Atan2(y - c.Cy, x - c.Cx) * 180 / Math.PI - c.StartDeg);
            if (at <= c.SweepDeg + 1e-9 || c.SweepDeg >= 360) return true;
        }
        return false;
    }

    static double Distance(double x1, double y1, double x2, double y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

    static double ToSegment(Segment s, double x, double y)
    {
        double dx = s.X2 - s.X1, dy = s.Y2 - s.Y1, len2 = dx * dx + dy * dy;
        double t = len2 == 0 ? 0 : Math.Max(0, Math.Min(1, ((x - s.X1) * dx + (y - s.Y1) * dy) / len2));
        return Distance(s.X1 + t * dx, s.Y1 + t * dy, x, y);
    }

    void AddSegment(bool line, double x1, double y1, double x2, double y2)
    {
        _segments.Add(new Segment(Entities, line, x1, y1, x2, y2));
        _ends.Add((Entities, x1, y1));
        _ends.Add((Entities, x2, y2));
    }

    void AddCurve(double cx, double cy, double r, double startDeg, double sweepDeg, bool ends)
    {
        _curves.Add(new Curve(Entities, cx, cy, r, startDeg, sweepDeg));
        if (!ends) return;
        double a = startDeg * Math.PI / 180, b = (startDeg + sweepDeg) * Math.PI / 180;
        _ends.Add((Entities, cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        _ends.Add((Entities, cx + r * Math.Cos(b), cy + r * Math.Sin(b)));
    }

    /// <summary>Millimetres per drawing unit for $INSUNITS; false for unitless (0) and units not read here.</summary>
    public static bool TryMmPer(int units, out double mm)
    {
        switch (units)
        {
            case 1: mm = 25.4; return true;
            case 2: mm = 304.8; return true;
            case 4: mm = 1; return true;
            case 5: mm = 10; return true;
            case 6: mm = 1000; return true;
            default: mm = 0; return false;
        }
    }

    static List<(int Code, string Value)> Pairs(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count % 2 != 0) throw new FormatException($"its {lines.Count} lines do not pair up as group codes and values");
        var pairs = new List<(int, string)>(lines.Count / 2);
        for (int i = 0; i < lines.Count; i += 2)
        {
            if (!int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                throw new FormatException($"line {i + 1} is not a group code: '{Short(lines[i])}'");
            pairs.Add((code, lines[i + 1].Trim()));
        }
        return pairs;
    }

    /// <summary>The pairs after <paramref name="start"/> up to the next code 0 (and, in the header, the next code 9).</summary>
    static List<(int Code, string Value)> Group(List<(int Code, string Value)> pairs, int start, bool stopAtNine)
    {
        var group = new List<(int, string)>();
        for (int j = start + 1; j < pairs.Count && pairs[j].Code != 0 && !(stopAtNine && pairs[j].Code == 9); j++) group.Add(pairs[j]);
        return group;
    }

    static double Number(string type, int code, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : throw new FormatException($"{type}'s group code {code} is not a number: '{Short(value)}'");

    static double? First(string type, List<(int Code, string Value)> group, int code)
    {
        foreach (var (c, v) in group)
        {
            if (c == code) return Number(type, c, v);
        }
        return null;
    }

    static string Short(string value) => value.Length > 40 ? value.Substring(0, 40) + "…" : value;

    void Header(string name, List<(int Code, string Value)> group)
    {
        if (name == "$INSUNITS")
        {
            if (First(name, group, 70) is double u) Units = (int)u;
            return;
        }
        if (name != "$EXTMIN" && name != "$EXTMAX") return;
        double? x = First(name, group, 10), y = First(name, group, 20);
        if (x == null || y == null) return;
        _header[name] = (x.Value, y.Value);
        if (_header.TryGetValue("$EXTMIN", out var min) && _header.TryGetValue("$EXTMAX", out var max))
        {
            bool set = Math.Abs(min.X) < Unset && Math.Abs(min.Y) < Unset && Math.Abs(max.X) < Unset && Math.Abs(max.Y) < Unset && max.X >= min.X && max.Y >= min.Y;
            HeaderExtents = set ? new DxfBox(min.X, min.Y, max.X, max.Y) : (DxfBox?)null;
        }
    }

    readonly Dictionary<string, (double X, double Y)> _header = new Dictionary<string, (double, double)>(StringComparer.Ordinal);

    /// <summary>Counts the entity on its layer and linetype; true when either is named for bends.</summary>
    bool Count(List<(int Code, string Value)> group)
    {
        string layer = group.FirstOrDefault(p => p.Code == 8).Value ?? "0";
        string linetype = group.FirstOrDefault(p => p.Code == 6).Value ?? "BYLAYER";
        Layers[layer] = (Layers.TryGetValue(layer, out int l) ? l : 0) + 1;
        Linetypes[linetype] = (Linetypes.TryGetValue(linetype, out int t) ? t : 0) + 1;
        return layer.IndexOf(BendMarker, StringComparison.OrdinalIgnoreCase) >= 0 || linetype.IndexOf(BendMarker, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    void Entity(string type, List<(int Code, string Value)> group)
    {
        bool bend = Count(group);
        switch (type)
        {
            case "LINE":
            {
                double x1 = Need(type, group, 10), y1 = Need(type, group, 20), x2 = Need(type, group, 11), y2 = Need(type, group, 21);
                Add(x1, y1);
                Add(x2, y2);
                AddSegment(true, x1, y1, x2, y2);
                break;
            }
            case "CIRCLE":
            {
                double cx = Need(type, group, 10), cy = Need(type, group, 20), r = Need(type, group, 40);
                Add(cx - r, cy - r);
                Add(cx + r, cy + r);
                AddCurve(cx, cy, r, 0, 360, ends: false);
                break;
            }
            case "ARC":
            {
                double cx = Need(type, group, 10), cy = Need(type, group, 20), r = Need(type, group, 40), from = Need(type, group, 50), to = Need(type, group, 51);
                Arc(cx, cy, r, from, to);
                double sweep = Normal(to - from);
                AddCurve(cx, cy, r, from, sweep == 0 ? 360 : sweep, ends: true);
                break;
            }
            case "LWPOLYLINE":
            {
                var vertices = new List<(double X, double Y, double Bulge)>();
                foreach (var (c, v) in group)
                {
                    if (c == 10) vertices.Add((Number(type, c, v), double.NaN, 0));
                    else if (c == 20 && vertices.Count > 0) vertices[vertices.Count - 1] = (vertices[vertices.Count - 1].X, Number(type, c, v), 0);
                    else if (c == 42 && vertices.Count > 0) vertices[vertices.Count - 1] = (vertices[vertices.Count - 1].X, vertices[vertices.Count - 1].Y, Number(type, c, v));
                }
                bool closed = ((int)(First(type, group, 70) ?? 0) & 1) != 0;
                Polyline(type, vertices, closed);
                break;
            }
            case "SPLINE":
            {
                double? x = null;
                foreach (var (c, v) in group)
                {
                    if (c == 10) x = Number(type, c, v);
                    else if (c == 20 && x != null) Add(x.Value, Number(type, c, v));
                }
                Splines++;
                break;
            }
            case "ELLIPSE":
                Ellipse(type, group);
                break;
            default:
                Skipped[type] = (Skipped.TryGetValue(type, out int s) ? s : 0) + 1;
                return;
        }
        Entities++;
        if (bend) NamedBendLines++;
    }

    /// <summary>A POLYLINE and its VERTEX entities up to SEQEND, measured as one entity; the index of its last pair.</summary>
    int Polyline(List<(int Code, string Value)> pairs, int start)
    {
        const string type = "POLYLINE";
        var head = Group(pairs, start, stopAtNine: false);
        bool bend = Count(head);
        bool closed = ((int)(First(type, head, 70) ?? 0) & 1) != 0;
        var vertices = new List<(double X, double Y, double Bulge)>();
        int i = start + 1 + head.Count;
        while (i < pairs.Count && pairs[i].Code == 0 && pairs[i].Value == "VERTEX")
        {
            var vertex = Group(pairs, i, stopAtNine: false);
            vertices.Add((Need("VERTEX", vertex, 10), Need("VERTEX", vertex, 20), First("VERTEX", vertex, 42) ?? 0));
            i += 1 + vertex.Count;
        }
        if (i < pairs.Count && pairs[i].Code == 0 && pairs[i].Value == "SEQEND") i += Group(pairs, i, stopAtNine: false).Count;
        else i--;
        Polyline(type, vertices, closed);
        Entities++;
        if (bend) NamedBendLines++;
        return i;
    }

    void Polyline(string type, List<(double X, double Y, double Bulge)> vertices, bool closed)
    {
        if (vertices.Any(v => double.IsNaN(v.Y))) throw new FormatException($"{type} has a vertex without its y (code 20)");
        for (int k = 0; k < vertices.Count; k++)
        {
            var p = vertices[k];
            Add(p.X, p.Y);
            bool last = k == vertices.Count - 1;
            if (last && !closed) break;
            var q = vertices[last ? 0 : k + 1];
            if (p.Bulge != 0) Bulge(p.X, p.Y, q.X, q.Y, p.Bulge);
            else AddSegment(false, p.X, p.Y, q.X, q.Y);
        }
    }

    /// <summary>The arc from (x1, y1) to (x2, y2) of bulge b (the tangent of a quarter of its angle; positive counter-clockwise).</summary>
    void Bulge(double x1, double y1, double x2, double y2, double b)
    {
        double dx = x2 - x1, dy = y2 - y1, chord = Math.Sqrt(dx * dx + dy * dy);
        if (chord == 0) return;
        double s = chord / 2 * (1 - b * b) / (2 * b);
        double cx = (x1 + x2) / 2 - s * dy / chord, cy = (y1 + y2) / 2 + s * dx / chord;
        double r = Math.Sqrt((x1 - cx) * (x1 - cx) + (y1 - cy) * (y1 - cy));
        double a1 = Math.Atan2(y1 - cy, x1 - cx) * 180 / Math.PI, a2 = Math.Atan2(y2 - cy, x2 - cx) * 180 / Math.PI;
        double from = b > 0 ? a1 : a2, to = b > 0 ? a2 : a1;
        Arc(cx, cy, r, from, to);
        AddCurve(cx, cy, r, from, Normal(to - from), ends: true);
    }

    /// <summary>An arc counter-clockwise from <paramref name="startDeg"/> to <paramref name="endDeg"/>: its ends and every quadrant point it passes.</summary>
    void Arc(double cx, double cy, double r, double startDeg, double endDeg)
    {
        double sweep = Normal(endDeg - startDeg);
        if (sweep == 0) sweep = 360;
        Add(cx + r * Math.Cos(startDeg * Math.PI / 180), cy + r * Math.Sin(startDeg * Math.PI / 180));
        Add(cx + r * Math.Cos(endDeg * Math.PI / 180), cy + r * Math.Sin(endDeg * Math.PI / 180));
        for (int q = 0; q < 360; q += 90)
        {
            if (Normal(q - startDeg) <= sweep) Add(cx + r * Math.Cos(q * Math.PI / 180), cy + r * Math.Sin(q * Math.PI / 180));
        }
    }

    static double Normal(double degrees)
    {
        double d = degrees % 360;
        return d < 0 ? d + 360 : d;
    }

    void Ellipse(string type, List<(int Code, string Value)> group)
    {
        double cx = Need(type, group, 10), cy = Need(type, group, 20), mx = Need(type, group, 11), my = Need(type, group, 21);
        double ratio = Need(type, group, 40), from = First(type, group, 41) ?? 0, to = First(type, group, 42) ?? 2 * Math.PI;
        if (to <= from) to += 2 * Math.PI;
        const int steps = 720;
        for (int k = 0; k <= steps; k++)
        {
            double t = from + (to - from) * k / steps;
            Add(cx + mx * Math.Cos(t) - ratio * my * Math.Sin(t), cy + my * Math.Cos(t) + ratio * mx * Math.Sin(t));
        }
    }

    static double Need(string type, List<(int Code, string Value)> group, int code) =>
        First(type, group, code) ?? throw new FormatException($"{type} has no group code {code}");

    void Add(double x, double y)
    {
        _minX = Math.Min(_minX, x);
        _minY = Math.Min(_minY, y);
        _maxX = Math.Max(_maxX, x);
        _maxY = Math.Max(_maxY, y);
    }
}
