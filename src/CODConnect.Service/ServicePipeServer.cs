using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CODConnect.Service;

public sealed class ServicePipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly IIpcHandler _handler;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public ServicePipeServer(IIpcHandler handler, string? pipeName = null, Action<string>? log = null)
    {
        _handler = handler;
        _pipeName = pipeName ?? ServiceIpc.DefaultPipeName;
        _log = log;
    }

    public string PipeName => _pipeName;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_acceptLoop is not null)
            {
                return Task.CompletedTask;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            loop = _acceptLoop;
            _acceptLoop = null;
            _cts?.Cancel();
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreateServerStream();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _log?.Invoke($"could not create pipe instance: {ex.Message}");
                await Task.Delay(500, token).ConfigureAwait(false);
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                return;
            }
            catch (IOException)
            {
                server.Dispose();
                continue;
            }

            _ = Task.Run(() => ServeClientAsync(server, token), CancellationToken.None);
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream server, CancellationToken token)
    {
        try
        {
            await using var _ = server.ConfigureAwait(false);
            await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(server, leaveOpen: true);
            while (!token.IsCancellationRequested)
            {
                var request = await IpcWire.ReadRequestAsync(reader, token).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                var response = await _handler.HandleAsync(request, token).ConfigureAwait(false);
                await IpcWire.WriteResponseAsync(writer, response, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            _ = ex;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"pipe client error: {ex.Message}");
        }
    }

    private NamedPipeServerStream CreateServerStream()
    {
        const int inBufferSize = 4 * 1024;
        const int outBufferSize = 64 * 1024;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                return NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize,
                    outBufferSize,
                    BuildPipeSecurity());
            }
            catch (UnauthorizedAccessException ex)
            {
                _log?.Invoke($"pipe ACL hardening skipped: {ex.Message}");
            }
        }

        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize,
            outBufferSize);
    }

    public static PipeSecurity BuildPipeSecurity()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));

        foreach (var identifier in new[]
                 {
                     (SecurityIdentifier)WindowsIdentity.GetCurrent().User!,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
        {
            security.AddAccessRule(new PipeAccessRule(identifier, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return security;
    }
}
