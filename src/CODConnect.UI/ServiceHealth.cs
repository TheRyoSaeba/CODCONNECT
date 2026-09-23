using System.ComponentModel;
using System.IO;
using System.ServiceProcess;

namespace CODConnect.UI;

internal enum ServiceRunState { Unknown, Running, Stopped, Starting, Stopping, Paused, Pausing, Resuming, Missing, AccessDenied }
internal enum ServiceReply { Unknown, Responding, NoResponse, AccessDenied, Error }
internal sealed record ServiceHealthSnapshot(ServiceRunState Codconnect, ServiceRunState Npcap,
    ServiceRunState SoftEther, ServiceReply Reply, DateTimeOffset CheckedAt);

internal static class ServiceHealth
{
    internal static async Task<ServiceHealthSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var statesTask = Task.Run(() => new[]
        {
            ReadState("CODConnect"), ReadState("npcap"), ReadState("SEVPNCLIENT")
        }, cancellationToken);
        var reply = ServiceReply.Unknown;
        try
        {
            await using var pipe = new ServiceIpcClient();
            var response = await pipe.SendAsync(new IpcRequest("ping"), deadline.Token).ConfigureAwait(false);
            reply = response.Ok ? ServiceReply.Responding : ServiceReply.Error;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { reply = ServiceReply.NoResponse; }
        catch (UnauthorizedAccessException) { reply = ServiceReply.AccessDenied; }
        catch (Exception ex) when (ex is IOException or System.TimeoutException or InvalidOperationException)
        { reply = ServiceReply.NoResponse; }
        cancellationToken.ThrowIfCancellationRequested();
        var states = await statesTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new(states[0], states[1], states[2], reply, DateTimeOffset.Now);
    }

    internal static ServiceRunState ReadState(string name, Func<ServiceControllerStatus>? read = null)
    {
        try
        {
            ServiceControllerStatus status;
            if (read is not null) status = read();
            else { using var controller = new ServiceController(name); status = controller.Status; }
            return status switch
            {
                ServiceControllerStatus.Running => ServiceRunState.Running,
                ServiceControllerStatus.Stopped => ServiceRunState.Stopped,
                ServiceControllerStatus.StartPending => ServiceRunState.Starting,
                ServiceControllerStatus.StopPending => ServiceRunState.Stopping,
                ServiceControllerStatus.Paused => ServiceRunState.Paused,
                ServiceControllerStatus.PausePending => ServiceRunState.Pausing,
                ServiceControllerStatus.ContinuePending => ServiceRunState.Resuming,
                _ => ServiceRunState.Unknown
            };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (ex is UnauthorizedAccessException or System.Security.SecurityException) return ServiceRunState.AccessDenied;
            var native = ex as Win32Exception ?? ex.InnerException as Win32Exception;
            return native?.NativeErrorCode switch { 1060 => ServiceRunState.Missing, 5 => ServiceRunState.AccessDenied, _ => ServiceRunState.Unknown };
        }
    }

    internal static string Describe(ServiceRunState state) => state switch
    {
        ServiceRunState.Missing => "Not installed", ServiceRunState.AccessDenied => "Access denied",
        ServiceRunState.Unknown => "Status unavailable", _ => state.ToString()
    };
}
