using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Tests;

/// <summary>A component of a <see cref="FakeAssembly"/>: the entity references name, and what Read reports.</summary>
sealed class FakeComponent
{
    public FakeComponent(FakeEntity entity, ComponentInfo info)
    {
        Entity = entity;
        Info = info;
    }

    public FakeEntity Entity { get; }

    public ComponentInfo Info { get; }

    /// <summary>The configurations its file has; SOLIDWORKS keeps the old one when asked for any other.</summary>
    public List<string> Configurations { get; } = new List<string> { "Default" };

    /// <summary>The names of the mates that use it.</summary>
    public List<string> Mates { get; } = new List<string>();
}

sealed partial class FakeDocument
{
    /// <summary>What Assembly returns: set for an assembly made with <see cref="NewAssembly"/>.</summary>
    public FakeAssembly? Asm { get; set; }

    public ISwAssembly? Assembly() => Asm;

    /// <summary>An assembly with the template's planes and origin, and no components.</summary>
    public static FakeDocument NewAssembly(string title, string? path = null)
    {
        var doc = Part(title, path);
        doc.Document.Type = DocumentType.Assembly;
        doc.Asm = new FakeAssembly(doc);
        return doc;
    }
}

/// <summary>An assembly for operation tests: components, mates by name, and the user's in-context edit.</summary>
sealed class FakeAssembly : ISwAssembly
{
    readonly FakeDocument _doc;
    int _instances;

    public FakeAssembly(FakeDocument doc)
    {
        _doc = doc;
    }

    public List<FakeComponent> Items { get; } = new List<FakeComponent>();

    /// <summary>Every call that changed something, in order, for tests that check what was asked and in what order.</summary>
    public List<string> Calls { get; } = new List<string>();

