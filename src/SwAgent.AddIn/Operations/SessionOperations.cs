using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Operations;

public sealed class SessionHelloOperation : Operation<SessionHelloRequest, SessionHelloResult>
{
    readonly AddInServices _services;

    public SessionHelloOperation(AddInServices services)
    {
        _services = services;
    }

    protected override SessionHelloResult Run(SessionHelloRequest request, IOperationContext context)
    {
        context.Log.Info($"hello from a client speaking protocol {request.ProtocolVersion}, version {request.ClientVersion ?? "unknown"}");
        return new SessionHelloResult
        {
            ProtocolVersion = Protocol.Version,
            AddinVersion = _services.AddinVersion,
            SwRevision = _services.SolidWorks.RevisionNumber,
            ProcessId = _services.SolidWorks.ProcessId,
        };
    }

    protected override void Verify(SessionHelloRequest request, SessionHelloResult result, IOperationContext context)
    {
        if (result.SwRevision.Length == 0)
            throw new SwAgentException(ErrorCode.VerificationFailed, "SOLIDWORKS reported an empty revision number");
    }
}

public sealed class SessionStatusOperation : Operation<SessionStatusRequest, SessionStatusResult>
{
    readonly AddInServices _services;

    public SessionStatusOperation(AddInServices services)
    {
        _services = services;
    }

    protected override SessionStatusResult Run(SessionStatusRequest request, IOperationContext context)
    {
        var sw = _services.SolidWorks;
        return new SessionStatusResult
        {
            SwRevision = sw.RevisionNumber,
            ProcessId = sw.ProcessId,
            AddinVersion = _services.AddinVersion,
            ProtocolVersion = Protocol.Version,
            ActiveDocument = sw.ActiveDocument(),
            OpenDocumentCount = sw.OpenDocumentCount(),
            QueuedRequests = _services.QueuedRequests(),
        };
    }

    /// <summary>The document reported is still the active one.</summary>
    protected override void Verify(SessionStatusRequest request, SessionStatusResult result, IOperationContext context)
    {
        var now = _services.SolidWorks.ActiveDocument();
        var reported = result.ActiveDocument;
        bool same = now == null
            ? reported == null
            : reported != null && reported.Title == now.Title && reported.Path == now.Path && reported.Type == now.Type;
        if (!same)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"the active document changed while the status was read (reported '{reported?.Title ?? "none"}', now '{now?.Title ?? "none"}'); ask again");
        }
    }
}
