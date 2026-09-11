# Galaxies Reborn Launcher

Reborn Launcher installs and operates Star Wars Galaxies server source sets. It
fetches exact source revisions from the
[Galaxies Reborn umbrella](https://github.com/Galaxies-Reborn/galaxies-reborn),
prepares an isolated instance, builds and runs the server in containers, and can
point a client you already own at it.

It runs on Windows, Linux, and macOS. A headless command line front end,
`reborn`, drives the same code for servers without a desktop.

## What it can install

Choose a project, then an era where one applies, then a variant. Nothing is
downloaded until you choose.

| Project | Eras | Notes |
| --- | --- | --- |
| Galaxies Reborn | NGE, CU, Pre-CU | The Galaxies Reborn repositories. Only NGE `x64-dx11-vanilla` is published so far. |
| SWGEmu | — | [Core3](https://github.com/swgemu/Core3), an independent Pre-CU emulator with its own container stack. |

The catalog is read from the umbrella at run time, so published variants appear
without a launcher update.

## Game clients

**Galaxies Reborn does not distribute game clients.** Bring your own. The
launcher points it at your server, writes the login address into its
configuration, and starts it. On Linux and macOS it runs through Wine or a Proton
wrapper that you install.

SWGEmu additionally needs `.tre` files from a retail Pre-CU client, which also
cannot be distributed. Point the launcher at them when preparing that variant.

## Supported container environments

| Environment | Compose command |
| --- | --- |
| Docker Desktop | `docker compose` |
| Podman Desktop | `podman compose` with a Compose provider |
| Rancher Desktop using Moby | `docker compose` |
| Rancher Desktop using containerd | `nerdctl compose` |

The launcher validates both the engine and Compose before changing an instance.
Install and start one container environment before preparing a server.

## Using the launcher

1. Start Reborn Launcher.
2. Pick a project, era, renderer, and variant.
3. Set the instance folder, public address, and container runtime, then choose
   **Prepare instance**.
4. Use the **Server** tab for status, start, stop, and logs.
5. Use the **Clients** tab to point at your own client and launch it.

The first build initializes the database, compiles the C++ services, Station
Chat, and Java scripts, and starts the server. It takes a long time. Database and
work volumes are kept when the stack is stopped.

Instance secrets are generated locally in the instance's `.env.reborn`. They are
never committed or sent anywhere.

## Hosting on a VPS

See [`docs/VPS-DEBIAN-13.md`](docs/VPS-DEBIAN-13.md) for a full Debian 13 setup,
including the ports the stack publishes.

```bash
reborn list
reborn prepare --variant x64-dx11-vanilla --address 203.0.113.10
```

## Local login

With the default local authentication configuration, the LoginServer accepts an
account name and derives a station ID from it; it does not validate the client
password. `local` is the conventional password for development instances.

## Development

Requires the .NET 8 SDK. Git is used to fetch sources; on Windows the installer
ships it, and elsewhere the system Git is used.

```bash
dotnet test RebornLauncher.sln
dotnet run --project src/RebornLauncher.Desktop     # desktop launcher
dotnet run --project src/RebornLauncher.Cli -- list # headless
```

`REBORN_UMBRELLA_REMOTE` points the launcher at a different umbrella, such as a
fork or a local checkout.

Publishing is per platform:

```bash
dotnet publish src/RebornLauncher.Desktop -c Release -r linux-x64
dotnet publish src/RebornLauncher.Cli     -c Release -r linux-x64
```

Tagging `v*` builds and publishes both for Linux, Windows, and macOS.

Variant and umbrella layout is documented in the
[umbrella repository](https://github.com/Galaxies-Reborn/galaxies-reborn).
