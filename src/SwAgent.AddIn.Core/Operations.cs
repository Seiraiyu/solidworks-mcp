using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

public interface IOperationContext
{
    string Method { get; }

    /// <summary>When the client stops waiting. An operation already running is never interrupted.</summary>
    DateTime DeadlineUtc { get; }

    ILog Log { get; }
}

internal sealed class OperationContext : IOperationContext
{
    public OperationContext(string method, DateTime deadlineUtc, ILog log)
    {
        Method = method;
        DeadlineUtc = deadlineUtc;
        Log = log;
    }

    public string Method { get; }
    public DateTime DeadlineUtc { get; }
    public ILog Log { get; }
}

public interface IOperation
{
    string Name { get; }
    Type RequestType { get; }
    Type ResultType { get; }
    object Run(object request, IOperationContext context);
    void Verify(object request, object result, IOperationContext context);
}

/// <summary>
/// One operation. <see cref="Run"/> and then <see cref="Verify"/> run on SOLIDWORKS' UI thread, one request at a
/// time. Throw <see cref="SwAgentException"/> to fail with a specific code; any other exception becomes
/// operation_failed from Run and verification_failed from Verify.
/// </summary>
public abstract class Operation<TRequest, TResult> : IOperation
    where TRequest : class
    where TResult : class
{
    protected Operation()
    {
        var a = typeof(TRequest).GetCustomAttribute<OperationAttribute>()
            ?? throw new InvalidOperationException($"{typeof(TRequest).Name} has no [Operation] attribute");
        if (a.ResultType != typeof(TResult))
            throw new InvalidOperationException($"{GetType().Name} returns {typeof(TResult).Name} but '{a.Name}' is declared to return {a.ResultType.Name}");
        Name = a.Name;
    }

    public string Name { get; }
    public Type RequestType => typeof(TRequest);
    public Type ResultType => typeof(TResult);

    protected abstract TResult Run(TRequest request, IOperationContext context);

    /// <summary>Checks that Run did what it was asked. Every operation has one, even if it only re-reads what it reported.</summary>
    protected abstract void Verify(TRequest request, TResult result, IOperationContext context);

    object IOperation.Run(object request, IOperationContext context) =>
        Run((TRequest)request, context) ?? throw new InvalidOperationException($"{Name} returned no result");

    void IOperation.Verify(object request, object result, IOperationContext context) =>
        Verify((TRequest)request, (TResult)result, context);
}

public sealed class RegistryException : Exception
{
    public RegistryException(IReadOnlyList<string> problems)
        : base("the operation registry is inconsistent: " + string.Join("; ", problems))
    {
        Problems = problems;
    }

    public IReadOnlyList<string> Problems { get; }
}

/// <summary>Exactly one implementation for every operation the contracts declare, or it refuses to exist.</summary>
public sealed class OperationRegistry
{
    readonly Dictionary<string, IOperation> _byName;

    OperationRegistry(Dictionary<string, IOperation> byName)
    {
        _byName = byName;
    }

    public IReadOnlyCollection<string> Names => _byName.Keys;

    public bool TryGet(string name, out IOperation? operation) => _byName.TryGetValue(name, out operation);

    public static OperationRegistry Create(IEnumerable<OperationDescriptor> contracts, IEnumerable<IOperation> implementations)
    {
        var problems = new List<string>();
        var declared = contracts.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var byName = new Dictionary<string, IOperation>(StringComparer.Ordinal);
        foreach (var op in implementations)
        {
            if (!declared.TryGetValue(op.Name, out var contract))
            {
                problems.Add($"{op.GetType().Name} implements '{op.Name}', which no contract declares");
                continue;
            }
            if (contract.RequestType != op.RequestType || contract.ResultType != op.ResultType)
            {
                problems.Add($"{op.GetType().Name} does not match the contract types of '{op.Name}'");
                continue;
            }
            if (byName.TryGetValue(op.Name, out var existing))
            {
                problems.Add($"'{op.Name}' is implemented twice: {existing.GetType().Name} and {op.GetType().Name}");
                continue;
            }
            byName.Add(op.Name, op);
        }
        foreach (var name in declared.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!byName.ContainsKey(name)) problems.Add($"'{name}' has no implementation");
        }
        if (problems.Count > 0) throw new RegistryException(problems);
        return new OperationRegistry(byName);
    }

    /// <summary>Builds every concrete <see cref="IOperation"/> among <paramref name="candidates"/> with <paramref name="factory"/>.</summary>
    public static OperationRegistry Discover(IEnumerable<OperationDescriptor> contracts, IEnumerable<Type> candidates, Func<Type, IOperation> factory)
    {
        var operations = new List<IOperation>();
        var problems = new List<string>();
        foreach (var type in candidates
                     .Where(t => t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters && typeof(IOperation).IsAssignableFrom(t))
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            try
            {
                operations.Add(factory(type));
            }
            catch (Exception e)
            {
                var cause = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                problems.Add($"{type.Name} could not be created: {cause.Message}");
            }
        }
        if (problems.Count > 0) throw new RegistryException(problems);
        return Create(contracts, operations);
    }
}
