using System.IO.Pipes;

namespace NFour.AutoUpdater.Client;

/// <summary>Associates an IPC route with a handler type or method.</summary><param name="route">The route name.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class IpcRouteAttribute(string route) : Attribute
{
    /// <summary>Gets the normalized absolute route.</summary>
    public string Route { get; } = Normalize(route);
    private static string Normalize(string route) => route.StartsWith('/') ? route : "/" + route;
}

/// <summary>Contains one routed IPC request.</summary><param name="Route">The normalized route.</param><param name="Payload">The JSON payload.</param>
public sealed record IpcRequest(string Route, JsonElement Payload);
/// <summary>Contains a stable IPC error code and explanation.</summary><param name="Code">The error code.</param><param name="Message">The explanation.</param>
public sealed record IpcError(string Code, string Message);

/// <summary>Sends unary and streaming requests across a local IPC boundary.</summary>
public interface IpcTransport : IAsyncDisposable
{
    /// <summary>Sends a request and returns its first response.</summary>
    ValueTask<JsonElement> RequestAsync(string route, JsonElement payload, CancellationToken cancellationToken = default);
    /// <summary>Sends a request and streams every response.</summary>
    IAsyncEnumerable<JsonElement> StreamAsync(string route, JsonElement payload, CancellationToken cancellationToken = default);
}

/// <summary>Handles an IPC request as an asynchronous response sequence.</summary>
public delegate IAsyncEnumerable<JsonElement> IpcRouteHandler(JsonElement payload, CancellationToken cancellationToken);

/// <summary>Stores routes shared by in-process and named-pipe transports.</summary>
public sealed class IpcRouter
{
    private readonly Dictionary<string, IpcRouteHandler> _routes = new(StringComparer.Ordinal);
    /// <summary>Registers one normalized route and handler.</summary>
    public IpcRouter Register(string route, IpcRouteHandler handler)
    {
        var normalized = Normalize(route);
        if (!_routes.TryAdd(normalized, handler)) throw new InvalidOperationException($"IPC route '{normalized}' is already registered.");
        return this;
    }

    internal bool TryGet(string route, out IpcRouteHandler handler) => _routes.TryGetValue(Normalize(route), out handler!);
    private static string Normalize(string route) => route.StartsWith('/') ? route : "/" + route;
}

/// <summary>Dispatches IPC requests in process while preserving the route boundary.</summary>
public sealed class InProcessIpcTransport : IpcTransport
{
    private readonly IpcRouter _router;
    /// <summary>Initializes an in-process transport over a router.</summary>
    public InProcessIpcTransport(IpcRouter router) => _router = router;
    /// <summary>Initializes an in-process transport from unary route delegates.</summary>
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

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    /// <inheritdoc />
    public async ValueTask<JsonElement> RequestAsync(string route, JsonElement payload, CancellationToken cancellationToken = default)
    {
        await foreach (var response in StreamAsync(route, payload, cancellationToken).ConfigureAwait(false)) return response;
        throw new KeyNotFoundException($"IPC route '{route}' is not registered or returned no response.");
    }
    /// <inheritdoc />
    public IAsyncEnumerable<JsonElement> StreamAsync(string route, JsonElement payload, CancellationToken cancellationToken = default)
        => DispatchAsync(_router, route, payload, cancellationToken);

    private static async IAsyncEnumerable<JsonElement> DispatchAsync(IpcRouter router, string route, JsonElement payload, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!router.TryGet(route, out var handler)) throw new KeyNotFoundException($"IPC route '{route}' is not registered.");
        await foreach (var response in handler(payload, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false)) yield return response;
    }
}

/// <summary>Hosts current-user-only IPC routes on a named pipe.</summary><param name="pipeName">The local pipe name.</param><param name="router">The route table.</param>
public sealed class NamedPipeIpcServer(string pipeName, IpcRouter router)
{
    /// <summary>Accepts and handles pipe connections until cancellation.</summary>
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

/// <summary>Provides length-prefixed JSON named-pipe IPC with streaming responses.</summary><param name="pipeName">The local pipe name.</param><param name="direction">The pipe direction.</param>
public sealed class NamedPipeIpcTransport(string pipeName, PipeDirection direction = PipeDirection.InOut) : IpcTransport
{
    private readonly string _pipeName = pipeName;
    private readonly PipeDirection _direction = direction;

    /// <inheritdoc />
    public async ValueTask<JsonElement> RequestAsync(string route, JsonElement payload, CancellationToken cancellationToken = default)
    {
        await foreach (var response in StreamAsync(route, payload, cancellationToken).ConfigureAwait(false)) return response;
        throw new IOException("IPC peer returned no response payload.");
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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
