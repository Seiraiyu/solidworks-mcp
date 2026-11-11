using System.Collections.Generic;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// An open assembly, as the assembly operations read and change it, so they can be tested without SOLIDWORKS (design
/// 455). UI thread only. Components are SOLIDWORKS' own objects, passed around as object and only ever handed back to the
/// same assembly. Lengths are millimetres, as at every public boundary.
/// </summary>
public interface ISwAssembly
{
    /// <summary>Its components in tree order: the top level only, or every level with each subassembly's children after it.</summary>
    IReadOnlyList<object> Components(bool topLevelOnly);

    /// <summary>The component with this name (sub-1/pin-1 inside a subassembly), or null.</summary>
    object? ComponentByName(string name);

    /// <summary>Everything about a component but its reference, which the reference service adds.</summary>
    ComponentInfo Read(object component);

    /// <summary>A component's name, file and state, read cheaply (no box, mates or solve): what references and lookups need.</summary>
    ComponentIdentity Identify(object component);

    /// <summary>The document a resolved component is an instance of (IComponent2.GetModelDoc2); null when it is suppressed, lightweight or missing.</summary>
    ISwDocument? ModelOf(object component);

    /// <summary>
    /// The entity <paramref name="entity"/> (a face, edge, vertex or feature of <see cref="ModelOf"/>'s document) is in
    /// the assembly, through this component (IComponent2.GetCorresponding); null when SOLIDWORKS gives none.
    /// </summary>
    object? Corresponding(object component, object entity);

    /// <summary>
    /// An entity of a component, described in the assembly's coordinates and naming the component (design 455, Assembly
    /// references): a feature (plane, axis, point) as the component's own document describes it, a face, edge or vertex
    /// as SOLIDWORKS gives it, each then moved by the component's placement. No reference: the reference service adds it.
    /// </summary>
    EntityInfo Describe(object component, object entity);

    /// <summary>Every face, edge or vertex of a resolved part component's solid bodies, in the assembly's context; empty for a subassembly or a component that is not resolved.</summary>
    IReadOnlyList<object> Topology(object component, TopologyKind kind);

    /// <summary>The shortest distance from <paramref name="point"/> (mm, assembly coordinates) to a face, edge or vertex of the component, in mm.</summary>
    double DistanceMm(object component, object entity, Point3 point);

    /// <summary>The title of the document the user is editing in the assembly's context, or null when they edit the assembly itself.</summary>
    string? EditingComponent();

    /// <summary>How many mates the assembly has.</summary>
    int MateCount();

    /// <summary>The names of the mates that use this component.</summary>
    IReadOnlyList<string> MatesOf(object component);

    /// <summary>
    /// Inserts <paramref name="path"/> as a component using <paramref name="configuration"/> (its active one when null),
    /// placed at <paramref name="at"/>; null when SOLIDWORKS made none. Loads the file, hidden, if it is not open.
    /// </summary>
    object? Insert(string path, string? configuration, Transform at);

    /// <summary>Moves a component to <paramref name="transform"/>.</summary>
    void SetTransform(object component, Transform transform);

    /// <summary>Fixes or floats a component.</summary>
    void SetFixed(object component, bool fix);

    /// <summary>Suppresses or resolves a component in the active configuration; false when SOLIDWORKS refused.</summary>
    bool SetSuppression(object component, Suppression suppression);

    /// <summary>Makes a component use another configuration of its file.</summary>
    void SetConfiguration(object component, string configuration);

    /// <summary>Makes a subassembly solve rigid or flexible.</summary>
    void SetSolveAs(object component, SolveAs solveAs);

    /// <summary>Hides or shows a component.</summary>
    void SetHidden(object component, bool hidden);

    /// <summary>Deletes components; SOLIDWORKS deletes the mates that use them with them.</summary>
    void Delete(IReadOnlyList<object> components);

    /// <summary>Rebuilds the assembly, solving its mates.</summary>
    void Rebuild();

    /// <summary>Fault testing only (diag.edit_component): starts editing a component in context as the user would; false when SOLIDWORKS refused.</summary>
    bool EditInContext(object component);

    /// <summary>Fault testing only: returns to editing the assembly itself.</summary>
    void EditAssembly();
}

/// <summary>A component's name (sub-1/pin-1 inside a subassembly), the full path of its file (null when it has none), and whether it is loaded.</summary>
public sealed class ComponentIdentity
{
    public ComponentIdentity(string name, string? path, ComponentState state)
    {
        Name = name;
        Path = path;
        State = state;
    }

    public string Name { get; }

    public string? Path { get; }

    public ComponentState State { get; }
}
