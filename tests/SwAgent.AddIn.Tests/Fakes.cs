using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Tests;

/// <summary>An entity of a <see cref="FakeDocument"/>: what it is, its persistent id, and what SOLIDWORKS says when that id is looked up.</summary>
sealed class FakeEntity
{
    static int _next;

    public FakeEntity(ReferenceKind kind, EntityInfo info)
    {
        Kind = kind;
        info.Kind = kind;
        Info = info;
        Id = BitConverter.GetBytes(System.Threading.Interlocked.Increment(ref _next));
    }

    public ReferenceKind Kind { get; set; }
    public EntityInfo Info { get; }
    public byte[]? Id { get; set; }

    /// <summary>What looking up the id reports; anything but Ok returns no entity.</summary>
    public PersistState State { get; set; } = PersistState.Ok;

    /// <summary>When set, the id now finds this entity instead (SOLIDWORKS resolving to something else).</summary>
    public FakeEntity? ResolvesTo { get; set; }
}

/// <summary>A document for operation tests: a title, path and type, and entities the reference code can name.</summary>
sealed class FakeDocument : ISwDocument
{
    public FakeDocument(string title, string? path = null, DocumentType type = DocumentType.Part)
    {
        Document = new DocumentInfo { Title = title, Path = path, Type = type };
    }

    public DocumentInfo Document { get; }

    public List<FakeEntity> Entities { get; } = new List<FakeEntity>();

    /// <summary>A part with the template's planes (named in German, to prove names are not relied on) and its origin.</summary>
    public static FakeDocument Part(string title, string? path = null)
    {
        var doc = new FakeDocument(title, path);
        doc.Add(ReferenceKind.Plane, new EntityInfo { Name = "Ebene vorne", Geometry = GeometryType.Plane, Normal = new Vector3(0, 0, 1) });
        doc.Add(ReferenceKind.Plane, new EntityInfo { Name = "Ebene oben", Geometry = GeometryType.Plane, Normal = new Vector3(0, 1, 0) });
        doc.Add(ReferenceKind.Plane, new EntityInfo { Name = "Ebene rechts", Geometry = GeometryType.Plane, Normal = new Vector3(1, 0, 0) });
        doc.Add(ReferenceKind.Point, new EntityInfo { Name = "Ursprung", Geometry = GeometryType.Point, Origin = new Point3(0, 0, 0) });
        return doc;
    }

    public FakeEntity Add(ReferenceKind kind, EntityInfo info)
    {
        var e = new FakeEntity(kind, info);
        Entities.Add(e);
        return e;
    }

    public DocumentInfo Info() => new DocumentInfo { Title = Document.Title, Path = Document.Path, Type = Document.Type, Dirty = Document.Dirty };

    public byte[]? PersistId(object entity) => ((FakeEntity)entity).Id;

    public object? Resolve(byte[] persistId, out PersistState state)
    {
        var e = Entities.FirstOrDefault(x => x.Id != null && x.Id.SequenceEqual(persistId));
        if (e == null)
        {
            state = PersistState.Invalid;
            return null;
        }
        state = e.State;
        return e.State == PersistState.Ok ? e.ResolvesTo ?? e : null;
    }

    public ReferenceKind? KindOf(object entity) => (entity as FakeEntity)?.Kind;

    public EntityInfo Describe(object entity)
    {
        var e = (FakeEntity)entity;
        string json = SwAgent.Contracts.Serialization.JsonCodec.Serialize(e.Info);
        var copy = SwAgent.Contracts.Serialization.JsonCodec.Deserialize<EntityInfo>(json)!;
        copy.Kind = e.Kind;
        copy.Reference = null;
        copy.DistanceMm = null;
        return copy;
    }

    static readonly ReferenceKind[] FeatureKinds = { ReferenceKind.Feature, ReferenceKind.Plane, ReferenceKind.Axis, ReferenceKind.Point, ReferenceKind.Sketch };

    public object? FeatureByName(string name) =>
        Entities.FirstOrDefault(e => FeatureKinds.Contains(e.Kind) && e.Info.Name == name);

    public object? StandardPlane(int index) => Entities.Where(e => e.Kind == ReferenceKind.Plane).ElementAtOrDefault(index);

    public object? Origin() => Entities.FirstOrDefault(e => e.Kind == ReferenceKind.Point && e.Info.Name == "Ursprung");

    public bool ReadOnly { get; set; }

    /// <summary>Set by FakeSolidWorks.Track: makes this the active document.</summary>
    public Action? Activated { get; set; }

    public int Activations { get; private set; }

    public void Activate()
    {
        Activations++;
        Activated?.Invoke();
    }

    /// <summary>When set, the next save reports this instead of saving.</summary>
    public SaveOutcome? FailNextSave { get; set; }

    /// <summary>When true, a save reports success but writes nothing (SOLIDWORKS' silent SaveAs failure).</summary>
    public bool SaveWritesNothing { get; set; }

