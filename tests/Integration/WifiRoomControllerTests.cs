using CODConnect.Service.Wifi;
using CODConnect.Wifi;

namespace CODConnect.IntegrationTests;

public class WifiRoomControllerTests
{
    private static readonly HotspotSettings UsersOwn = new("MAKIMURA 2380", "users-own-password", "Auto");

    private sealed class FakeHotspot : IWifiHotspotManager
    {
        public bool IsOn;
        public HotspotSettings Settings = UsersOwn;
        public bool FailStartAfterConfiguring;
        public int FailApplyTimes;

        public Task<WifiCapabilityReport> CheckCapabilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WifiCapabilityReport(true, "ok"));

        public Task<HotspotStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new HotspotStatus(IsOn, Settings));

        public Task<WifiHotspot> StartHotspotAsync(HotspotSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            if (FailStartAfterConfiguring)
            {
                throw new InvalidOperationException("Windows could not start the console's Wi-Fi network");
            }

            IsOn = true;
            return Task.FromResult(new WifiHotspot(settings, "Local Area Connection* 2", "{id}", "192.168.137.1"));
        }

        public Task StopHotspotAsync(CancellationToken cancellationToken = default)
        {
            IsOn = false;
            return Task.CompletedTask;
        }

        public Task ApplySettingsAsync(HotspotSettings settings, CancellationToken cancellationToken = default)
        {
            if (FailApplyTimes-- > 0)
            {
                throw new IOException("tethering service busy");
            }

            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGuard : IDhcpGuard
    {
        public bool FailApply;
        public string? Address;

        public bool IsApplied => Address is not null;

        public void Apply(string hotspotAddress)
        {
            if (FailApply)
            {
                throw new InvalidOperationException("netsh failed");
            }

            Address = hotspotAddress;
        }

        public void Remove() => Address = null;
    }

    private sealed class FakeForwarding : IForwardingGuard
    {
        public HashSet<int> Disabled { get; } = new();
        public HashSet<(int, string)> Addresses { get; } = new();

        public int? IndexOf(string adapterId) => 23;

        public void AddAddress(int interfaceIndex, string address, string mask) => Addresses.Add((interfaceIndex, address));

        public void RemoveAddress(int interfaceIndex, string address) => Addresses.Remove((interfaceIndex, address));

        public int? Disable(string adapterId)
        {
            Disabled.Add(23);
            return 23;
        }

        public void Restore(int interfaceIndex) => Disabled.Remove(interfaceIndex);
    }

    private static string JournalPath() => Path.Combine(Path.GetTempPath(), "codconnect-tests", Guid.NewGuid().ToString("N"), "wifi.bin");

    private static void AssertRestored(FakeHotspot hotspot, FakeGuard guard, string journalPath)
    {
        Assert.False(hotspot.IsOn);
        Assert.Equal(UsersOwn, hotspot.Settings);
        Assert.False(guard.IsApplied);
        Assert.Null(new WifiJournal(journalPath).Read());
    }

    [Fact]
    public async Task NormalRoom_StartThenStop_LeavesThePcAsItWas()
    {
        var (hotspot, guard, path) = (new FakeHotspot(), new FakeGuard(), JournalPath());
        var controller = new WifiRoomController(hotspot, guard, new WifiJournal(path));

        var started = await controller.StartAsync();
        Assert.True(hotspot.IsOn);
        Assert.StartsWith(WifiRoomController.SsidPrefix, hotspot.Settings.Ssid);
        Assert.Equal("2.4 GHz", hotspot.Settings.Band);
        Assert.Equal(8, started.Settings.Passphrase.Length);
        Assert.True(guard.IsApplied);

        await controller.StopAsync();
        AssertRestored(hotspot, guard, path);
    }

    [Fact]
    public async Task Crash_NextStartUndoesEverything()
    {
        var (hotspot, guard, path) = (new FakeHotspot(), new FakeGuard(), JournalPath());
        await new WifiRoomController(hotspot, guard, new WifiJournal(path)).StartAsync();

        await new WifiRoomController(hotspot, guard, new WifiJournal(path)).StopAsync();

        AssertRestored(hotspot, guard, path);
    }

    [Fact]
    public async Task PowerLoss_NextBootRestoresTheUsersSetting()
    {
        var (hotspot, guard, path) = (new FakeHotspot(), new FakeGuard(), JournalPath());
        await new WifiRoomController(hotspot, guard, new WifiJournal(path)).StartAsync();
        hotspot.IsOn = false;

        await new WifiRoomController(hotspot, guard, new WifiJournal(path)).StopAsync();

        AssertRestored(hotspot, guard, path);
    }

    [Fact]
    public async Task StartFailure_UndoesItsOwnPartialChanges()
    {
        var (hotspot, guard, path) = (new FakeHotspot { FailStartAfterConfiguring = true }, new FakeGuard(), JournalPath());
        var controller = new WifiRoomController(hotspot, guard, new WifiJournal(path));

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync());

        AssertRestored(hotspot, guard, path);
    }

    [Fact]
    public async Task GuardFailure_UndoesTheHotspot()
    {
        var (hotspot, guard, path) = (new FakeHotspot(), new FakeGuard { FailApply = true }, JournalPath());
        var controller = new WifiRoomController(hotspot, guard, new WifiJournal(path));

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync());

        AssertRestored(hotspot, guard, path);
    }

    [Fact]
    public async Task FailedUndoStep_IsRetriedAtTheNextStart()
    {
        var (hotspot, guard, path) = (new FakeHotspot(), new FakeGuard(), JournalPath());
        var controller = new WifiRoomController(hotspot, guard, new WifiJournal(path));
        await controller.StartAsync();

        hotspot.FailApplyTimes = 1;
        await controller.StopAsync();
        Assert.NotNull(new WifiJournal(path).Read());
        Assert.False(hotspot.IsOn);
        Assert.False(guard.IsApplied);

        await new WifiRoomController(hotspot, guard, new WifiJournal(path)).StopAsync();
        AssertRestored(hotspot, guard, path);
    }

    [Fact]
    public async Task Recovery_LeavesAHotspotTheUserTookBackAlone()
    {
        var (hotspot, guard, path) = (new FakeHotspot(), new FakeGuard(), JournalPath());
        await new WifiRoomController(hotspot, guard, new WifiJournal(path)).StartAsync();

        var theirs = new HotspotSettings("New name they chose", "their-new-password", "5 GHz");
        hotspot.Settings = theirs;
        hotspot.IsOn = true;

        await new WifiRoomController(hotspot, guard, new WifiJournal(path)).StopAsync();

        Assert.True(hotspot.IsOn);
        Assert.Equal(theirs, hotspot.Settings);
        Assert.False(guard.IsApplied);
        Assert.Null(new WifiJournal(path).Read());
    }

    [Fact]
    public async Task UsersOwnHotspotAlreadyOn_IsRefused_AndNothingIsChanged()
    {
        var (hotspot, guard, path) = (new FakeHotspot { IsOn = true }, new FakeGuard(), JournalPath());
        var controller = new WifiRoomController(hotspot, guard, new WifiJournal(path));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync());
        Assert.Contains("already on", error.Message);

        Assert.True(hotspot.IsOn);
        Assert.Equal(UsersOwn, hotspot.Settings);
        Assert.False(guard.IsApplied);
        Assert.Null(new WifiJournal(path).Read());
    }

    [Fact]
    public async Task StrayGuardRule_WithoutARecord_IsRemoved()
    {
        var (hotspot, guard, path) = (new FakeHotspot(), new FakeGuard { Address = "192.168.137.1" }, JournalPath());
        await new WifiRoomController(hotspot, guard, new WifiJournal(path)).StopAsync();

        Assert.False(guard.IsApplied);
        Assert.Equal(UsersOwn, hotspot.Settings);
    }

    [Fact]
    public async Task NothingToUndo_IsANoOp()
    {
        var (hotspot, guard, path) = (new FakeHotspot(), new FakeGuard(), JournalPath());
        var controller = new WifiRoomController(hotspot, guard, new WifiJournal(path));
        await controller.StopAsync();
        await controller.StopAsync();
        AssertRestored(hotspot, guard, path);
    }

    [Fact]
    public async Task NewRoom_RefusedWhileTheLastUndoCannotFinish_KeepingTheUsersOriginal()
    {
        var (hotspot, guard, path) = (new FakeHotspot(), new FakeGuard(), JournalPath());
        await new WifiRoomController(hotspot, guard, new WifiJournal(path)).StartAsync();
        hotspot.IsOn = false;
        hotspot.FailApplyTimes = 1;

        var controller = new WifiRoomController(hotspot, guard, new WifiJournal(path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync());
        Assert.Equal(UsersOwn, new WifiJournal(path).Read()!.Original);

        await controller.StopAsync();
        AssertRestored(hotspot, guard, path);
    }

    [Fact]
    public async Task NewRoom_FinishesAnInterruptedUndoFirst()
    {
        var (hotspot, guard, path) = (new FakeHotspot(), new FakeGuard(), JournalPath());
        await new WifiRoomController(hotspot, guard, new WifiJournal(path)).StartAsync();
        hotspot.IsOn = false;

        var controller = new WifiRoomController(hotspot, guard, new WifiJournal(path));
        await controller.StartAsync();
        await controller.StopAsync();

        AssertRestored(hotspot, guard, path);
    }

    [Fact]
    public async Task HotspotRouting_IsOffDuringTheRoom_AndRestoredOnLeaveOrCrash()
    {
        var (hotspot, guard, path) = (new FakeHotspot(), new FakeGuard(), JournalPath());
        var forwarding = new FakeForwarding();

        await new WifiRoomController(hotspot, guard, new WifiJournal(path), forwarding: forwarding).StartAsync(pcAddress: ("10.42.0.253", "255.255.255.0"));
        Assert.Contains(23, forwarding.Disabled);
        Assert.Contains((23, "10.42.0.253"), forwarding.Addresses);
        Assert.Equal(23, new WifiJournal(path).Read()!.ForwardingInterfaceIndex);

        await new WifiRoomController(hotspot, guard, new WifiJournal(path), forwarding: forwarding).StopAsync();
        Assert.Empty(forwarding.Disabled);
        Assert.Empty(forwarding.Addresses);
        AssertRestored(hotspot, guard, path);
    }
}
