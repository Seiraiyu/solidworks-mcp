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
