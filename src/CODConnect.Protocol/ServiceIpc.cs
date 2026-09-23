using System.IO.Pipes;
using System.Text.Json;

namespace CODConnect.Protocol;

public static class ServiceIpc
{
    public const string DefaultPipeName = "codconnect.service.v1";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public static class IpcWire
{
    public static async Task<IpcRequest?> ReadRequestAsync(TextReader reader, CancellationToken cancellationToken = default)
    {
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return line is null ? null : JsonSerializer.Deserialize<IpcRequest>(line, ServiceIpc.JsonOptions);
    }

    public static async Task WriteRequestAsync(TextWriter writer, IpcRequest request, CancellationToken cancellationToken = default)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, ServiceIpc.JsonOptions)).ConfigureAwait(false);
    }

    public static async Task<IpcResponse?> ReadResponseAsync(TextReader reader, CancellationToken cancellationToken = default)
    {
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return line is null ? null : JsonSerializer.Deserialize<IpcResponse>(line, ServiceIpc.JsonOptions);
    }

    public static async Task WriteResponseAsync(TextWriter writer, IpcResponse response, CancellationToken cancellationToken = default)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, ServiceIpc.JsonOptions)).ConfigureAwait(false);
    }
}

public sealed class ServiceIpcClient : IAsyncDisposable
{
    private readonly string _pipeName;

    public ServiceIpcClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? ServiceIpc.DefaultPipeName;
    }

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await IpcWire.WriteRequestAsync(writer, request, cancellationToken).ConfigureAwait(false);
        var response = await IpcWire.ReadResponseAsync(reader, cancellationToken).ConfigureAwait(false);
        return response ?? throw new IOException("The service closed the connection without a response.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
