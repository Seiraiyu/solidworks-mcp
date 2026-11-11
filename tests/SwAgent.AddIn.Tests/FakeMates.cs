using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Tests;

/// <summary>A mate of a <see cref="FakeAssembly"/>: the entity references name, what ReadMate reports, and its two sides.</summary>
sealed class FakeMate
{
    public FakeMate(FakeEntity entity, MateFacts facts, IReadOnlyList<FakeEntity> sides)
    {
        Entity = entity;
        Facts = facts;
        Sides = sides.ToList();
    }

    public FakeEntity Entity { get; }

    public MateFacts Facts { get; }

    public List<FakeEntity> Sides { get; }
}

sealed partial class FakeAssembly
{
    int _mateCount;

    public List<FakeMate> MateItems { get; } = new List<FakeMate>();

    /// <summary>When set, AddMate reports this swAddMateError_e name.</summary>
    public string? AddMateError { get; set; }

    /// <summary>With <see cref="AddMateError"/>: SOLIDWORKS made the mate anyway (an over-defining mate).</summary>
    public bool AddMateKeepsMate { get; set; }

    /// <summary>Runs after every mate made or edited: the solve (tests move components and their faces here).</summary>
    public Action<FakeAssembly, FakeMate>? OnMate { get; set; }

    /// <summary>When true, EditMate reports SOLIDWORKS refusing.</summary>
    public bool EditMateRefused { get; set; }

    /// <summary>When true, SetMateSuppressed reports SOLIDWORKS refusing.</summary>
    public bool MateSuppressionRefused { get; set; }

    /// <summary>What RemainingDofs answers, by component name; absent for none.</summary>
    public Dictionary<string, RemainingDofs> Dofs { get; } = new Dictionary<string, RemainingDofs>();

    public FakeMate MateOf(object mate) => MateItems.Single(m => ReferenceEquals(m.Entity, mate));

    public IReadOnlyList<object> Mates() => MateItems.Select(m => (object)m.Entity).ToList();

    public object? MateByName(string name) =>
        MateItems.FirstOrDefault(m => string.Equals(m.Facts.Name, name, StringComparison.OrdinalIgnoreCase))?.Entity;

    public MateFacts ReadMate(object mate)
    {
        var m = MateOf(mate);
        var f = m.Facts;
        var copy = new MateFacts
        {
            Name = f.Name, Type = f.Type, SolidWorksType = f.SolidWorksType, Alignment = f.Alignment, Flipped = f.Flipped,
            DistanceMm = f.DistanceMm, AngleDeg = f.AngleDeg, Suppressed = f.Suppressed, Error = f.Error,
        };
        foreach (var side in m.Sides) copy.Entities.Add((side.Kind == ReferenceKind.Component ? side : side.Component?.Entity, side));
        return copy;
    }

    public object? AddMate(MateSpec spec, out string? error)
    {
        Calls.Add($"mate {WireNames.ForEnum(spec.Type)}");
        error = AddMateError;
        if (error != null && !AddMateKeepsMate) return null;
        string name = spec.Name ?? spec.Type.ToString() + ++_mateCount;
        var entity = _doc.Add(ReferenceKind.Mate, new EntityInfo { Name = name, Geometry = GeometryType.Other });
        var facts = new MateFacts
        {
            Name = name, Type = spec.Type, SolidWorksType = "swMate" + spec.Type.ToString().ToUpperInvariant(),
            Alignment = spec.Type == MateType.Lock ? null : spec.Alignment ?? MateAlignment.Aligned,
            Flipped = spec.Flip, DistanceMm = spec.DistanceMm, AngleDeg = spec.AngleDeg,
        };
        var mate = new FakeMate(entity, facts, spec.Entities.Cast<FakeEntity>().ToList());
        MateItems.Add(mate);
        MateNames.Add(name);
        foreach (var c in Joined(mate)) c.Mates.Add(name);
        OnMate?.Invoke(this, mate);
        return entity;
    }

    IEnumerable<FakeComponent> Joined(FakeMate m) =>
        m.Sides.Select(s => s.Kind == ReferenceKind.Component ? Of(s) : s.Component).Where(c => c != null).Select(c => c!).Distinct();

    public bool EditMate(object mate, MateSpec spec)
    {
        var m = MateOf(mate);
        Calls.Add($"edit {m.Facts.Name}");
        if (EditMateRefused) return false;
        m.Facts.Alignment = spec.Alignment ?? m.Facts.Alignment;
        m.Facts.Flipped = spec.Flip;
        m.Facts.DistanceMm = spec.DistanceMm ?? m.Facts.DistanceMm;
        m.Facts.AngleDeg = spec.AngleDeg ?? m.Facts.AngleDeg;
        OnMate?.Invoke(this, m);
        return true;
    }

    public bool SetMateSuppressed(object mate, bool suppressed)
    {
        var m = MateOf(mate);
        Calls.Add($"{(suppressed ? "suppress" : "unsuppress")} {m.Facts.Name}");
        if (MateSuppressionRefused) return false;
        m.Facts.Suppressed = suppressed;
        m.Entity.State = PersistState.Ok;
        return true;
    }

    public void DeleteMates(IReadOnlyList<object> mates)
    {
        foreach (var m in mates.Select(MateOf).ToList())
        {
            Calls.Add($"delete {m.Facts.Name}");
            MateItems.Remove(m);
            MateNames.Remove(m.Facts.Name);
            m.Entity.State = PersistState.Deleted;
            foreach (var c in Items) c.Mates.Remove(m.Facts.Name);
        }
    }

    public RemainingDofs? RemainingDofs(object component) => Dofs.TryGetValue(Of(component).Info.Name, out var d) ? d : null;
}