    /// <summary>Adds a component as if it were already in the assembly.</summary>
    public FakeComponent AddComponent(string name, string path, Point3? at = null, bool fix = false, string? parent = null)
    {
        var entity = _doc.Add(ReferenceKind.Component, new EntityInfo { Name = name, Geometry = GeometryType.Other });
        var info = new ComponentInfo
        {
            Name = name,
            Path = path,
            Configuration = "Default",
            State = ComponentState.Resolved,
            Fixed = fix,
            Transform = new Transform { Origin = at ?? new Point3() },
            Subassembly = path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase),
            Constraint = fix ? Constraint.Fixed : Constraint.UnderDefined,
            Parent = parent,
        };
        if (info.Subassembly) info.SolveAs = SolveAs.Rigid;
        var component = new FakeComponent(entity, info);
        Items.Add(component);
        return component;
    }

    public FakeComponent Of(object component) => Items.Single(c => ReferenceEquals(c.Entity, component));

    public FakeComponent Named(string name) => Items.Single(c => c.Info.Name == name);

    public IReadOnlyList<object> Components(bool topLevelOnly) =>
        Items.Where(c => !topLevelOnly || c.Info.Parent == null).Select(c => (object)c.Entity).ToList();

    public object? ComponentByName(string name) =>
        Items.FirstOrDefault(c => string.Equals(c.Info.Name, name, StringComparison.OrdinalIgnoreCase))?.Entity;

    public ComponentInfo Read(object component)
    {
        var c = Of(component);
        var copy = JsonCodec.Deserialize<ComponentInfo>(JsonCodec.Serialize(c.Info));
        copy.Mates = c.Mates.Count;
        copy.Reference = null;
        return copy;
    }

    /// <summary>The title of the document the user is editing in context, or null.</summary>
    public string? EditingTitle { get; set; }

    public string? EditingComponent() => EditingTitle;

    /// <summary>The assembly's mates by name.</summary>
    public List<string> MateNames { get; } = new List<string>();

    public int MateCount() => MateNames.Count;

    public IReadOnlyList<string> MatesOf(object component) => Of(component).Mates.ToList();

    /// <summary>When true, Insert makes nothing (SOLIDWORKS returning no component).</summary>
    public bool InsertFails { get; set; }

    /// <summary>When set, an inserted component lands here instead of where it was asked to (a placement that did not take).</summary>
    public Transform? InsertLandsAt { get; set; }

    /// <summary>Configurations the files inserted have.</summary>
    public List<string> FileConfigurations { get; } = new List<string> { "Default" };

    public object? Insert(string path, string? configuration, Transform at)
    {
        Calls.Add($"insert {System.IO.Path.GetFileName(path)}");
        if (InsertFails) return null;
        string name = System.IO.Path.GetFileNameWithoutExtension(path) + "-" + ++_instances;
        var c = AddComponent(name, path);
        c.Configurations.Clear();
        c.Configurations.AddRange(FileConfigurations);
        c.Info.Configuration = configuration != null && FileConfigurations.Contains(configuration) ? configuration : FileConfigurations[0];
        c.Info.Transform = JsonCodec.Deserialize<Transform>(JsonCodec.Serialize(InsertLandsAt ?? at));
        return c.Entity;
    }

    /// <summary>Runs after every placement and rebuild: what a solve does to the components (tests move them here).</summary>
    public Action<FakeAssembly>? OnSolve { get; set; }

    /// <summary>When true, a placement is ignored (a component its mates hold where it is).</summary>
    public bool PlacementIgnored { get; set; }

    public void SetTransform(object component, Transform transform)
    {
        var c = Of(component);
        Calls.Add($"place {c.Info.Name}");
        if (!PlacementIgnored) c.Info.Transform = JsonCodec.Deserialize<Transform>(JsonCodec.Serialize(transform));
        OnSolve?.Invoke(this);
    }

    public void SetFixed(object component, bool fix)
    {
        var c = Of(component);
        Calls.Add($"{(fix ? "fix" : "float")} {c.Info.Name}");
        c.Info.Fixed = fix;
        c.Info.Constraint = fix ? Constraint.Fixed : Constraint.UnderDefined;
    }

    /// <summary>When true, SetSuppression reports SOLIDWORKS refusing.</summary>
    public bool SuppressionRefused { get; set; }

    public bool SetSuppression(object component, Suppression suppression)
    {
        var c = Of(component);
        Calls.Add($"{WireNames.ForEnum(suppression)} {c.Info.Name}");
        if (SuppressionRefused) return false;
        c.Info.State = suppression == Suppression.Suppressed ? ComponentState.Suppressed : ComponentState.Resolved;
        c.Entity.State = suppression == Suppression.Suppressed ? PersistState.Suppressed : PersistState.Ok;
        return true;
    }

    public void SetConfiguration(object component, string configuration)
    {
        var c = Of(component);
        Calls.Add($"configuration {c.Info.Name} {configuration}");
        if (c.Configurations.Contains(configuration)) c.Info.Configuration = configuration;
    }

    public void SetSolveAs(object component, SolveAs solveAs)
    {
        var c = Of(component);
        Calls.Add($"{WireNames.ForEnum(solveAs)} {c.Info.Name}");
        c.Info.SolveAs = solveAs;
    }

    public void SetHidden(object component, bool hidden)
    {
        var c = Of(component);
        Calls.Add($"{(hidden ? "hide" : "show")} {c.Info.Name}");
        c.Info.Hidden = hidden;
    }

    /// <summary>When true, Delete removes nothing (SOLIDWORKS refusing without saying so).</summary>
    public bool DeleteIgnored { get; set; }

    public void Delete(IReadOnlyList<object> components)
    {
        var doomed = components.Select(Of).ToList();
        Calls.Add($"delete {string.Join(", ", doomed.Select(c => c.Info.Name))}");
        if (DeleteIgnored) return;
        foreach (var c in doomed)
        {
            foreach (var child in Items.Where(i => i.Info.Parent == c.Info.Name).ToList()) Remove(child);
            Remove(c);
        }
    }

    void Remove(FakeComponent c)
    {
        Items.Remove(c);
        c.Entity.State = PersistState.Deleted;
        foreach (var mate in c.Mates)
        {
            MateNames.Remove(mate);
            foreach (var other in Items) other.Mates.Remove(mate);
        }
    }

    /// <summary>When true, EditInContext reports SOLIDWORKS refusing.</summary>
    public bool EditRefused { get; set; }

    public bool EditInContext(object component)
    {
        if (EditRefused) return false;
        EditingTitle = System.IO.Path.GetFileName(Of(component).Info.Path);
        return true;
    }

    public void EditAssembly() => EditingTitle = null;

    public int Rebuilds { get; private set; }

    public void Rebuild()
    {
        Rebuilds++;
        OnSolve?.Invoke(this);
    }
}
