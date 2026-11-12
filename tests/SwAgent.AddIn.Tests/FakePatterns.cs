using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Tests;

/// <summary>A component pattern of a <see cref="FakeAssembly"/>: the feature entity references name, its facts, and its components.</summary>
sealed class FakePattern
{
    public FakePattern(FakeEntity entity, string name, ComponentPatternKind kind)
    {
        Entity = entity;
        Name = name;
        Kind = kind;
    }

    public FakeEntity Entity { get; }

    public string Name { get; }

    public ComponentPatternKind Kind { get; }

    public List<FakeComponent> Seeds { get; } = new List<FakeComponent>();

    public List<FakeComponent> Instances { get; } = new List<FakeComponent>();

    public string? Error { get; set; }
}

sealed partial class FakeAssembly
{
    int _patterns;

    public List<FakePattern> PatternItems { get; } = new List<FakePattern>();

    /// <summary>When true, the pattern methods make nothing (SOLIDWORKS returning no feature).</summary>
    public bool PatternFails { get; set; }

    /// <summary>When set, where instance i (from 1) of a seed lands instead of where the fake works it out (a pattern that went wrong, or a feature pattern's holes).</summary>
    public Func<FakeComponent, int, Transform>? PlaceInstance { get; set; }

    /// <summary>How many instances of each seed a feature pattern makes (its holes less the seed's).</summary>
    public int FeatureInstances { get; set; } = 3;

    /// <summary>The faces FeatureFaces gives for a feature, by the feature's name (the assembly's counterpart of a part feature is made lazily, so tests cannot hold it).</summary>
    public Dictionary<string, List<EntityInfo>> DrivingFaces { get; } = new Dictionary<string, List<EntityInfo>>(StringComparer.OrdinalIgnoreCase);

    public FakePattern PatternOf(object pattern) => PatternItems.Single(p => ReferenceEquals(p.Entity, pattern));

    public IReadOnlyList<object> Patterns() => PatternItems.Select(p => (object)p.Entity).ToList();

    public object? PatternByName(string name) =>
        PatternItems.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Entity;

    public ComponentPatternFacts ReadPattern(object pattern)
    {
        var p = PatternOf(pattern);
        var facts = new ComponentPatternFacts { Name = p.Name, Kind = p.Kind, Error = p.Error };
        facts.Seeds.AddRange(p.Seeds.Select(s => (object)s.Entity));
        facts.Instances.AddRange(p.Instances.Select(i => (object)i.Entity));
        return facts;
    }

    public object? LinearPattern(ComponentLinearSpec spec, out string tried)
    {
        Calls.Add("linear pattern");
        var d1 = Way((FakeEntity)spec.Direction, spec.Reverse);
        var d2 = spec.Direction2 == null ? null : Way((FakeEntity)spec.Direction2, spec.Reverse2);
        return Make("LocalLPattern", ComponentPatternKind.Linear, spec.Seeds, out tried, (seed, n) =>
        {
            var list = new List<Transform>();
            for (int i = 0; i < spec.Count; i++)
            {
                for (int j = 0; j < (d2 == null ? 1 : spec.Count2); j++)
                {
                    if (i == 0 && j == 0) continue;
                    var o = seed.Info.Transform.Origin;
                    double dx = d1.X * spec.SpacingMm * i + (d2 == null ? 0 : d2.X * spec.SpacingMm2 * j);
                    double dy = d1.Y * spec.SpacingMm * i + (d2 == null ? 0 : d2.Y * spec.SpacingMm2 * j);
                    double dz = d1.Z * spec.SpacingMm * i + (d2 == null ? 0 : d2.Z * spec.SpacingMm2 * j);
                    list.Add(new Transform { Origin = new Point3(o.X + dx, o.Y + dy, o.Z + dz), XAxis = seed.Info.Transform.XAxis, YAxis = seed.Info.Transform.YAxis, ZAxis = seed.Info.Transform.ZAxis });
                }
            }
            return list;
        });
    }

    public object? CircularPattern(ComponentCircularSpec spec, out string tried)
    {
        Calls.Add("circular pattern");
        var axis = (FakeEntity)spec.Axis;
        double step = spec.EqualSpacing ? (Math.Abs(spec.AngleDeg - 360) < 1e-9 ? 360.0 / spec.Count : spec.AngleDeg / (spec.Count - 1)) : spec.AngleDeg;
        if (spec.Reverse) step = -step;
        return Make("LocalCirPattern", ComponentPatternKind.Circular, spec.Seeds, out tried, (seed, n) =>
            Enumerable.Range(1, spec.Count - 1).Select(i => PatternGeometry.Turn(seed.Info.Transform, axis.Info.Origin!, axis.Info.Axis!, step * i)).ToList());
    }

    public object? FeaturePattern(IReadOnlyList<object> seeds, object drivingFeature, out string tried)
    {
        Calls.Add($"feature pattern {((FakeEntity)drivingFeature).Info.Name}");
        return Make("DerivedLPattern", ComponentPatternKind.Feature, seeds, out tried, (seed, n) =>
            Enumerable.Range(1, FeatureInstances).Select(_ => seed.Info.Transform).ToList());
    }

    static Vector3 Way(FakeEntity e, bool reverse)
    {
        var v = PatternGeometry.Unit(e.Info.Axis ?? e.Info.Normal!);
        return reverse ? new Vector3(-v.X, -v.Y, -v.Z) : v;
    }

    /// <summary>Makes the pattern feature and, for each seed, an instance at each place (or where PlaceInstance says).</summary>
    object? Make(string type, ComponentPatternKind kind, IReadOnlyList<object> seeds, out string tried, Func<FakeComponent, int, List<Transform>> places)
    {
        if (PatternFails)
        {
            tried = "fake: returned null";
            return null;
        }
        string name = type + ++_patterns;
        var entity = _doc.AddFeature(name, type);
        var pattern = new FakePattern(entity, name, kind);
        foreach (var seed in seeds.Select(Of))
        {
            pattern.Seeds.Add(seed);
            var at = places(seed, 0);
            for (int i = 0; i < at.Count; i++)
            {
                string stem = System.IO.Path.GetFileNameWithoutExtension(seed.Info.Path) ?? "c";
                int k = 1;
                while (Items.Any(c => c.Info.Name == $"{stem}-{k}")) k++;
                var instance = AddComponent($"{stem}-{k}", seed.Info.Path!);
                instance.Info.Transform = JsonCodec.Deserialize<Transform>(JsonCodec.Serialize(PlaceInstance?.Invoke(seed, i + 1) ?? at[i]));
                pattern.Instances.Add(instance);
            }
        }
        PatternItems.Add(pattern);
        tried = $"fake: made {name}";
        OnSolve?.Invoke(this);
        return entity;
    }

    public void DeletePatterns(IReadOnlyList<object> patterns)
    {
        foreach (var p in patterns.Select(PatternOf).ToList())
        {
            Calls.Add($"delete {p.Name}");
            foreach (var i in p.Instances) Remove(i);
            PatternItems.Remove(p);
            _doc.RemoveFeature(p.Entity);
            p.Entity.State = PersistState.Deleted;
        }
    }

    public IReadOnlyList<EntityInfo> FeatureFaces(object component, object feature) =>
        DrivingFaces.TryGetValue(((FakeEntity)feature).Info.Name ?? "", out var faces) ? faces.Select(f => JsonCodec.Deserialize<EntityInfo>(JsonCodec.Serialize(f))).ToList() : new List<EntityInfo>();
}
