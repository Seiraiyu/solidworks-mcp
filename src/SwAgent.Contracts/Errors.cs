using System;
using System.ComponentModel;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Contracts;

/// <summary>Stable error codes. Their wire names (snake_case) never change once released; new codes are only added.</summary>
public enum ErrorCode
{
    SwNotRunning,
    SwCrashed,
    SwMultipleInstances,
    AddinNotLoaded,
    AddinIncompatible,
    Timeout,
    Busy,
    DialogOpen,
    InvalidArgument,
    ReferenceUnresolved,
    WrongDocumentType,
    OperationFailed,
    VerificationFailed,
    ApiError,

    /// <summary>SOLIDWORKS is running but has not finished starting (or waits at a sign-in or update prompt).</summary>
    SwStarting,
}

/// <summary>A failure as an agent sees it: a stable code, what to do next, and structured details.</summary>
[Description("A typed failure.")]
public sealed class SwAgentError
{
    public SwAgentError()
    {
    }

    public SwAgentError(ErrorCode code, string message, JsonObject? details = null)
    {
        Code = code;
        Message = message;
        Details = details;
    }

    [Description("Stable error code; branch on this.")]
    public ErrorCode Code { get; set; }

    [Required]
    [Description("What happened and what to do next.")]
    public string Message { get; set; } = "";

    [Description("Structured facts about the failure.")]
    public JsonObject? Details { get; set; }

    public override string ToString() => $"{WireNames.ForEnum(Code)}: {Message}";
}

/// <summary>Throw this to fail with a specific code; anything else is mapped to a generic code.</summary>
public sealed class SwAgentException : Exception
{
    public SwAgentException(ErrorCode code, string message, JsonObject? details = null)
        : this(new SwAgentError(code, message, details))
    {
    }

    public SwAgentException(SwAgentError error) : base(error.Message)
    {
        Error = error;
    }

    public SwAgentError Error { get; }

    public ErrorCode Code => Error.Code;
}
