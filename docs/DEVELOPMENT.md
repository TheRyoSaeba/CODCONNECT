# Development

## Build and test

Requires the .NET 10 SDK on Windows.

```bash
dotnet build CODConnect.slnx
dotnet test CODConnect.slnx
dotnet run --project tools/CODConnect.UiChecks -- artifacts/ui-review/screenshots
```

The tests use simulated network links, so `dotnet test` runs without Npcap, SoftEther or admin
rights. `CODConnect.UiChecks` renders every page offscreen and checks layout rules (no visible
scrollbars, nothing cut off at the smallest window size); its renders also produce the README
screenshots.

To build the installer, see [installer/README.md](../installer/README.md).

## Repository layout

| Path | Contents |
| --- | --- |
| `src/CODConnect.UI` | The desktop app (WPF) |
| `src/CODConnect.Service` | The Windows service that runs rooms: session manager, named-pipe IPC, Wi-Fi mode, crash recovery, uninstall cleanup |
| `src/CODConnect.Sessions` | A room: transports (SoftEther, TCP), reconnect, console Internet, room chat |
| `src/CODConnect.PacketEngine` | Frame codecs, Npcap capture and inject, the switch, the room's DHCP |
| `src/CODConnect.SoftEther` | SoftEther server admin (JSON-RPC) and VPN Client control |
| `src/CODConnect.Wifi` | Wi-Fi mode: capability check and Windows hotspot control |
| `src/CODConnect.Discovery` | Console identification |
| `src/CODConnect.Protocol` | Room codes, room server client, service IPC messages |
| `src/CODConnect.Core` | Shared types: MAC and IPv4 addresses, counters, change ledger |
| `src/CODConnect.Networking` | Tunnel abstraction and the direct TCP tunnel |
| `server/CODConnect.Rendezvous` | The room server (ASP.NET Core; deployed to Render from `render.yaml`) |
| `installer/` | WiX installer build |
| `tests/` | Unit, integration and simulation tests |
| `tools/CODConnect.PocTool` | Developer command-line tool |
| `tools/CODConnect.UiChecks` | Offscreen UI renders and layout checks |

## Developer tool

```bash
# Adapters and raw packets
dotnet run --project tools/CODConnect.PocTool -- doctor
dotnet run --project tools/CODConnect.PocTool -- list-adapters
dotnet run --project tools/CODConnect.PocTool -- sniff <adapter> --seconds 60 --out capture.pcap

# The installed service
dotnet run --project tools/CODConnect.PocTool -- service status
dotnet run --project tools/CODConnect.PocTool -- service host-wifi
dotnet run --project tools/CODConnect.PocTool -- service join <code>
dotnet run --project tools/CODConnect.PocTool -- service stop

# Wi-Fi mode on this PC
dotnet run --project tools/CODConnect.PocTool -- wifi check
```
