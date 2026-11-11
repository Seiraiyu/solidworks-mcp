using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>Finds the mate a request names: by its reference (r1.mate.…), which never resolves to another mate, or by its name (Coincident1).</summary>
public static class MateLookup
{
    public static object Find(ReferenceService references, ISwDocument doc, ISwAssembly asm, string text, string field)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new SwAgentException(ErrorCode.InvalidArgument, $"{field} is empty; name a mate by its reference or its name", new JsonObject { ["field"] = field });
        if (References.IsReference(text)) return references.Resolve(doc, text, field, ReferenceKind.Mate).Entity;
        var mate = asm.MateByName(text);
        if (mate != null) return mate;
        var names = asm.Mates().Select(m => (object?)asm.ReadMate(m).Name).Take(50).ToList();
        throw new SwAgentException(ErrorCode.InvalidArgument,
            $"{field}: {doc.Info().Title} has no mate named '{text}'; its mates are listed in details.mates (mate.list gives each one's reference)",
            new JsonObject { ["field"] = field, ["name"] = text, ["mates"] = names });
    }
}

/// <summary>A mate's side as resolved: the object SOLIDWORKS gave, and its description in assembly coordinates with its reference and component.</summary>
public sealed class ResolvedSide
{
    public ResolvedSide(object entity, EntityInfo info)
    {
        Entity = entity;
        Info = info;
    }

    public object Entity { get; }

    public EntityInfo Info { get; }
}

/// <summary>
/// A mate's side (design 455, Assembly references): an assembly reference, name@component or the assembly's own plane,
/// or with component a reference in that component's own part (GetCorresponding); for lock, a component. Field
/// pointers are /entities/&lt;i&gt;/entity and /entities/&lt;i&gt;/component.
/// </summary>
public static class MateEntities
{
    static readonly ReferenceKind[] Geometric =
        { ReferenceKind.Face, ReferenceKind.Edge, ReferenceKind.Vertex, ReferenceKind.Plane, ReferenceKind.Axis, ReferenceKind.Point };

    public static ResolvedSide Resolve(ReferenceService references, ISwDocument doc, ISwAssembly asm, MateType type, MateEntity side, int index)
    {
        string field = $"/entities/{index}/entity", componentField = $"/entities/{index}/component";
        if (type == MateType.Lock)
        {
            if (side.Component != null)
                throw new SwAgentException(ErrorCode.InvalidArgument, "a lock mate joins two components: name each in entity, and leave component out", new JsonObject { ["field"] = componentField });
            var c = ComponentLookup.Find(references, doc, asm, side.Entity, field);
            string name = asm.Identify(c).Name;
            var info = new EntityInfo { Kind = ReferenceKind.Component, Name = name, Component = name, Geometry = GeometryType.Other };
            info.Reference = references.Issue(doc, c, doc.Describe(c));
            return new ResolvedSide(c, info);
        }
        var entity = side.Component == null
            ? references.Resolve(doc, side.Entity, field, Geometric).Entity
            : references.ResolveInComponent(doc, side.Entity, side.Component, field, componentField, Geometric).Entity;
        return new ResolvedSide(entity, references.Describe(doc, entity));
    }
}
