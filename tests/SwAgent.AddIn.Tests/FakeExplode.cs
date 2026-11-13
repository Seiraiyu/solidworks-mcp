using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Tests;

/// <summary>An exploded view of a <see cref="FakeAssembly"/>.</summary>
sealed class FakeExplodeView
{
    public FakeExplodeView(string name, string configuration)
    {
        Name = name;
        Configuration = configuration;
    }

    public string Name { get; set; }

    public string Configuration { get; }

    public List<FakeExplodeStep> Steps { get; } = new List<FakeExplodeStep>();
}

/// <summary>A step: its components moved by Move (mm), then turned AngleDeg about Axis.</summary>
sealed class FakeExplodeStep
{
    public string Name { get; set; } = "";

    public List<FakeComponent> Components { get; } = new List<FakeComponent>();

    public Vector3 Move { get; set; } = new Vector3(0, 0, 0);

    public (Point3 Through, Vector3 Axis)? Axis { get; set; }

    public double AngleDeg { get; set; }

    public double DistanceMm { get; set; }

    public bool Reversed { get; set; }
}

/// <summary>Exploded views as SOLIDWORKS keeps them: steps that move the components while the view is shown exploded.</summary>
sealed partial class FakeAssembly
{
    int _views, _steps;
    string? _making;
    Dictionary<FakeComponent, Transform>? _collapsed;

    public List<FakeExplodeView> Views { get; } = new List<FakeExplodeView>();

    /// <summary>The view shown exploded, or null.</summary>
    public string? Shown { get; private set; }

    /// <summary>CreateExplodedView makes nothing.</summary>
    public bool ExplodeFails { get; set; }

    /// <summary>AddExplodeStep makes nothing.</summary>
    public bool StepFails { get; set; }

    /// <summary>SOLIDWORKS goes the other way along a step's direction.</summary>
    public bool StepGoesAgainst { get; set; }

    /// <summary>SOLIDWORKS turns the other way about a step's axis.</summary>
    public bool StepTurnsAgainst { get; set; }

    /// <summary>An extra move for a component in step i (from 0): a step gone wrong.</summary>
    public Func<FakeComponent, int, Vector3?>? StepStrays { get; set; }

    /// <summary>Collapsing leaves the components where they were exploded.</summary>
    public bool CollapseStays { get; set; }

    public bool RenameRefused { get; set; }

    public bool DeleteRefused { get; set; }

    FakeExplodeView? ViewNamed(string name) =>
        Views.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase) && v.Configuration == _doc.Active);

    public IReadOnlyList<string> ExplodedViews(string configuration) => Views.Where(v => v.Configuration == configuration).Select(v => v.Name).ToList();

    public string? ShownExploded() => Shown;

    public bool ShowExploded(string view, bool exploded)
    {
        var v = ViewNamed(view);
        if (v == null) return false;
        Calls.Add(exploded ? $"explode {v.Name}" : $"collapse {v.Name}");
        Collapse();
        if (!exploded) return true;
        _collapsed = Items.ToDictionary(c => c, c => JsonCodec.Deserialize<Transform>(JsonCodec.Serialize(c.Info.Transform)));
        for (int i = 0; i < v.Steps.Count; i++) Apply(v.Steps[i], i);
        Shown = v.Name;
        return true;
    }

    void Collapse()
    {
        if (_collapsed != null && !CollapseStays)
        {
            foreach (var p in _collapsed) p.Key.Info.Transform = p.Value;
        }
        _collapsed = null;
        Shown = null;
    }

    void Apply(FakeExplodeStep s, int index)
    {
        foreach (var c in s.Components)
        {
            var t = PatternGeometry.Shift(c.Info.Transform, s.Move);
            if (s.Axis is (Point3 through, Vector3 axis)) t = PatternGeometry.Turn(t, through, axis, s.AngleDeg);
            if (StepStrays?.Invoke(c, index) is Vector3 extra) t = PatternGeometry.Shift(t, extra);
            c.Info.Transform = t;
        }
    }

    public IReadOnlyList<ExplodeStepFacts> ExplodeSteps(string view) =>
        (ViewNamed(view) ?? throw new InvalidOperationException($"no view {view}")).Steps.Select(s =>
        {
            var facts = new ExplodeStepFacts { Name = s.Name, DistanceMm = s.DistanceMm, AngleDeg = Math.Abs(s.AngleDeg), Reversed = s.Reversed };
            facts.Components.AddRange(s.Components.Select(c => (object)c.Entity));
            return facts;
        }).ToList();

    public string? CreateExplodedView()
    {
        Calls.Add("explode view");
        if (ExplodeFails) return null;
        var v = new FakeExplodeView("ExplView" + ++_views, _doc.Active);
        Views.Add(v);
        _making = v.Name;
        return v.Name;
    }

    public string? AddExplodeStep(ExplodeStepSpec spec, out string tried)
    {
        Calls.Add("explode step");
        var view = _making == null ? null : ViewNamed(_making);
        if (StepFails || view == null)
        {
            tried = "fake: returned null";
            return null;
        }
        var d = (FakeEntity)spec.Direction;
        var dir = PatternGeometry.Unit(d.Info.Axis ?? d.Info.Normal!);
        double sign = spec.Reverse ^ StepGoesAgainst ? -1 : 1;
        var step = new FakeExplodeStep
        {
            Name = "Explode Step" + ++_steps,
            Move = new Vector3(dir.X * spec.DistanceMm * sign, dir.Y * spec.DistanceMm * sign, dir.Z * spec.DistanceMm * sign),
            DistanceMm = spec.DistanceMm,
            Reversed = spec.Reverse,
        };
        if (spec.RotationAxis is FakeEntity a)
        {
            step.Axis = (a.Info.Origin!, a.Info.Axis!);
            step.AngleDeg = spec.AngleDeg * (StepTurnsAgainst ? -1 : 1);
        }
        step.Components.AddRange(spec.Components.Select(Of));
        view.Steps.Add(step);
        tried = $"fake: made {step.Name}";
        return step.Name;
    }

    public bool RenameExplodedView(string view, string name, out string tried)
    {
        tried = RenameRefused ? "fake: refused" : "fake: renamed";
        Calls.Add($"rename view {view}");
        var v = ViewNamed(view);
        if (RenameRefused || v == null) return false;
        if (Shown == v.Name) Shown = name;
        if (_making == v.Name) _making = name;
        v.Name = name;
        return true;
    }

    public bool DeleteExplodedView(string view, out string tried)
    {
        tried = DeleteRefused ? "fake: refused" : "fake: deleted";
        Calls.Add($"delete view {view}");
        var v = ViewNamed(view);
        if (DeleteRefused || v == null) return false;
        if (Shown == v.Name) Collapse();
        if (_making == v.Name) _making = null;
        Views.Remove(v);
        return true;
    }
}
