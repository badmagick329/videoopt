# Remote SSH processing

Remote SSH mode keeps VideoOptimiser and its job database on Windows while a manually provisioned Linux server performs CRF search and AV1 encoding. Validation, review, and finalization remain local. This first version does not create or delete the server.

## 1. Create a dedicated SSH key on Windows

Run this once in PowerShell:

```powershell
ssh-keygen -t ed25519 -a 100 -f "$env:USERPROFILE\.ssh\video-optimiser-hetzner-cx43-ed25519" -C "video-optimiser-hetzner-cx43"
```

VideoOptimiser connects in non-interactive SSH mode. For the simplest dedicated worker setup, press Enter twice when `ssh-keygen` asks for a passphrase. If the key has a passphrase, load it into the Windows OpenSSH agent before running `doctor` or processing jobs.

Copy only the public key:

```powershell
Get-Content "$env:USERPROFILE\.ssh\video-optimiser-hetzner-cx43-ed25519.pub" | Set-Clipboard
```

In Hetzner, select **Add SSH Key**, paste the public key, and name it `video-optimiser-hetzner-cx43`. Leave **Set as default key** unchecked. Never upload or share `video-optimiser-hetzner-cx43-ed25519` without the `.pub` extension; that is the private key.

## 2. Create the Hetzner server

Create one server with the following selections:

- Type: CX43
- Location: Helsinki
- Image: Ubuntu 24.04 x86-64, not Ubuntu 26.04
- Networking: IPv4 and IPv6
- SSH key: `video-optimiser-hetzner-cx43`
- Volumes: none
- Backups: disabled
- Server count: one
- Name: `video-optimiser-cx43-hel1`

Record the server's public IPv4 address after creation. The CX43's 160 GB local disk permits a source of roughly 60 GB under the configured `2.5` free-disk multiplier after allowing for the operating system and installed tools.

## 3. Configure the SSH alias for bootstrap

Open `%USERPROFILE%\.ssh\config` and add:

```sshconfig
Host video-worker
    HostName <server-ip>
    User root
    IdentityFile ~/.ssh/video-optimiser-hetzner-cx43-ed25519
    IdentitiesOnly yes
```

Replace `<server-ip>` with the server's IPv4 address. Test the alias:

```powershell
ssh video-worker
```

On the first connection, OpenSSH displays the server's host-key fingerprint. Confirm that you are connecting to the IP shown in Hetzner, accept the key, and then exit the remote shell:

```bash
exit
```

Do not disable host-key checking. If a replacement server later reuses the address, remove only the obsolete entry with `ssh-keygen -R <server-ip>` before connecting to the replacement.

## 4. Bootstrap the worker

Adding the public key in Hetzner grants SSH access, but it does not install VideoOptimiser's worker software. Copy the bootstrap script once from the VideoOptimiser repository root, using the `video-worker` alias configured above:

```powershell
scp .\scripts\bootstrap-remote-worker.sh video-worker:/root/
ssh video-worker "bash /root/bootstrap-remote-worker.sh"
```

The script is safe to rerun. It installs the following pinned stack under `/opt/video-optimiser`:

- `ab-av1` 0.10.4
- FFmpeg 8.0
- SVT-AV1 3.1.0
- libvmaf 3.0.0
- libdav1d software AV1 decoding support

It also creates the password-disabled `videoopt` user, copies the selected SSH public key from root, and gives `videoopt` access to `/var/tmp/video-optimiser`. The worker account has no sudo access.

If the server was bootstrapped with an older copy of this script, or `doctor` reports that FFmpeg lacks the `libdav1d` decoder, copy the current script to `/root` and run it again with the two commands above. The bootstrap detects the missing decoder and rebuilds FFmpeg even when its FFmpeg 8.0 version marker already exists.

## 5. Switch the SSH alias to the worker account

After the bootstrap completes, edit the existing `video-worker` entry and change only `User root` to `User videoopt`:

```sshconfig
Host video-worker
    HostName <server-ip>
    User videoopt
    IdentityFile ~/.ssh/video-optimiser-hetzner-cx43-ed25519
    IdentitiesOnly yes
```

Verify the final connection and installed encoder:

```powershell
ssh video-worker "id -un; ab-av1 --version"
```

The output should identify `videoopt` and report `ab-av1 0.10.4`.

## 6. Configure VideoOptimiser

### Build the Windows controller

Install the .NET 9 SDK, then run the local build script from the repository root:

```powershell
.\scripts\build.ps1
```

This creates and smoke-tests the self-contained Windows Native AOT executable at:

```text
.\artifacts\local\video-optimiser.exe
```

Re-run the build script after changing the application code. The remote Ubuntu server does not receive this executable; it receives only the worker tools installed by the bootstrap script.

### Select remote processing

Add the `processing` block to `video-optimiser.yaml`. Add `sshPath` and `sftpPath` to its existing `tools` block; do not create a second `tools` block:

```yaml
tools:
  abAv1Path: "ab-av1"
  ffmpegPath: "ffmpeg"
  ffprobePath: "ffprobe"
  sshPath: "ssh"
  sftpPath: "sftp"

processing:
  mode: "remoteSsh"
  remoteSsh:
    host: "video-worker"
    workingDirectory: "/var/tmp/video-optimiser"
    minimumCpuCount: 8
    minimumAvailableMemory: "14GiB"
    minimumFreeDiskMultiplier: 2.5
```

The SSH key remains in the Windows OpenSSH configuration. Do not put credentials or private-key contents in YAML.

Check the local controller and remote worker before uploading a video:

```powershell
.\artifacts\local\video-optimiser.exe doctor --config .\video-optimiser.yaml
```

## 7. Process and retrieve the queue

Processing is sequential and reuses the same server:

```powershell
.\artifacts\local\video-optimiser.exe queue discover --config .\video-optimiser.yaml
.\artifacts\local\video-optimiser.exe queue run --config .\video-optimiser.yaml
.\artifacts\local\video-optimiser.exe status --config .\video-optimiser.yaml
```

The first discovery probes eligible candidates and stores their metadata locally. Later discovery runs reuse that metadata for unchanged files while still applying the current YAML eligibility rules.

For an initial test with an empty queue, discover only one eligible video instead of every candidate:

```powershell
.\artifacts\local\video-optimiser.exe queue discover --first --config .\video-optimiser.yaml
```

The `--first` option limits only newly discovered videos. It does not remove or limit jobs that are already queued.

Before deleting the server, confirm every expected job is `ReadyToFinalize`. This means its output has been downloaded, checksum-verified, and validated locally. An interrupted job may require another `queue run` to reattach and retrieve its output.

## 8. Delete the server, then finalize locally

In Hetzner, verify that the Primary IP is set to auto-delete with the server, or delete it separately afterward. Also remove any independently created volumes, snapshots, or floating IPs.

Delete the server after every expected result is safely `ReadyToFinalize`. Powering it off does not stop billing.

Finalization remains local and may be performed after server deletion:

```powershell
.\artifacts\local\video-optimiser.exe finalize --ready --config .\video-optimiser.yaml
```

The original videos remain untouched until finalization succeeds.
