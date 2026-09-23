using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace CODConnect.Service;

public sealed record SessionRecord(string RoomCode, string MemberId, string MemberSecret, string? AdminKey);

public sealed class SessionJournal
{
    private readonly string _path;
    private readonly object _gate = new();

    public SessionJournal(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CODCONNECT", "session.bin");
    }

    public void Write(SessionRecord record)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
                File.WriteAllBytes(_path, Protect(bytes));
            }
            catch
            {
            }
        }
    }

    public SessionRecord? Read()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(_path)
                    ? JsonSerializer.Deserialize<SessionRecord>(Unprotect(File.ReadAllBytes(_path)))
                    : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                File.Delete(_path);
            }
            catch
            {
            }
        }
    }

    public async Task RecoverAsync(HttpClient rendezvousHttp, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var record = Read();
        if (record is null)
        {
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var client = new RendezvousClient(rendezvousHttp);
            if (record.AdminKey is not null)
            {
                await client.CloseRoomAsync(record.RoomCode, record.AdminKey, timeout.Token).ConfigureAwait(false);
                log?.Invoke($"closed room {record.RoomCode} left open by the previous run");
            }
            else
            {
                await client.LeaveAsync(record.RoomCode, record.MemberId, record.MemberSecret, timeout.Token).ConfigureAwait(false);
                log?.Invoke($"left room {record.RoomCode} joined by the previous run");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            log?.Invoke($"could not tidy room {record.RoomCode} from the previous run ({ex.Message}); its lease will lapse");
        }
        finally
        {
            Clear();
        }
    }

    private static byte[] Protect(byte[] data)
        => OperatingSystem.IsWindows() ? ProtectedData.Protect(data, Encoding.UTF8.GetBytes("CODCONNECT.session"), DataProtectionScope.LocalMachine) : data;

    private static byte[] Unprotect(byte[] data)
        => OperatingSystem.IsWindows() ? ProtectedData.Unprotect(data, Encoding.UTF8.GetBytes("CODCONNECT.session"), DataProtectionScope.LocalMachine) : data;
}

public sealed class SessionLifecycleHostedService(
    SessionManager sessions,
    SessionJournal journal,
    HttpClient rendezvous,
    Action<string>? log = null,
    Wifi.WifiRoomController? wifi = null)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await journal.RecoverAsync(rendezvous, log, cancellationToken).ConfigureAwait(false);

        if (wifi is not null)
        {
            try
            {
                await wifi.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Wi-Fi recovery deferred: {ex.Message}");
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await sessions.StopAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.Invoke($"session stop during service shutdown did not finish cleanly: {ex.Message}");
        }
    }
}
