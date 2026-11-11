using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// Finds the component a request names (design 455, Components): by its reference (r1.component.…), which resolves only
/// in its own assembly and never to another component, or by its name (bracket-1, sub-1/pin-1).
/// </summary>
public static class ComponentLookup
{
    public static object Find(ReferenceService references, ISwDocument doc, ISwAssembly asm, string text, string field)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new SwAgentException(ErrorCode.InvalidArgument, $"{field} is empty; name a component by its reference or its name", new JsonObject { ["field"] = field });
        if (References.IsReference(text)) return references.Resolve(doc, text, field, ReferenceKind.Component).Entity;
        var component = asm.ComponentByName(text);
        if (component != null) return component;
        var names = asm.Components(false).Select(c => asm.Read(c).Name).Take(50).Select(n => (object?)n).ToList();
        throw new SwAgentException(ErrorCode.InvalidArgument,
            $"{field}: {doc.Info().Title} has no component named '{text}'; its components are listed in details.components (inside a subassembly, name it sub-1/pin-1)",
            new JsonObject { ["field"] = field, ["name"] = text, ["components"] = names });
    }
}
