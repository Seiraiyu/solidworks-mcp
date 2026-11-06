using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>What SOLIDWORKS said when a persistent id was looked up (swPersistReferencedObjectStates_e).</summary>
public enum PersistState
{
    Ok,
    Invalid,
    Suppressed,
    Deleted,
}

/// <summary>
/// One open document, as the operations read and change it, so they can be tested without SOLIDWORKS. UI thread only.
/// Entities (faces, edges, features …) are SOLIDWORKS' own objects, passed around as object and only ever handed back
/// to the same document. Lengths are millimetres and angles degrees, as at every public boundary.
/// </summary>
public interface ISwDocument
{
    /// <summary>Title, path, type and dirty flag, read now.</summary>
    DocumentInfo Info();

    /// <summary>SOLIDWORKS' persistent id for <paramref name="entity"/>, or null when it cannot give one.</summary>
    byte[]? PersistId(object entity);

    /// <summary>The entity a persistent id names now, and what SOLIDWORKS said about it.</summary>
    object? Resolve(byte[] persistId, out PersistState state);

    /// <summary>What kind of entity this is, or null when it is none the add-in names.</summary>
    ReferenceKind? KindOf(object entity);

    /// <summary>Its kind, name or owning feature, shape and measurements; no reference (the reference service adds it).</summary>
    EntityInfo Describe(object entity);

    /// <summary>The feature (including planes, axes, points and sketches) with this exact name, or null.</summary>
    object? FeatureByName(string name);

    /// <summary>The first, second or third reference plane in the feature tree (front, top, right in every template), or null.</summary>
    object? StandardPlane(int index);

    /// <summary>The origin feature, or null.</summary>
    object? Origin();

    /// <summary>The name of the sketch the user has open for editing in this document, or null when none is (IModelDoc2.GetActiveSketch2).</summary>
    string? EditingSketch();

    /// <summary>True when the document is open read-only.</summary>
    bool ReadOnly { get; }

    /// <summary>Makes it the active document, without rebuilding it.</summary>
    void Activate();

    /// <summary>Saves it where it is, silently. SOLIDWORKS' swFileSaveError_e and swFileSaveWarning_e flags.</summary>
    SaveOutcome Save();

    /// <summary>Saves it to <paramref name="path"/> in the current version, silently; it is then that file.</summary>
    SaveOutcome SaveAs(string path);

    /// <summary>Rebuilds what changed, or every feature when <paramref name="force"/>; false when SOLIDWORKS reports it failed.</summary>
    bool Rebuild(bool force);

    /// <summary>The features with a rebuild error or warning (SOLIDWORKS' What's Wrong list), in tree order.</summary>
    System.Collections.Generic.IReadOnlyList<RebuildItem> WhatsWrong();

    /// <summary>Every feature in tree order from the first reference plane, each followed by its sub-features (see <see cref="FeatureTree"/>).</summary>
    System.Collections.Generic.IReadOnlyList<FeatureRecord> Features();

    /// <summary>How many solid bodies the part has.</summary>
    int SolidBodyCount();

    /// <summary>Mass properties in mm and g (Document, Bodies and Material left for the caller), or null when SOLIDWORKS gives none.</summary>
    ModelMassResult? Mass();

    /// <summary>The material's name, or null when none is set.</summary>
    string? Material();

    /// <summary>The box around the solid bodies, in mm: SOLIDWORKS' quick one, or the exact one from extreme points.</summary>
    Box3? BoundingBox(bool precise);

    /// <summary>Every face, edge or vertex of the part's solid bodies.</summary>
    System.Collections.Generic.IReadOnlyList<object> Topology(TopologyKind kind);

    /// <summary>The shortest distance from <paramref name="point"/> to a face, edge or vertex, in mm.</summary>
    double DistanceMm(object entity, Point3 point);

    /// <summary>How many items are selected now.</summary>
    int SelectionCount();

    /// <summary>The first <paramref name="max"/> selected items, in order: SOLIDWORKS' select type (swSelectType_e) and the object, when it gives one.</summary>
    System.Collections.Generic.IReadOnlyList<SelectedObject> Selection(int max);

