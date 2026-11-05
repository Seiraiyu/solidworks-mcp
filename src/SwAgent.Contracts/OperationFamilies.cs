using System;
using System.Collections.Generic;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Contracts;

/// <summary>How long a call may take when it names no deadline, per operation family; and the bounds on any deadline.</summary>
public static class OperationFamilies
{
    public static readonly TimeSpan MaxDeadline = TimeSpan.FromHours(1);

    public const long MaxDeadlineMs = 3_600_000;

    static readonly Dictionary<string, TimeSpan> Defaults = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
    {
        ["session"] = TimeSpan.FromSeconds(10),
        ["addin"] = TimeSpan.FromSeconds(5),
        ["dialog"] = TimeSpan.FromSeconds(10),
        ["diag"] = TimeSpan.FromMinutes(10),
        ["document"] = TimeSpan.FromSeconds(60),
        ["sketch"] = TimeSpan.FromSeconds(30),
        ["feature"] = TimeSpan.FromSeconds(60),
        ["model"] = TimeSpan.FromSeconds(30),
        ["selection"] = TimeSpan.FromSeconds(30),
        ["export"] = TimeSpan.FromSeconds(120),
        ["api"] = TimeSpan.FromSeconds(30),
    };

    public static bool TryGetDefault(string family, out TimeSpan deadline) => Defaults.TryGetValue(family, out deadline);

    public static TimeSpan DefaultDeadline(string family) =>
        TryGetDefault(family, out var deadline) ? deadline : throw new ArgumentException($"operation family '{family}' has no default deadline");

    /// <summary>The default deadline of the operation named <paramref name="method"/>; <paramref name="fallback"/> for a method no contract declares.</summary>
    public static TimeSpan DefaultFor(string method, TimeSpan fallback)
    {
        var op = OperationCatalog.Find(method);
        return op != null && TryGetDefault(op.Family, out var deadline) ? deadline : fallback;
    }

    /// <summary>Throws invalid_argument unless <paramref name="deadlineMs"/> is between 1 ms and one hour.</summary>
    public static void ValidateOverride(long deadlineMs)
    {
        if (deadlineMs < 1 || deadlineMs > MaxDeadlineMs)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"deadline_ms must be between 1 and {MaxDeadlineMs} (one hour), not {deadlineMs}",
                new JsonObject { ["deadline_ms"] = deadlineMs, ["max_ms"] = MaxDeadlineMs });
        }
    }
}
