using System.IO.Pipes;

namespace NFour.AutoUpdater.Client;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class IpcRouteAttribute(string route) : Attribute
{
    public string Route { get; } = Normalize(route);
    private static string Normalize(string route) => route.StartsWith('/') ? route : "/" + route;
}

public sealed record IpcRequest(string Route, JsonElement Payload);
public sealed record IpcError(string Code, string Message);

public interface IpcTransport : IAsyncDisposable
{
    ValueTask<JsonElement> RequestAsync(string route, JsonElement payload, CancellationToken cancellationToken = default);
    IAsyncEnumerable<JsonElement> StreamAsync(string route, JsonElement payload, CancellationToken cancellationToken = default);
}

public delegate IAsyncEnumerable<JsonElement> IpcRouteHandler(JsonElement payload, CancellationToken cancellationToken);

/// A route table shared by the in-process and named-pipe transports. Handlers may yield
/// multiple responses; the pipe protocol sends a terminal frame after the sequence.
public sealed class IpcRouter
{
    private readonly Dictionary<string, IpcRouteHandler> _routes = new(StringComparer.Ordinal);
    public IpcRouter Register(string route, IpcRouteHandler handler)
    {
        var normalized = Normalize(route);
        if (!_routes.TryAdd(normalized, handler)) throw new InvalidOperationException($"IPC route '{normalized}' is already registered.");
        return this;
    }

    internal bool TryGet(string route, out IpcRouteHandler handler) => _routes.TryGetValue(Normalize(route), out handler!);
    private static string Normalize(string route) => route.StartsWith('/') ? route : "/" + route;
}

/// In-process transport used by launchers and tests. It exercises the same route boundary
/// without weakening the production pipe's peer restriction.
public sealed class InProcessIpcTransport : IpcTransport
{
    private readonly IpcRouter _router;
    public InProcessIpcTransport(IpcRouter router) => _router = router;
    public InProcessIpcTransport(IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, ValueTask<JsonElement>>> routes)
    {
        _router = new IpcRouter();
        foreach (var (route, handler) in routes)
            _router.Register(route, (payload, cancellationToken) => Single(handler, payload, cancellationToken));

        static async IAsyncEnumerable<JsonElement> Single(Func<JsonElement, CancellationToken, ValueTask<JsonElement>> handler, JsonElement payload, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return await handler(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public async ValueTask<JsonElement> RequestAsync(string route, JsonElement payload, CancellationToken cancellationToken = default)
    {
        await foreach (var response in StreamAsync(route, payload, cancellationToken).ConfigureAwait(false)) return response;
        throw new KeyNotFoundException($"IPC route '{route}' is not registered or returned no response.");
    }
    public IAsyncEnumerable<JsonElement> StreamAsync(string route, JsonElement payload, CancellationToken cancellationToken = default)
        => DispatchAsync(_router, route, payload, cancellationToken);

    private static async IAsyncEnumerable<JsonElement> DispatchAsync(IpcRouter router, string route, JsonElement payload, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!router.TryGet(route, out var handler)) throw new KeyNotFoundException($"IPC route '{route}' is not registered.");
        await foreach (var response in handler(payload, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false)) yield return response;
    }
}

/// Authenticated-by-construction local IPC server. CurrentUserOnly prevents another local
/// account from opening the pipe; route handlers remain responsible for authorization inside
/// the updater process.
public sealed class NamedPipeIpcServer(string pipeName, IpcRouter router)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("A pipe name is required.", nameof(pipeName));
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            await HandleAsync(server, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(Stream stream, CancellationToken cancellationToken)
    {
        IpcRequest request;
        try { request = await IpcFraming.ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            try { await IpcFraming.WriteResponseAsync(stream, error: new IpcError("invalid_request", ex.Message), done: true, cancellationToken: CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            return;
        }
        if (!router.TryGet(request.Route, out var handler))
        {
            await IpcFraming.WriteResponseAsync(stream, error: new IpcError("route_not_found", $"IPC route '{request.Route}' is not registered."), done: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }
        try
        {
            await foreach (var payload in handler(request.Payload, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                await IpcFraming.WriteResponseAsync(stream, payload, null, done: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            await IpcFraming.WriteResponseAsync(stream, null, null, done: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await IpcFraming.WriteResponseAsync(stream, error: new IpcError("handler_failed", ex.Message), done: true, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
    }
}

/// Length-prefixed JSON named-pipe transport with streaming response support.
public sealed class NamedPipeIpcTransport(string pipeName, PipeDirection direction = PipeDirection.InOut) : IpcTransport
{
    private readonly string _pipeName = pipeName;
    private readonly PipeDirection _direction = direction;

    public async ValueTask<JsonElement> RequestAsync(string route, JsonElement payload, CancellationToken cancellationToken = default)
    {
        await foreach (var response in StreamAsync(route, payload, cancellationToken).ConfigureAwait(false)) return response;
        throw new IOException("IPC peer returned no response payload.");
    }

    public async IAsyncEnumerable<JsonElement> StreamAsync(string route, JsonElement payload, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(".", _pipeName, _direction, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await IpcFraming.WriteRequestAsync(pipe, new IpcRequest(Normalize(route), payload), cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var response = await IpcFraming.ReadResponseAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (response.Error is not null) throw new IOException($"IPC {response.Error.Code}: {response.Error.Message}");
            if (response.Payload is { } value) yield return value;
            if (response.Done) yield break;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private static string Normalize(string route) => route.StartsWith('/') ? route : "/" + route;
}

internal sealed record IpcResponse(JsonElement? Payload, IpcError? Error, bool Done);

internal static class IpcFraming
{
    private const int MaxFrameLength = 16 * 1024 * 1024;

    public static ValueTask WriteRequestAsync(Stream stream, IpcRequest request, CancellationToken cancellationToken)
        => WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)), cancellationToken);

    public static ValueTask WriteResponseAsync(Stream stream, JsonElement? payload = null, IpcError? error = null, bool done = false, CancellationToken cancellationToken = default)
        => WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new IpcResponse(payload, error, done), new JsonSerializerOptions(JsonSerializerDefaults.Web)), cancellationToken);

    public static async ValueTask<IpcRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var document = await ReadDocumentAsync(stream, cancellationToken).ConfigureAwait(false);
        var request = document.Deserialize<IpcRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return request ?? throw new InvalidDataException("IPC request is empty.");
    }

    public static async ValueTask<IpcResponse> ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var document = await ReadDocumentAsync(stream, cancellationToken).ConfigureAwait(false);
        var response = document.Deserialize<IpcResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return response ?? throw new InvalidDataException("IPC response is empty.");
    }

    private static async ValueTask<JsonDocument> ReadDocumentAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBytes);
        if (length is < 2 or > MaxFrameLength) throw new InvalidDataException("IPC frame length is outside the permitted range.");
        var body = new byte[length];
        await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    private static async ValueTask WriteFrameAsync(Stream stream, byte[] body, CancellationToken cancellationToken)
    {
        if (body.Length > MaxFrameLength) throw new InvalidDataException("IPC response exceeds the maximum frame length.");
        await stream.WriteAsync(BitConverter.GetBytes(body.Length), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("IPC peer closed the pipe mid-frame.");
            buffer = buffer[read..];
        }
    }
}
