using System;

namespace SwAgent.Contracts;

/// <summary>Overrides the snake_case name a property or enum member has on the wire.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class WireNameAttribute : Attribute
{
    public WireNameAttribute(string name) { Name = name; }
    public string Name { get; }
}

/// <summary>
/// Marks a property that must be present (and not null) on the wire. Non-nullable value types are required
/// without it; reference types and <c>Nullable&lt;T&gt;</c> are optional unless marked.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RequiredAttribute : Attribute
{
}
