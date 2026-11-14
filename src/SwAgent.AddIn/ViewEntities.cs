using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>A <see cref="ViewEntity"/> resolved: the view's name, its model's document, and the entity in that model.</summary>
public sealed class ResolvedViewEntity
{
    public ResolvedViewEntity(string view, ISwDocument model, object entity, ReferenceKind kind)
    {
        View = view;
        Model = model;
        Entity = entity;
        Kind = kind;
    }

    public string View { get; }

    public ISwDocument Model { get; }

    public object Entity { get; }

    public ReferenceKind Kind { get; }
}

/// <summary>
/// Resolves a <see cref="ViewEntity"/> (design 611, Protocol and contracts): the view by reference or name, then the
/// entity in the view's model in exactly one of the forms phase 4 takes (a reference issued in that model; a
/// {component, entity} pair; a name such as Boss-Extrude1 or Axis1@pin-1), with phase 4's resolver, never substituted.
/// </summary>
public static class ViewEntities
{
    public static ResolvedViewEntity Resolve(ReferenceService references, ISwDocument doc, ISwDrawing drawing, ViewEntity given, string field, params ReferenceKind[] accepted)
    {
        if (given.Reference != null && given.Entity != null)
            throw Invalid(field + "/reference", $"{field} gives both reference and entity; give one");
        if (given.Reference == null && given.Entity == null)
            throw Invalid(field + "/entity", $"{field} names no entity: give reference (issued in the view's model) or entity (a name, or with component an entity of that component)");
        if (given.Component != null && given.Entity == null)
            throw Invalid(field + "/component", $"{field}: component goes with entity (an entity of that component), not with reference, which already names its component");
        string view = references.ResolveView(doc, drawing, given.View, field + "/view");
        var model = drawing.ModelOf(view)
            ?? throw Invalid(field + "/view", $"{field}/view: {view} shows no model that is loaded, so it has no entity to name");
        var (entity, kind) = given.Reference != null
            ? references.Resolve(model, given.Reference, field + "/reference", accepted)
            : given.Component != null
                ? references.ResolveInComponent(model, given.Entity!, given.Component, field + "/entity", field + "/component", accepted)
                : references.Resolve(model, given.Entity!, field + "/entity", accepted);
        return new ResolvedViewEntity(view, model, entity, kind);
    }

    static SwAgentException Invalid(string field, string message) =>
        new SwAgentException(ErrorCode.InvalidArgument, message, new JsonObject { ["field"] = field });
}
