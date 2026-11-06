using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>
/// api.call, the escape hatch (design 371, Escape hatch): a late-bound call on any member of SOLIDWORKS' API, on the
/// UI thread, in the dispatcher, with the same deadline, dialog watch and error mapping as any operation. SOLIDWORKS'
/// own units. Every reference in the request is resolved before anything is called; the refused members are never
/// called; when it rebuilds, the user's open sketch or command is waited for as any model change waits for it. There
/// is no rollback. Every call, whatever its outcome, is logged in full to api-calls.log.
/// </summary>
public sealed class ApiCallOperation : Operation<ApiCallRequest, ApiCallResult>
{
    readonly AddInServices _services;

    public ApiCallOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ApiCallResult Run(ApiCallRequest request, IOperationContext context)
    {
        var watch = Stopwatch.StartNew();
        ApiCallResult? result = null;
        SwAgentError? error = null;
        string? document = null;
        try
        {
            result = Call(request, watch, out document);
            context.Log.Info($"api.call: {request.Target} {result.Member} in {result.DurationMs} ms");
            return result;
        }
        catch (SwAgentException e)
        {
            error = e.Error;
            throw;
        }
        catch (Exception e)
        {
            error = new SwAgentError(ErrorCode.ApiError, e.Message);
            throw;
        }
        finally
        {
            _services.ApiCalls?.Write(new ApiCallRecord
            {
                Target = request.Target,
                Document = document,
                Member = request.Member,
                Args = JsonCodec.ToJson(request.Args),
                Result = result == null ? null : JsonCodec.ToJson(result),
                Error = error,
                DurationMs = watch.ElapsedMilliseconds,
            });
        }
    }

    ApiCallResult Call(ApiCallRequest request, Stopwatch watch, out string? document)
    {
        document = null;
        var sw = _services.SolidWorks;
        string target = (request.Target ?? "").Trim();
        bool app = string.Equals(target, "app", StringComparison.OrdinalIgnoreCase);
        bool whole = string.Equals(target, "document", StringComparison.OrdinalIgnoreCase);
        if (target.Length == 0)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                "target is required: app, document, or a reference (or the name of a feature, plane, axis or point) to call the member on",
                new JsonObject { ["field"] = "target" });
        }
        // A refused member answers as refused, before any document, sketch or reference is looked at.
        ApiCallGuard.CheckNames(ApiCallGuard.Split(request.Member), request.Member);
        var args = request.Args ?? new List<object?>();
        var references = ApiValues.References(args);
        bool rebuild = request.Rebuild ?? !app;

        ISwDocument? doc = !app || rebuild || request.Document != null || references.Count > 0
            ? DocumentLocator.Find(sw, request.Document)
            : null;
        if (doc != null) document = DocumentLocator.Label(doc.Info());
        if (rebuild) ModelChangeGuard.Require(sw, doc!, Name);

        // Every reference resolves before anything is called, so a request with one bad reference calls nothing.
        object on = app ? sw.ApiObject() : whole ? doc!.ApiObject() : _services.References.Resolve(doc!, target, "target").Entity;
        var resolved = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (reference, field) in references)
        {
            if (!resolved.ContainsKey(reference)) resolved[reference] = _services.References.Resolve(doc!, reference, field).Entity;
        }

        var outcome = ApiCaller.Call(_services.ApiBinder, on, request.Member, args, resolved);
        var named = doc ?? sw.ActiveDoc();
        object? Describe(object o) => DescribeObject(named, o);
        var result = new ApiCallResult
        {
            Member = outcome.Member,
            Interfaces = outcome.Interfaces,
            Result = ApiValues.ToJson(outcome.Value, Describe),
        };
        foreach (var o in outcome.Out) result.Out[o.Key] = ApiValues.ToJson(o.Value, Describe);
        if (rebuild)
        {
            doc!.Rebuild(false);
            var problems = doc.WhatsWrong();
            result.Rebuild = new SwAgent.Contracts.RebuildStatus
            {
                Errors = problems.Count(p => !p.Warning),
                Warnings = problems.Count(p => p.Warning),
                Items = problems.ToList(),
            };
        }
        if (doc != null) result.Document = doc.Info();
        result.DurationMs = watch.ElapsedMilliseconds;
        return result;
    }

    /// <summary>An object the API returned: its reference when it is an entity SOLIDWORKS can persist in the document, and its interface's name.</summary>
    JsonObject DescribeObject(ISwDocument? doc, object value)
    {
        var json = new JsonObject();
        try
        {
            if (doc != null && doc.KindOf(value) != null)
            {
                string? reference = _services.References.Describe(doc, value).Reference;
                if (reference != null) json["ref"] = reference;
            }
        }
        catch (Exception)
        {
            // Not an entity of this document after all: its interface is all that can be said of it.
        }
        json["interface"] = _services.ApiBinder.InterfaceName(value);
        return json;
    }

    /// <summary>
    /// Nothing is checked against the request (the escape hatch does what it is told). The document it acted on must
    /// still be open: every member the guard knows to close, reload or replace a document is refused before the call,
    /// so a document gone afterwards means a way round the guard, reported loudly. This catches a close, not a reload
    /// to the same path, which is why those members are refused by name rather than left to this check.
    /// </summary>
    protected override void Verify(ApiCallRequest request, ApiCallResult result, IOperationContext context)
    {
        if (result.Document == null) return;
        string label = DocumentLocator.Label(result.Document);
        if (_services.SolidWorks.Documents().Any(d => DocumentKeys.Same(DocumentKeys.Of(d.Info()), DocumentKeys.Of(result.Document)))) return;
        throw new SwAgentException(ErrorCode.VerificationFailed,
            $"{label} is no longer open after api.call {result.Member}; close documents only with document.close",
            new JsonObject { ["check"] = "document_open", ["document"] = label });
    }
}
