# Remote SSH processing

Remote SSH mode keeps VideoOptimiser and its job database on Windows while a Linux worker performs CRF search and AV1 encoding. Validation, review, and finalisation remain local. The recommended Hetzner lifecycle creates a server when work starts, using the first type on your list that Hetzner has in stock, once you approve its hourly price, and deletes it after every required output is safely downloaded.

The managed workflow has only two one-time manual prerequisites: store the dedicated SSH public key in the Hetzner project and create an API token. Do **not** create a server in the Hetzner console; VideoOptimiser creates it through the API.

## 1. Confirm the dedicated SSH key

This is a one-time setup per Hetzner project, not something repeated for each worker. If the project already shows an SSH key named `video-optimiser-hetzner-cx43` and the matching private key still exists at `%USERPROFILE%\.ssh\video-optimiser-hetzner-cx43-ed25519`, skip to step 2.

To check Hetzner, open the project that the API token will control, then go to **Security > SSH Keys**. The key must be in this same project because the creation request refers to it by the configured `sshKeyName`.

Only if the key is missing, generate it in PowerShell:

```powershell
ssh-keygen -t ed25519 -a 100 -f "$env:USERPROFILE\.ssh\video-optimiser-hetzner-cx43-ed25519" -C "video-optimiser-hetzner-cx43"
```

For the simplest non-interactive worker, press Enter twice when asked for a passphrase. If the key has a passphrase, load it into the Windows OpenSSH agent before processing.

Copy only the public key:

```powershell
Get-Content "$env:USERPROFILE\.ssh\video-optimiser-hetzner-cx43-ed25519.pub" | Set-Clipboard
```

In **Security > SSH Keys**, select **Add SSH Key**, paste the public key, and name it `video-optimiser-hetzner-cx43`. Leave **Set as default key** unchecked. Never upload or share the file without the `.pub` extension; that is the private key.

VideoOptimiser sends this project key's name in each server-creation request. Hetzner installs the public key for `root`; the cloud-init bootstrap copies it to the restricted `videoopt` account. Windows then connects as `videoopt` with the matching private key. Deleting a server does not delete the project-level SSH key, so later workers reuse it automatically.

## 2. Create a Hetzner API token

Open the same Hetzner Cloud project, go to **Security > API Tokens**, and create a token with **Read & Write** permission. Copy it immediately; Hetzner only shows it once.

Create the ignored local token file once from the repository root:

```powershell
Copy-Item .\.env.example .\.env
notepad .\.env
```

Replace the placeholder so the file contains:

```dotenv
VIDEO_OPTIMISER_HETZNER_TOKEN=<paste-token>
```

`.env` is already excluded by `.gitignore`. The controller loads it automatically from the path configured below, so nothing must be entered again after opening a new terminal. The token is never copied into YAML, the job database, recovery state, or command output. Do not commit or share `.env`.

For CI or a temporary override, an existing `VIDEO_OPTIMISER_HETZNER_TOKEN` environment variable takes precedence over the file.

## 3. Build the Windows controller

Skip this step when you use the release zip: it already holds the executable and `scripts/bootstrap-remote-worker.sh`. From a source checkout, install the .NET 10 SDK, then run this from the repository root:

```powershell
.\scripts\build.ps1
```

This creates and smoke-tests the Windows Native AOT executable at `artifacts\local\video-optimiser.exe`. The Windows executable is not copied to the worker; cloud-init runs `scripts/bootstrap-remote-worker.sh` there instead.

The bootstrap installs this pinned stack under `/opt/video-optimiser`:

- `ab-av1` 0.11.7
- FFmpeg 8.0
- SVT-AV1 3.1.0
- libvmaf 3.0.0
- libdav1d AV1 decoding support

It creates the password-disabled, non-sudo `videoopt` SSH user and the `/var/tmp/video-optimiser` workspace.

## 4. Configure managed Hetzner processing

Add the following settings to the existing YAML blocks. Paths are relative to the YAML file unless absolute.

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
    lifecycle: "hetzner"
    identityFile: "C:\\Users\\YOUR_USER\\.ssh\\video-optimiser-hetzner-cx43-ed25519"
    workingDirectory: "/var/tmp/video-optimiser"
    minimumCpuCount: 8
    minimumAvailableMemory: "14GiB"
    minimumFreeDiskMultiplier: 2.5
    hetzner:
      apiTokenEnvironmentVariable: "VIDEO_OPTIMISER_HETZNER_TOKEN"
      apiTokenFile: ".env"
      # Tried in this order, each in every location below.
      serverTypes: ["cx43", "cx53", "cpx42", "ccx33"]
      # EUR per hour including VAT. null removes the cap.
      maxHourlyPrice: 0.30
      image: "ubuntu-24.04"
      locations: ["hel1", "fsn1", "nbg1"]
      sshKeyName: "video-optimiser-hetzner-cx43"
      serverNamePrefix: "video-optimiser"
      bootstrapScriptPath: "scripts/bootstrap-remote-worker.sh"
      bootstrapTimeout: "90m"
      deleteAfterRun: true