    /// <summary>Clears the selection.</summary>
    void ClearSelection();

    /// <summary>Selects <paramref name="entity"/>, adding to the selection when <paramref name="append"/>; false when SOLIDWORKS refuses.</summary>
    bool Select(object entity, bool append);

    /// <summary>Opens a new sketch on a plane or planar face for drawing (an operation's own; <see cref="ISketchEditor.Close"/> ends it).</summary>
    ISketchEditor NewSketch(object planeOrFace);

    /// <summary>Opens an existing sketch for drawing (an operation's own; <see cref="ISketchEditor.Close"/> ends it).</summary>
    ISketchEditor EditSketch(object sketch);

    /// <summary>Everything in a sketch: its segments, points, relations and dimensions, solve state, and where its plane is.</summary>
    SketchContents ReadSketch(object sketch);

    /// <summary>Every dimension of <paramref name="feature"/> (a feature or sketch), or of every feature in tree order when null.</summary>
    System.Collections.Generic.IReadOnlyList<DimensionRecord> Dimensions(object? feature);

    /// <summary>Sets a dimension (a <see cref="DimensionRecord.Dimension"/>) in the active configuration: mm, or degrees for an angle. Does not rebuild.</summary>
    void SetDimension(object dimension, double value);

    /// <summary>Deletes a feature (a sketch an operation made), and what it absorbed; throws when SOLIDWORKS does not.</summary>
    void DeleteFeature(object feature);

    /// <summary>
    /// Deletes a feature, with its children (the features built on it) only when <paramref name="children"/>, and the
    /// features it absorbed (its sketch) only when <paramref name="absorbed"/>; throws when SOLIDWORKS does not.
    /// </summary>
    void Delete(object feature, bool children, bool absorbed);

    /// <summary>Extrudes <paramref name="sketch"/>; the new feature, or null when SOLIDWORKS made none. Selects what it needs and clears the selection.</summary>
    object? Extrude(object sketch, ExtrudeSpec spec);

    /// <summary>The faces <paramref name="feature"/> made.</summary>
    System.Collections.Generic.IReadOnlyList<object> FacesOf(object feature);
}

/// <summary>One selected object as SOLIDWORKS reports it.</summary>
public sealed class SelectedObject
{
    public SelectedObject(int type, object? entity)
    {
        Type = type;
        Entity = entity;
    }

    /// <summary>swSelectType_e.</summary>
    public int Type { get; }

    public object? Entity { get; }
}

/// <summary>A feature as the tree lists it.</summary>
public sealed class FeatureRecord
{
    public FeatureRecord(object feature, string name, string type, string? parent, bool suppressed, int errorCode, bool warning)
    {
        Feature = feature;
        Name = name;
        Type = type;
        Parent = parent;
        Suppressed = suppressed;
        ErrorCode = errorCode;
        Warning = warning;
    }

    /// <summary>SOLIDWORKS' feature object, for references.</summary>
    public object Feature { get; }
    public string Name { get; }
    public string Type { get; }
    public string? Parent { get; }
    public bool Suppressed { get; }

    /// <summary>IFeature.GetErrorCode2: 0 when it rebuilt cleanly.</summary>
    public int ErrorCode { get; }
    public bool Warning { get; }
}

/// <summary>What a save reported: whether SOLIDWORKS said it succeeded, and its error and warning flags.</summary>
public sealed class SaveOutcome
{
    public SaveOutcome(bool saved, int errors, int warnings)
    {
        Saved = saved;
        Errors = errors;
        Warnings = warnings;
    }

    public bool Saved { get; }
    public int Errors { get; }
    public int Warnings { get; }
}

/// <summary>What opening a file reported: the document (null when it did not open), and SOLIDWORKS' error and warning flags.</summary>
public sealed class OpenOutcome
{
    public OpenOutcome(ISwDocument? document, int errors, int warnings)
    {
        Document = document;
        Errors = errors;
        Warnings = warnings;
    }

    public ISwDocument? Document { get; }
    public int Errors { get; }
    public int Warnings { get; }
}