    public List<string> Saves { get; } = new List<string>();

    public SaveOutcome Save()
    {
        if (Document.Path == null) throw new InvalidOperationException("a document never saved cannot be saved in place");
        return Write(Document.Path);
    }

    public SaveOutcome SaveAs(string path)
    {
        var outcome = Write(path);
        if (outcome.Saved)
        {
            Document.Path = path;
            Document.Title = System.IO.Path.GetFileName(path);
        }
        return outcome;
    }

    /// <summary>The rebuild errors and warnings WhatsWrong reports.</summary>
    public List<RebuildItem> Problems { get; } = new List<RebuildItem>();

    /// <summary>What Rebuild returns.</summary>
    public bool RebuildSucceeds { get; set; } = true;

    /// <summary>Each Rebuild call's force flag.</summary>
    public List<bool> Rebuilds { get; } = new List<bool>();

    public bool Rebuild(bool force)
    {
        Rebuilds.Add(force);
        return RebuildSucceeds;
    }

    public IReadOnlyList<RebuildItem> WhatsWrong() => Problems.ToList();

    /// <summary>What Features returns.</summary>
    public List<FeatureRecord> Tree { get; } = new List<FeatureRecord>();

    public IReadOnlyList<FeatureRecord> Features() => Tree.ToList();

    /// <summary>Adds a top-level feature (with an entity for its reference) and returns its entity.</summary>
    public FakeEntity AddFeature(string name, string type, ReferenceKind kind = ReferenceKind.Feature, string? parent = null, bool suppressed = false, int errorCode = 0, bool warning = false)
    {
        var e = Add(kind, new EntityInfo { Name = name, Geometry = GeometryType.Other });
        Tree.Add(new FeatureRecord(e, name, type, parent, suppressed, errorCode, warning));
        return e;
    }

    public int Bodies { get; set; }

    public int SolidBodyCount() => Bodies;

    /// <summary>What Mass returns.</summary>
    public ModelMassResult? MassProperties { get; set; }

    public ModelMassResult? Mass() => MassProperties == null ? null : SwAgent.Contracts.Serialization.JsonCodec.Deserialize<ModelMassResult>(SwAgent.Contracts.Serialization.JsonCodec.Serialize(MassProperties));

    public string? MaterialName { get; set; }

    public string? Material() => MaterialName;

    /// <summary>What BoundingBox returns, by precise flag.</summary>
    public Box3? QuickBox { get; set; }
    public Box3? ExactBox { get; set; }

    public Box3? BoundingBox(bool precise) => precise ? ExactBox : QuickBox;

    public IReadOnlyList<object> Topology(TopologyKind kind)
    {
        var want = kind == TopologyKind.Edge ? ReferenceKind.Edge : kind == TopologyKind.Vertex ? ReferenceKind.Vertex : ReferenceKind.Face;
        return Entities.Where(e => e.Kind == want).Cast<object>().ToList();
    }

    /// <summary>Points DistanceMm was asked about.</summary>
    public List<Point3> DistanceAsked { get; } = new List<Point3>();

    /// <summary>The distance from any point is the entity's Info.DistanceMm, as SOLIDWORKS would measure it.</summary>
    public double DistanceMm(object entity, Point3 point)
    {
        DistanceAsked.Add(point);
        return ((FakeEntity)entity).Info.DistanceMm ?? 1e9;
    }

    /// <summary>What Selection reports.</summary>
    public List<SelectedObject> Selected { get; } = new List<SelectedObject>();

    public int SelectionCount() => Selected.Count;

    /// <summary>How many items Selection was asked to read, call by call.</summary>
    public List<int> SelectionReads { get; } = new List<int>();

    public IReadOnlyList<SelectedObject> Selection(int max)
    {
        SelectionReads.Add(max);
        return Selected.Take(max).ToList();
    }

    public void ClearSelection() => Selected.Clear();

    public bool Select(object entity, bool append)
    {
        if (!append) Selected.Clear();
        var kind = ((FakeEntity)entity).Kind;
        int type = kind switch
        {
            ReferenceKind.Edge => 1,
            ReferenceKind.Face => 2,
            ReferenceKind.Vertex => 3,
            ReferenceKind.Plane => 4,
            ReferenceKind.Axis => 5,
            ReferenceKind.Point => 6,
            ReferenceKind.Sketch => 9,
            _ => 22,
        };
        Selected.Add(new SelectedObject(type, entity));
        return true;
    }

    SaveOutcome Write(string path)
    {
        Saves.Add(path);
        if (FailNextSave != null)
        {
            var failed = FailNextSave;
            FailNextSave = null;
            return failed;
        }
        if (!SaveWritesNothing) System.IO.File.WriteAllText(path, "part");
        Document.Dirty = false;
        return new SaveOutcome(true, 0, 0);
    }
}
