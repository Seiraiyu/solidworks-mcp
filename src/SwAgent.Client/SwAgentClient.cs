using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Client;

public sealed class ClientOptions
{
    /// <summary>The SOLIDWORKS process to reach; null means the only one running.</summary>
    public int? ProcessId { get; set; }

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan HelloDeadline { get; set; } = OperationFamilies.DefaultDeadline("session");

    /// <summary>The deadline of a call that names none and whose method no contract declares; declared methods use their family's default.</summary>
    public TimeSpan DefaultDeadline { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long past a request's deadline the client still waits, so the add-in's own timeout answer can arrive.</summary>
    public TimeSpan ResponseGrace { get; set; } = TimeSpan.FromSeconds(1);

    public IProcessList Processes { get; set; } = SystemProcessList.Instance;

    public IPipeDirectory Pipes { get; set; } = SystemPipeDirectory.Instance;

    /// <summary>The pipe name for a SOLIDWORKS process id; tests point it at a pipe of their own.</summary>
    public Func<int, string> PipeNameFor { get; set; } = Protocol.PipeName;

    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>Called on a pool thread for every notification the add-in sends (phase 2 events).</summary>
    public Action<string, JsonObject>? OnNotification { get; set; }
}

/// <summary>
/// The one way anything talks to the add-in: finds SOLIDWORKS, connects, checks the protocol with session.hello,
/// then sends typed calls.
/// </summary>
public sealed class SwAgentClient : IDisposable
{
    readonly AddinConnection _connection;
    readonly ClientOptions _options;

    SwAgentClient(AddinConnection connection, int processId, SessionHelloResult hello, ClientOptions options)
    {
        _connection = connection;
        ProcessId = processId;
        Hello = hello;
        _options = options;
    }

    /// <summary>This client's informational version.</summary>
    public static string Version =>
        typeof(SwAgentClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SwAgentClient).Assembly.GetName().Version!.ToString();

    public int ProcessId { get; }

    public SessionHelloResult Hello { get; }

    public bool IsConnected => _connection.IsOpen;

    public static async Task<SwAgentClient> ConnectAsync(ClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ClientOptions();
        int pid = new AddinLocator(options.Processes, options.Pipes, options.PipeNameFor).Locate(options.ProcessId);
        var connection = await AddinConnection.OpenAsync(options.PipeNameFor(pid), pid, options, cancellationToken).ConfigureAwait(false);
        try
        {
            var request = new SessionHelloRequest { ProtocolVersion = Protocol.Version, ClientVersion = Version };
            object? tree = await connection.CallAsync("session.hello", (JsonObject)JsonCodec.ToJson(request)!, options.HelloDeadline, cancellationToken)
                .ConfigureAwait(false);
            if (tree is JsonObject raw && raw.TryGetValue("protocol_version", out var v) && v is long theirs && theirs != Protocol.Version)
            {
                throw Incompatible($"the add-in speaks protocol {theirs} but this swmcp speaks {Protocol.Version}",
                    theirs, raw.TryGetValue("addin_version", out var av) ? av as string : null);
            }
            SessionHelloResult hello;
            try
            {
                hello = JsonCodec.FromJson<SessionHelloResult>(tree);
            }
            catch (SwAgentException e)
            {
                throw Incompatible($"the add-in's hello does not match this swmcp's contract ({e.Message})", null, null);
            }
            options.Logger.LogInformation("hello: add-in {AddinVersion}, SOLIDWORKS {Revision}, process {Pid}", hello.AddinVersion, hello.SwRevision, hello.ProcessId);
            return new SwAgentClient(connection, pid, hello, options);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Runs one operation; throws <see cref="SwAgentException"/> with a typed error on any failure.</summary>
    public async Task<TResult> CallAsync<TRequest, TResult>(TRequest request, TimeSpan? deadline = null, CancellationToken cancellationToken = default)
        where TRequest : class
        where TResult : class
    {
        var op = typeof(TRequest).GetCustomAttribute<OperationAttribute>()
            ?? throw new ArgumentException($"{typeof(TRequest).Name} is not an operation request");
        if (op.ResultType != typeof(TResult))
            throw new ArgumentException($"'{op.Name}' returns {op.ResultType.Name}, not {typeof(TResult).Name}");
        object? tree = await CallAsync(op.Name, (JsonObject)JsonCodec.ToJson(request)!, deadline, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonCodec.FromJson<TResult>(tree);
        }
        catch (SwAgentException e)
        {
            throw Incompatible($"the add-in's result for {op.Name} does not match this swmcp's contract ({e.Message})", null, Hello.AddinVersion);
        }
    }

    /// <summary>Runs one operation by name with JSON parameters and returns the result as a JSON tree. With no deadline, the family's default.</summary>
    public Task<object?> CallAsync(string method, JsonObject parameters, TimeSpan? deadline = null, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(method, parameters, deadline ?? OperationFamilies.DefaultFor(method, _options.DefaultDeadline), cancellationToken);

    public void Dispose() => _connection.Dispose();

    static SwAgentException Incompatible(string what, long? addinProtocol, string? addinVersion)
    {
        var details = new JsonObject
        {
            ["client_protocol_version"] = (long)Protocol.Version,
            ["client_version"] = Version,
        };
        if (addinProtocol != null) details["addin_protocol_version"] = addinProtocol.Value;
        if (addinVersion != null) details["addin_version"] = addinVersion;
        return new SwAgentException(ErrorCode.AddinIncompatible,
            $"{what}; install the add-in and swmcp from the same release (swmcp {Version}, add-in {addinVersion ?? "unknown"})", details);
    }
}
