# Hosting on a VPS (Debian 13)

This guide stands a Galaxies Reborn server up on a fresh Debian 13 ("trixie")
VPS. Everything the server needs runs in containers, so the only things
installed on the host are Docker and Git.

The desktop launcher is a graphical application and a VPS has no desktop, so the
server is driven by `reborn`, the headless command line front end. It runs the
same code the desktop launcher does.

## What you need

| | |
| --- | --- |
| Host | Debian 13, x86-64 |
| Memory | 8 GB minimum, 16 GB recommended. The first build is memory hungry. |
| Disk | 60 GB free. Sources are about 250 MB; the built server and database are the rest. |
| Access | A user with `sudo`, and the ports below reachable from the internet. |

Building the server takes a long time on a small VPS: it compiles the C++
services and initializes a database. Two shared vCPUs will work but expect the
first build to run for hours.

Looking for a host? See [Want To Host Online?](#want-to-host-online) below.

## 1. Update the system

```bash
sudo apt update && sudo apt upgrade -y
sudo apt install -y git ca-certificates curl
```

## 2. Install Docker

Debian's own `docker.io` package is usually too old for Compose v2, so use
Docker's repository:

```bash
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc

echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] \
https://download.docker.com/linux/debian $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
| sudo tee /etc/apt/sources.list.d/docker.list > /dev/null

sudo apt update
sudo apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
```

Let your user run Docker without `sudo`, then re-open the session so the new
group applies:

```bash
sudo usermod -aG docker "$USER"
newgrp docker
```

Confirm both the engine and Compose are ready. The launcher checks for both:

```bash
docker version
docker compose version
```

> If `docker compose version` fails, the Compose plugin is missing and the
> server cannot be prepared.

## 3. Install the launcher CLI

Download `reborn` from the [releases page](https://github.com/Galaxies-Reborn/reborn-launcher/releases)
and put it on your `PATH`:

```bash
curl -fsSL -o reborn https://github.com/Galaxies-Reborn/reborn-launcher/releases/latest/download/reborn-linux-x64
chmod +x reborn
sudo mv reborn /usr/local/bin/
reborn help
```

It is a single self-contained binary; no .NET runtime is needed.

<details>
<summary>Or build it from source</summary>

```bash
sudo apt install -y dotnet-sdk-8.0
git clone https://github.com/Galaxies-Reborn/reborn-launcher.git
cd reborn-launcher
dotnet publish src/RebornLauncher.Cli -c Release -r linux-x64 -o ./out
sudo install -m 0755 ./out/reborn /usr/local/bin/reborn
```

</details>

## 4. Choose what to host

```bash
reborn list
```

This reads the release catalog and prints the projects and variants. It
transfers only manifests, a few hundred kilobytes, and downloads no source. A
variant marked `(not published)` has no sources yet.

## 5. Prepare the server

Use your VPS's public IP as the address. Players cannot reach a server that
advertises `127.0.0.1`.

```bash
reborn prepare --variant x64-dx9-vanilla --address 203.0.113.10
```

This fetches only the chosen variant's sources (~250 MB), generates the
instance's configuration and secrets, builds the containers, initializes the
database, and starts the server.

The first run takes a long time. To watch it without holding the SSH session
open, run it under `tmux`:

```bash
sudo apt install -y tmux
tmux new -s reborn
reborn prepare --variant x64-dx9-vanilla --address 203.0.113.10
# detach with Ctrl-b then d; return later with: tmux attach -t reborn
```

No client is downloaded. Galaxies Reborn does not distribute game clients, and a
server does not need one. Players bring their own and point it at your address.

### Hosting SWGEmu instead

SWGEmu's Core3 needs `.tre` files from a retail Pre-CU client. They cannot be
distributed, so copy your own to the VPS and point at them:

```bash
scp *.tre user@203.0.113.10:~/tre/
reborn prepare --variant core3 --address 203.0.113.10 --tre ~/tre
```

## 6. Open the ports

The server stack publishes these:

| Ports | Protocol | Purpose |
| --- | --- | --- |
| 44450–44465 | TCP **and** UDP | Login, zone, and the game servers. The login port you set (default 44453) is in this range. |
| 5000–5001 | TCP and UDP | Station Chat |

Each range is published on **both** protocols. Most of the game runs over UDP, so
a firewall that only opens TCP will let players reach very little.

With `ufw`:

```bash
sudo apt install -y ufw
sudo ufw allow OpenSSH
sudo ufw allow 44450:44465/tcp
sudo ufw allow 44450:44465/udp
sudo ufw allow 5000:5001/tcp
sudo ufw allow 5000:5001/udp
sudo ufw enable
```

If your provider has its own firewall in front of the VPS, open the same ranges
there too.

> The stack also publishes Oracle on **1521**. Do not open it. It is only needed
> inside the instance, and exposing a database to the internet invites trouble.
> The `ufw` rules above deliberately leave it closed.

SWGEmu publishes its own ports instead; `reborn status --variant core3` lists
what its container actually exposes.

## 7. Run it

```bash
reborn status --variant x64-dx9-vanilla
reborn logs   --variant x64-dx9-vanilla
reborn stop   --variant x64-dx9-vanilla
reborn start  --variant x64-dx9-vanilla
```

The containers are set to restart unless stopped, so the server comes back by
itself after a reboot. Nothing further is needed to survive a restart.

Players point the launcher's public address at your VPS IP and connect.

## Want To Host Online?

We suggest [ZAP-Hosting](https://zap-hosting.com/GalaxiesReborn?voucher=montgojo-a-7826).
Our link gives you **20% off for life**.

This is a referral link: using it supports Galaxies Reborn at no extra cost to
you. The 20% discount is ZAP-Hosting's offer, not ours, and their terms apply.
Any provider offering a Debian 13 VPS with the resources above will work.

## Troubleshooting

**`docker compose version` fails.** The Compose plugin is not installed. Repeat
step 2; `docker-compose-plugin` is the package.

**Permission denied talking to Docker.** Your user is not in the `docker` group,
or the session predates being added. Run `newgrp docker` or log out and back in.

**The build is killed partway.** Almost always out of memory. Check with
`dmesg | grep -i oom`. Add swap or move to a larger plan:

```bash
sudo fallocate -l 8G /swapfile && sudo chmod 600 /swapfile
sudo mkswap /swapfile && sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
```

**Players cannot connect but the server is running.** The UDP ports are not
open, or the instance advertises the wrong address. `reborn prepare` records the
address you passed; re-run it with the correct `--address` to change it.

**Sources did not download.** `reborn` reports which repositories failed by name.
Git exits successfully even when a submodule clone fails, so an empty tree is
detected and reported rather than being mistaken for success. Re-run the same
command; it resumes rather than starting over.
