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

    /// <summary>The assembly's component patterns (linear, circular, pattern driven), in tree order.</summary>
    IReadOnlyList<object> Patterns();

    /// <summary>The component pattern with this name, or null.</summary>
    object? PatternByName(string name);

    /// <summary>A component pattern as SOLIDWORKS holds it: kind, seeds, the instances it made, its error.</summary>
    ComponentPatternFacts ReadPattern(object pattern);

    /// <summary>
    /// Makes a linear component pattern by selection (never SeedComponentArray, a SAFEARRAY-of-dispatch setter); null
    /// when SOLIDWORKS made none. <paramref name="tried"/> says how each way of asking went.
    /// </summary>
    object? LinearPattern(ComponentLinearSpec spec, out string tried);

    /// <summary>Makes a circular component pattern by selection; null when SOLIDWORKS made none.</summary>
    object? CircularPattern(ComponentCircularSpec spec, out string tried);

    /// <summary>Makes a pattern driven component pattern: <paramref name="seeds"/> onto <paramref name="drivingFeature"/> (a component's feature, in the assembly); null when SOLIDWORKS made none.</summary>
    object? FeaturePattern(IReadOnlyList<object> seeds, object drivingFeature, out string tried);

    /// <summary>Deletes component patterns and the instances they made; their seeds stay.</summary>
    void DeletePatterns(IReadOnlyList<object> patterns);

    /// <summary>The faces a component's feature made, read in the component's own document and described in the assembly's coordinates.</summary>
    IReadOnlyList<EntityInfo> FeatureFaces(object component, object feature);

    /// <summary>
    /// Opens SOLIDWORKS' interference detection (IAssemblyDoc.InterferenceDetectionManager) with these options. The
    /// caller must call <see cref="IInterferenceSession.Done"/> in a finally.
    /// </summary>
    IInterferenceSession OpenInterference(InterferenceOptions options);

    /// <summary>The exploded views of a configuration, in order (IAssemblyDoc.GetExplodedViewNames2).</summary>
    IReadOnlyList<string> ExplodedViews(string configuration);

    /// <summary>The view the assembly shows exploded now, or null when it is collapsed (IModelDocExtension.IsExploded).</summary>
    string? ShownExploded();

    /// <summary>Explodes (true) or collapses (false) a view of the active configuration (IAssemblyDoc.ShowExploded2); false when SOLIDWORKS refused.</summary>
    bool ShowExploded(string view, bool exploded);

    /// <summary>A view's steps, read while it is shown exploded (IConfiguration.GetExplodeStep); what was shown before is shown again.</summary>
    IReadOnlyList<ExplodeStepFacts> ExplodeSteps(string view);

    /// <summary>Makes a new, empty exploded view in the active configuration (IAssemblyDoc.CreateExplodedView): its name, or null when SOLIDWORKS made none.</summary>
    string? CreateExplodedView();

    /// <summary>
    /// Adds a step to the view <see cref="CreateExplodedView"/> made, by selection (never IExplodeStep.SetComponents, a
    /// SAFEARRAY-of-dispatch setter): its name, or null when SOLIDWORKS made none. <paramref name="tried"/> says how each
    /// way of asking went.
    /// </summary>
    string? AddExplodeStep(ExplodeStepSpec spec, out string tried);

    /// <summary>
    /// Deletes a view of the active configuration's steps, and the view itself when SOLIDWORKS lets it (2026 SP4 does not:
    /// the view is left, empty). True when it has no steps left; <paramref name="tried"/> says how each way of asking went.
    /// </summary>
    bool DeleteExplodedView(string view, out string tried);

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

/// <summary>A linear component pattern to make: the seeds and directions as resolved in the assembly.</summary>
public sealed class ComponentLinearSpec
{
    public IReadOnlyList<object> Seeds { get; set; } = System.Array.Empty<object>();

    public object Direction { get; set; } = new object();

    public double SpacingMm { get; set; }

    public int Count { get; set; }

    public bool Reverse { get; set; }

    public object? Direction2 { get; set; }

    public double SpacingMm2 { get; set; }

    public int Count2 { get; set; } = 1;

    public bool Reverse2 { get; set; }
}

/// <summary>A circular component pattern to make.</summary>
public sealed class ComponentCircularSpec
{
    public IReadOnlyList<object> Seeds { get; set; } = System.Array.Empty<object>();

    public object Axis { get; set; } = new object();

    public int Count { get; set; }

    /// <summary>With equal spacing the angle shared; otherwise the angle between neighbours.</summary>
    public double AngleDeg { get; set; }

    public bool EqualSpacing { get; set; }

    public bool Reverse { get; set; }
}

/// <summary>A component pattern as SOLIDWORKS holds it.</summary>
public sealed class ComponentPatternFacts
{
    public string Name { get; set; } = "";

    public ComponentPatternKind Kind { get; set; }

    /// <summary>The components it repeats.</summary>
    public List<object> Seeds { get; } = new List<object>();

    /// <summary>Where the seeds were read from (for the log).</summary>
    public string SeedsFrom { get; set; } = "";

    /// <summary>The components it made, the seeds not included.</summary>
    public List<object> Instances { get; } = new List<object>();

    /// <summary>Its swFeatureError_e name, or null.</summary>
    public string? Error { get; set; }
}

/// <summary>How interference detection runs (design 455, Interference).</summary>
public sealed class InterferenceOptions
{
    public bool CoincidentIsInterference { get; set; }

    public bool IncludeHidden { get; set; }
}

/// <summary>An open interference detection session. Done closes it and must always be called.</summary>
public interface IInterferenceSession
{
    /// <summary>Every interference SOLIDWORKS found.</summary>
    IReadOnlyList<InterferenceFacts> Read();

    /// <summary>Puts back what opening it set, then IInterferenceDetectionMgr.Done.</summary>
    void Done();

    /// <summary>What the last Read saw besides the interferences (for the log), or empty.</summary>
    string Note { get; }
}

/// <summary>One interference: the components (SOLIDWORKS' objects), the volume and box in mm.</summary>
public sealed class InterferenceFacts
{
    public List<object> Components { get; } = new List<object>();

    public double VolumeMm3 { get; set; }

    public Box3? Box { get; set; }
}

/// <summary>An explode step to make: the components and direction (and rotation axis) as resolved in the assembly, mm and degrees.</summary>
public sealed class ExplodeStepSpec
{
    public IReadOnlyList<object> Components { get; set; } = System.Array.Empty<object>();

    public object Direction { get; set; } = new object();

    public double DistanceMm { get; set; }

    public bool Reverse { get; set; }

    /// <summary>Null for a step that does not turn.</summary>
    public object? RotationAxis { get; set; }

    public double AngleDeg { get; set; }
}

/// <summary>An explode step as SOLIDWORKS holds it.</summary>
public sealed class ExplodeStepFacts
{
    public string Name { get; set; } = "";

    /// <summary>The components it moves (SOLIDWORKS' objects).</summary>
    public List<object> Components { get; } = new List<object>();

    public double DistanceMm { get; set; }

    public double AngleDeg { get; set; }

    public bool Reversed { get; set; }
}
