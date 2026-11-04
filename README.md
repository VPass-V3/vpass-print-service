# VPass Print Service

Background service that prints VPass visitor badges on a label printer connected to a PC
(Windows, macOS or Linux). It replaces the legacy Electron "Printer App".

## How it works

```
sign-in  ─►  VPass server renders the badge with the template chosen in the admin portal
             (Setup ▸ Badge Printing Templates), stores a job per paired printer, and
             publishes a wake-up on WowSocks channel Printer-{id}
                     │
vpass-print run      ▼
  WebSocket wake-up ─┐
  45 s timer ────────┼─► GET printer/jobs/pending ─► print JPEG ─► POST printer/jobs/{id}/ack
  resume from sleep ─┘
  heartbeat every 60 s ─► admin portal shows online / offline / version
```

* The service **never draws a badge**. It prints the server-rendered image exactly, rotating
  landscape templates (3 and 4) 90° for continuous label rolls (configurable).
* Delivery is **pull based**: the server keeps every job until it is acknowledged, so a lost
  socket message, a sleeping PC or a server deploy cannot lose a badge. A local ledger of printed
  job ids stops a re-delivered job from printing twice. Jobs not printed within 10 minutes expire.
* Enrolment uses a **one-time pairing code** from the admin portal; no staff credentials live on the PC.

## Install

1. Admin portal → Setup → Badge Printing → **Manage printers** → *Add Printer*. Copy the pairing code.
2. On the PC connected to the badge printer:

| OS | Steps |
|---|---|
| Windows | Run the installer. It asks for the code and region, then installs the **VPass Print Service** Windows service. |
| macOS | Install the `.pkg`, then `sudo vpass-print pair <code> --region au` |
| Linux | `sudo apt install ./vpass-print-service_<ver>_amd64.deb`, then `sudo vpass-print pair <code> --region au` |

Regions: `au` (default), `uk`, `us`, `ca`, `qa`, `dev`.

3. Pick the printer when prompted, or `vpass-print printers` then `vpass-print set-printer "<name>"`.
4. `vpass-print test-print` and `vpass-print status`.

The card in the admin portal turns **Online** within a minute.

## Brother QL quick setup (Windows, verified on a QL-820NWB)

```
vpass-print printers                                   # pick the queue whose port reaches the printer
vpass-print set-printer "Brother QL-820NWB" --paper "62mm"          # DK-22205 continuous 62 mm roll
vpass-print set-printer "Brother QL-820NWB" --paper "62mm x 100mm"  # DK-11202 die-cut 62 x 100 labels
vpass-print test-print
```

`vpass-print papers` lists every size the driver offers. The service runs as `SYSTEM`, which gets the driver's
*Printing Defaults*, not your *Printing Preferences*, so the media must be pinned with `--paper` (or set under
Printer Properties → Advanced → Printing Defaults). The page is always printed portrait; the service rotates
the two landscape templates itself.

## Commands

```
vpass-print pair <code> [--region au|us|uk|ca] [--printer "<name>"] [--paper <name>] [--no-rotate]
vpass-print printers
vpass-print papers [--printer "<name>"]
vpass-print set-printer "<name>" [--paper <name>] [--rotate|--no-rotate] [--copies N]
vpass-print test-print [--landscape]
vpass-print install [--account DOMAIN\user --password ***]   # admin / sudo
vpass-print uninstall | restart | status | logs [--follow] | version
```

Changes made with the CLI are picked up by the running service within a few seconds.

## Files

| | Windows | macOS | Linux |
|---|---|---|---|
| Data (config, token, ledger, status) | `%ProgramData%\VPass\PrintService` | `/Library/Application Support/VPass/PrintService` | `/var/lib/vpass-print-service` |
| Logs | `…\logs\vpass-print-YYYYMMDD.log` (7 days) | same | same |
| Service | `VPassPrintService` (sc.exe) | `io.vpass.printservice` (launchd) | `vpass-print-service.service` (systemd) |

Override the data directory with `VPASS_PRINT_DATA_DIR`.

## Troubleshooting

* **Offline in the portal** – `vpass-print status`; check the PC is on and has internet. The service
  retries forever with backoff.
* **"Printer not ready"** – the OS printer is offline or missing. On Windows a service running as
  *LocalSystem* only sees printers installed for all users; for a per-user network printer reinstall
  with `vpass-print install --account DOMAIN\user`.
* **Job shows Printed but nothing comes out (Windows)** – the service runs as *LocalSystem* and uses the
  driver's **Printing Defaults** (Properties → Advanced → Printing Defaults…), not your per-user Printing
  Preferences. Set the loaded media there (e.g. 62 mm continuous, auto cut), or pin it with
  `vpass-print papers` then `vpass-print set-printer "<name>" --paper "<size>"`. From 1.0.2 the service
  watches the Windows queue and reports such jobs as failed with the queue's error text.
* **Job sits "Printing, Paused" in the Windows queue (owner SYSTEM)** – the Brother/Dymo driver paused it
  to show a prompt that a service session cannot display. Printer Properties → **Ports → untick "Enable
  bidirectional support"**, and set the loaded media under Properties → Advanced → Printing Defaults. From
  1.0.3 the service resumes a paused job once and otherwise reports it as failed with this hint.
* **Revoked** – an admin revoked / deleted / re-paired the printer. Generate a new code and run `pair` again.
* **Badge printed twice** – a kiosk at the same location also has a local printer. Turn one off.
* Logs: `vpass-print logs --follow`.

## Development

```
dotnet test
dotnet run --project src/VPass.PrintService -- status
dotnet publish src/VPass.PrintService -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```

Server side: `server_mobile/Controllers/PrinterController.cs` (device API),
`server_organisation/Controllers/PrinterController.cs` (admin API),
`server_organisation/SQL/Printers/001_create_printer_tables.sql`.

Releases: push a `v1.2.3` tag; `.github/workflows/release.yml` builds the installers. Code signing
(Windows Authenticode, Apple Developer ID + notarisation) is **not** wired up yet and is required
before public distribution.
