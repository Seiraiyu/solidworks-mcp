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

    /// <summary>True when the document is open read-only.</summary>
    bool ReadOnly { get; }

    /// <summary>Makes it the active document, without rebuilding it.</summary>
    void Activate();

    /// <summary>Saves it where it is, silently. SOLIDWORKS' swFileSaveError_e and swFileSaveWarning_e flags.</summary>
    SaveOutcome Save();

    /// <summary>Saves it to <paramref name="path"/> in the current version, silently; it is then that file.</summary>
    SaveOutcome SaveAs(string path);
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
