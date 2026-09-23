# Third-Party Notices

CODCONNECT is open source. Every third-party dependency is
license-checked before its code or binaries are distributed.

| Component | Use | License | Status |
| --- | --- | --- | --- |
| .NET 10 runtime | Application platform (self-contained in the app and service) | MIT | Standard distribution |
| Microsoft.Extensions.Hosting, .Hosting.WindowsServices, System.ServiceProcess.ServiceController, System.Security.Cryptography.ProtectedData, System.IO.Pipes.AccessControl | Service host, DPAPI, pipe security | MIT | NuGet packages |
| CommunityToolkit.WinUI.Notifications | Windows notifications for room chat | MIT | NuGet package |
| SoftEther VPN Server and Client 4.44 | Room tunnel, NAT traversal, VPN Azure relay | Apache License 2.0 | Server runtime files and the official client installer bundled in the MSI. Check the notice requirements for this distribution method before release |
| Npcap | Packet capture and injection on the console's interface | Npcap License | Not redistributed: users install it from npcap.com |

No third-party source code has been copied into this repository.
