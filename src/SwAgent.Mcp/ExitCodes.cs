using SwAgent.Contracts;

namespace SwAgent.Mcp;

/// <summary>
/// swmcp's exit codes. A typed error exits with its own code, grouped by class: 1x reaching SOLIDWORKS,
/// 2x SOLIDWORKS unavailable, 3x a bad request, 4x the operation failed.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Internal = 1;
    public const int Usage = 2;
    public const int NotRegistered = 3;

    public static int For(ErrorCode code) => code switch
    {
        ErrorCode.SwNotRunning => 10,
        ErrorCode.SwMultipleInstances => 11,
        ErrorCode.AddinNotLoaded => 12,
        ErrorCode.AddinIncompatible => 13,
        ErrorCode.SwCrashed => 14,
        ErrorCode.Timeout => 20,
        ErrorCode.Busy => 21,
        ErrorCode.DialogOpen => 22,
        ErrorCode.InvalidArgument => 30,
        ErrorCode.ReferenceUnresolved => 31,
        ErrorCode.WrongDocumentType => 32,
        ErrorCode.OperationFailed => 40,
        ErrorCode.VerificationFailed => 41,
        ErrorCode.ApiError => 42,
        _ => Internal,
    };
}
