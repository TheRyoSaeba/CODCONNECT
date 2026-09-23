# Installer

`build.ps1` publishes the app and service as self-contained single-file executables and
builds `installer/build/CODCONNECT-Setup.msi` with WiX.

```powershell
# App only (no prerequisite payloads)
powershell -File installer\build.ps1 -Version 0.1.22

# Release build: bundles Npcap and SoftEther
powershell -File installer\build.ps1 -Version 0.1.22 -PrereqsDir C:\prereqs
```

`-PrereqsDir` must contain `npcap-<ver>.exe` and the official
`softether-vpnclient-<ver>-windows-*.exe`. The SoftEther VPN Server runtime files are taken
from the build machine's SoftEther install, or from `<PrereqsDir>\softether-server`, or from
`-SoftEtherServerDir`. `-RendezvousUrl` defaults to the hosted rendezvous
(`https://codconnect-rendezvous.onrender.com/`); pass `http://127.0.0.1:5090/` for a local
one. Raise `-Version` on every build you install over an older one.

## What the MSI does

| Item | How |
| --- | --- |
| Desktop app | Installed to Program Files, Start menu shortcut, runs unelevated |
| CODCONNECT service | Auto-start Windows service as SYSTEM; restarts 5 s after a crash |
| Rendezvous URL | System environment variable `CODCONNECT_RENDEZVOUS_URL` |
| SoftEther VPN Server | Runtime files installed; the service registers them with the runtime's `/install` on first start (SoftEther's own installer has no silent mode) |
| SoftEther VPN Client | Official installer bundled; its wizard is launched from the last setup page when the client isn't installed yet (it installs the VPN adapter driver) |
| Npcap | Not bundled: its licence does not allow redistribution. Users install it from npcap.com; the app says so when it is missing. `-BundleNpcap` adds it for private test builds only |
| Uninstall | Runs `CODConnect.Service.exe --uninstall-cleanup` before the files are removed: closes any room, undoes Wi-Fi mode, removes room hubs and firewall rules. Skipped on upgrade |

Before a public release: a code-signing certificate.