```

Use the real path to the private key. The image must be Ubuntu 24.04 x86-64; the bootstrap intentionally rejects Ubuntu 26.04. The controller enables public IPv4, disables IPv6, marks the Primary IPv4 for automatic deletion, and applies ownership labels before it will reuse or delete a server.

### How the server is chosen

VideoOptimiser tries each server type in `serverTypes` order, and each type in every location in `locations` order, before moving to the next type. With the defaults that is `cx43` in hel1, fsn1 and nbg1, then `cx53` in the same three, and so on.

Before creating anything, it reads Hetzner's server type list once and skips any type and location where:

- Hetzner reports the type as unavailable (sold out) there, or doesn't sell it there;
- the type has fewer cores than `minimumCpuCount` or less memory than `minimumAvailableMemory`, because the worker check would reject it after billing starts;
- the type isn't x86 (the bootstrap installs x86 builds), or Hetzner has deprecated it;
- its price there, including VAT, is above `maxHourlyPrice`.

It prints each skipped type with the reason, then asks once before creating anything:

```text
Will create cpx42 in hel1 at EUR 0.134/h incl. VAT.
If that placement fails, a fallback may cost up to EUR 0.266/h (ccx33 in hel1).
Create the server? [y/N]
```

- **The first line** is the first type and location left after the checks above.
- **The second line** is the most expensive one left. It appears only when that one costs more than the first.
- **Fallbacks:** availability is only an indicator, so a create request can still fail with HTTP 412 (`resource_unavailable` or `placement_unavailable`). The next type and location is then tried without asking again: one `y` approves every candidate up to the second line's price.
- **Declining:** any answer other than `y` or `yes`, or no input at all, creates nothing. The command prints `No server was created.`, exits `0`, and leaves the jobs queued.
- **Scripts** approve by piping the answer: `'y' | .\artifacts\local\video-optimiser.exe queue run --config .\video-optimiser.yaml`.
- **A server that's already running** is reused without asking; it is already billing.

Before each create request it prints the type, location and price, such as `Creating Hetzner cpx42 in hel1 (EUR 0.134/h incl. VAT).` If nothing can be created, the error lists every type, the locations tried for it, and why each failed. No server is left running.

Gross hourly prices in the EU on 2026-10-12, for choosing your list:

| Type | Cores | Memory | €/h incl. VAT |
| --- | --- | --- | --- |
| `cx43` | 8 shared | 16 GB | 0.031 |
| `cx53` | 16 shared | 32 GB | 0.057 |
| `cpx42` | 8 shared (AMD) | 16 GB | 0.134 |
| `cpx62` | 16 shared (AMD) | 32 GB | 0.250 |
| `ccx33` | 8 dedicated | 32 GB | 0.266 |

CX types sell out often, and Hetzner sells them only in fsn1, nbg1 and hel1. Prices change, so check the Hetzner Console. Locations outside Europe (ash, hil, sin) mostly cost more, and every original and output crosses that distance, so keep EU locations first. Hetzner's `recommended` flag never reorders your list.

The worker's type and location show in `worker status`.

Validate the local settings without creating a server:

```powershell
.\artifacts\local\video-optimiser.exe config validate --config .\video-optimiser.yaml
```

## 5. Optional preflight

Normal processing creates the worker once you approve its price. To create it early and wait for the full toolchain build:

```powershell
.\artifacts\local\video-optimiser.exe worker create --config .\video-optimiser.yaml
.\artifacts\local\video-optimiser.exe doctor --config .\video-optimiser.yaml
.\artifacts\local\video-optimiser.exe worker status --config .\video-optimiser.yaml
```

`doctor` checks the worker's tools, resources and workspace only while it is running. Without one, it checks the Hetzner token and API access and warns that the host checks were skipped.

The first SSH connection uses a dedicated known-hosts file and OpenSSH's `accept-new` behavior. Every later connection requires that exact recorded host key. VideoOptimiser does not modify the user's global OpenSSH configuration.

During the first build, the controller prints phase-based setup percentages for dependency installation, libvmaf, SVT-AV1, FFmpeg, ab-av1, SSH configuration, and final verification. Long unchanged phases emit an elapsed-time heartbeat every minute. Each SSH readiness check is independently bounded, so a stalled OpenSSH session is terminated and retried instead of leaving the command frozen indefinitely.

`worker create` deliberately leaves the worker running. Delete it explicitly if no processing command will use it:

```powershell
.\artifacts\local\video-optimiser.exe worker delete --config .\video-optimiser.yaml
```

## 6. Process videos

For one video:

```powershell
.\artifacts\local\video-optimiser.exe process "F:\Music\MVs\example.mkv" --config .\video-optimiser.yaml
```

For the queue:

```powershell
.\artifacts\local\video-optimiser.exe queue discover --config .\video-optimiser.yaml
.\artifacts\local\video-optimiser.exe queue run --config .\video-optimiser.yaml
.\artifacts\local\video-optimiser.exe status --config .\video-optimiser.yaml
```

For an initial test with an empty queue, discover only one eligible video:

```powershell
.\artifacts\local\video-optimiser.exe queue discover --first --config .\video-optimiser.yaml
```

`--first` limits only newly discovered videos; it does not remove existing queued jobs. An empty `queue run` does not create a server. With work to do and no server running, `process` and `queue run` show the price and ask `Create the server? [y/N]` first; see [How the server is chosen](#how-the-server-is-chosen).

With `deleteAfterRun: true`, a clean `process` or `queue run` deletes the server after outputs have been downloaded, checksum-verified, and validated locally. It then confirms that the separately billable Primary IPv4 was also deleted. The original video remains untouched in `ReadyToFinalize` state.

While an original is staged, the command reports local hashing, remote capacity checks, resumable upload bytes/percentage, and checksum verification. Downloads report the same resumable bytes/percentage and verification phases. Slow transfers print an elapsed-time heartbeat every 30 seconds, so `Staging` or `Downloading` does not remain silent.

If the application crashes, the network becomes unreachable, bootstrap times out, or a job is marked `Interrupted`, the worker is retained. Run the same command again to reconnect, resume the remote stage or transfer, and delete the worker after the clean completion. A small recovery file and a worker-specific known-hosts file are stored beside the configured SQLite database; neither contains credentials.

Inspect or remove a retained worker with:

```powershell
.\artifacts\local\video-optimiser.exe worker status --config .\video-optimiser.yaml
.\artifacts\local\video-optimiser.exe worker delete --config .\video-optimiser.yaml
```

The delete command verifies the exact server ID and ownership labels. It refuses ambiguous or unowned resources.

## Failed jobs and retained workers

Before creating a managed worker, `queue run` checks each queued or resumable job against the current processing mode, remote identity, and deterministic workspace. A never-started queued job adopts the current configuration. A partially processed or interrupted job with a different configuration fails before provisioning.

When `deleteAfterRun` is enabled, the worker is deleted only if every attempted job completes cleanly. Failures and interruptions retain the worker. Failed interactive runs offer an explicit deletion prompt. Redirected or unavailable standard input retains the worker without prompting.

To retry a job that failed because its remote configuration changed, create a new queued job. The failed job stays in history, and the new job does not reuse its previous remote output or workspace:

```powershell
.\artifacts\local\video-optimiser.exe queue retry <job-id> --config .\video-optimiser.yaml
```

## 7. Finalise locally

Finalisation remains local and can happen after the server has been deleted:

```powershell
.\artifacts\local\video-optimiser.exe finalize --ready --config .\video-optimiser.yaml
```

The original videos remain untouched until finalisation succeeds.

## Manual SSH lifecycle

To keep managing a pre-existing worker yourself, use `lifecycle: "manual"` and an OpenSSH config alias:

```yaml
processing:
  mode: "remoteSsh"
  remoteSsh:
    lifecycle: "manual"
    host: "video-worker"
    workingDirectory: "/var/tmp/video-optimiser"
    minimumCpuCount: 8
    minimumAvailableMemory: "14GiB"
    minimumFreeDiskMultiplier: 2.5
```

Bootstrap it as root with `scripts/bootstrap-remote-worker.sh`, then change the alias to `User videoopt`. In manual mode VideoOptimiser never creates or deletes the server. Delete the server and any retained Primary IP in Hetzner after all expected results are safely local; powering a server off does not stop billing.
