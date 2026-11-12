using System.Collections.Generic;
using SwAgent.AddIn.Core;
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

    /// <summary>
    /// A component's share of the assembly's mass in mm and g, in the assembly's coordinates: its solid bodies in the
    /// configuration the assembly references, hidden ones included, as the assembly's own (ISwDocument.Mass) counts them.
    /// Null with <paramref name="refused"/> saying why when it cannot be counted exactly (a lightweight component, a
    /// material SOLIDWORKS gives no density for); null with no reason when SOLIDWORKS gives nothing.
    /// </summary>
    ComponentMass? Mass(object component, out string? refused);

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

    /// <summary>The assembly's own mates (its MateGroup's) in tree order; a subassembly's mates are in its own document.</summary>
    IReadOnlyList<object> Mates();

    /// <summary>The mate with this name, or null.</summary>
    object? MateByName(string name);

    /// <summary>A mate as SOLIDWORKS holds it: type, alignment, value, state, error, and the two things it joins.</summary>
    MateFacts ReadMate(object mate);

    /// <summary>
    /// Makes a standard mate between <see cref="MateSpec.Entities"/> (two entities in the assembly's context, or two
    /// components for lock). Returns the mate, or null when SOLIDWORKS made none; <paramref name="error"/> is
    /// SOLIDWORKS' swAddMateError_e name whenever it reported one, also when it made the mate anyway (over defined).
    /// </summary>
    object? AddMate(MateSpec spec, out string? error);

    /// <summary>Gives a mate the alignment, flip and value of <paramref name="spec"/>, in place; false when SOLIDWORKS refused.</summary>
    bool EditMate(object mate, MateSpec spec);

    /// <summary>Suppresses or unsuppresses a mate in the active configuration; false when SOLIDWORKS refused.</summary>
    bool SetMateSuppressed(object mate, bool suppressed);

    /// <summary>Deletes mates; the components stay where they are.</summary>
    void DeleteMates(IReadOnlyList<object> mates);

    /// <summary>What IComponent2.GetRemainingDOFs gives for a component (mm); null when SOLIDWORKS throws or gives nothing.</summary>
    RemainingDofs? RemainingDofs(object component);

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

/// <summary>A mate to make, or the values to give one.</summary>
public sealed class MateSpec
{
    public MateType Type { get; set; }

    /// <summary>The two sides, as resolved in the assembly; empty for an edit.</summary>
    public IReadOnlyList<object> Entities { get; set; } = System.Array.Empty<object>();

    public MateAlignment? Alignment { get; set; }

    public bool Flip { get; set; }

    public double? DistanceMm { get; set; }

    public double? AngleDeg { get; set; }

    public string? Name { get; set; }
}

/// <summary>A mate as SOLIDWORKS holds it.</summary>
public sealed class MateFacts
{
    public string Name { get; set; } = "";

    /// <summary>Null for an advanced or mechanical mate.</summary>
    public MateType? Type { get; set; }

    /// <summary>Its swMateType_e name.</summary>
    public string SolidWorksType { get; set; } = "";

    public MateAlignment? Alignment { get; set; }

    public bool Flipped { get; set; }

    public double? DistanceMm { get; set; }

    public double? AngleDeg { get; set; }

    public bool Suppressed { get; set; }

    /// <summary>Its swFeatureError_e name, or null.</summary>
    public string? Error { get; set; }

    /// <summary>Each side: its component (null for the assembly's own plane, axis or point) and the entity (for lock, the component).</summary>
    public List<(object? Component, object Entity)> Entities { get; } = new List<(object? Component, object Entity)>();
}
