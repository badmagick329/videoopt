# Remote SSH processing

Remote SSH mode keeps VideoOptimiser and its job database on Windows while a Linux worker performs CRF search and AV1 encoding. Validation, review, and finalisation remain local. The recommended Hetzner lifecycle creates a server when work starts, using the first type on your list that Hetzner has in stock, once you approve its hourly price, and deletes it after every required output is safely downloaded.

Setup is one-time: build the controller, create a Hetzner API token, and run `remote setup`. Do **not** create a server in the Hetzner console; VideoOptimiser creates it through the API.

## 1. Build the Windows controller

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

## 2. Run remote setup

```powershell
.\artifacts\local\video-optimiser.exe remote setup --config .\video-optimiser.yaml
```

`remote setup` creates no server and costs nothing. It sets up managed Hetzner workers only: with `lifecycle: "manual"` and a `host` set, it stops with exit `3` and points you to [Manual SSH lifecycle](#manual-ssh-lifecycle). If it cannot reach the Hetzner API or run `ssh-keygen`, it exits `4`. In order, it:

1. **Checks the Hetzner API token.** With none, it prints how to create one and exits `3`. In the Hetzner Cloud project, open **Security > API Tokens** and create a token with **Read & Write** permission; Hetzner shows it only once. Then create the ignored token file beside your config file (`apiTokenFile` sets another path, relative to the config):

   ```powershell
   Copy-Item .\.env.example .\.env
   notepad .\.env
   ```

   Set `VIDEO_OPTIMISER_HETZNER_TOKEN=<paste-token>` and run `remote setup` again. A token Hetzner rejects also exits `3`.
2. **Finds or creates the SSH key.** It uses `processing.remoteSsh.identityFile` when that file exists. Otherwise it uses `%USERPROFILE%\.ssh\video-optimiser-hetzner-ed25519`, creating it with `ssh-keygen` if it is missing. A new key has no passphrase, because VideoOptimiser runs ssh without a prompt. It never overwrites a key file.
3. **Uploads the public key to the Hetzner project**, unless the project already has that key under any name; then it reuses that name. A new upload is named `video-optimiser-hetzner`, or `video-optimiser-hetzner-2` and so on when the name is taken. This is the only change it makes in Hetzner. A read-only token exits `3`.
4. **Shows the `processing:` section it will write** and asks `Write this to <config>? A backup goes to <config>.bak. [y/N]`. The section keeps your own values, sets `mode: "remoteSsh"` and `lifecycle: "hetzner"`, and fills in `identityFile` and `sshKeyName`. On `y`, it saves the old file as `<config>.bak`, replaces only the `processing:` section, and validates the result. If the edit itself makes the file invalid, it restores the backup and exits `3`. If the file already had other problems, such as a missing `watch.roots`, it keeps the edit, lists every remaining problem on standard error, prints no completion line or next command, and exits `3`; fix them, then run `config validate`. Comments inside the old `processing:` section are replaced; everything outside it stays as written. Any other answer, or no input, changes nothing, so you can paste the section yourself.
5. **Prints the next command**: `immich run --first` when Immich is configured, otherwise `run`. The edit has already set `mode: "remoteSsh"`, so neither needs `--remote`. The line repeats `--config` only if you typed it.

Running it again is safe: it finds the same key file and the same Hetzner entry.

`.env` is already excluded by `.gitignore`, and the controller loads it automatically. The token is never copied into YAML, the job database, recovery state, or command output. Do not commit or share `.env`. For CI or a temporary override, an existing `VIDEO_OPTIMISER_HETZNER_TOKEN` environment variable takes precedence over the file.

VideoOptimiser sends the key's Hetzner name in each server-creation request. Hetzner installs the public key for `root`; the cloud-init bootstrap copies it to the restricted `videoopt` account, and Windows connects as `videoopt` with the private key. Deleting a server does not delete the project's SSH key, so later workers reuse it.

The image must be Ubuntu 24.04 x86-64; the bootstrap intentionally rejects Ubuntu 26.04. The controller enables public IPv4, disables IPv6, marks the Primary IPv4 for automatic deletion, and applies ownership labels before it will reuse or delete a server.

### How the server is chosen

VideoOptimiser tries each server type in `serverTypes` order, and each type in every location in `locations` order, before moving to the next type. With the defaults that is `cx43` in hel1, fsn1 and nbg1, then `cx53` in the same three, and so on.

Before creating anything, it reads Hetzner's server type list once and skips any type and location where:

- Hetzner doesn't list the type name at all, reports the type as unavailable (sold out) there, or doesn't sell it there;
- the type has fewer cores than `minimumCpuCount` or less memory than `minimumAvailableMemory`, because the worker check would reject it after billing starts;
- the type isn't x86 (the bootstrap installs x86 builds), or Hetzner has deprecated it;
- its price there, including VAT, is above `maxHourlyPrice`, or Hetzner lists no price there while a cap is set.

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
- **Scripts** approve by piping the answer: `'y' | .\artifacts\local\video-optimiser.exe run --config .\video-optimiser.yaml`.
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

<details>
<summary>Doing it by hand</summary>

`remote setup` does these steps for you. Do them by hand only if you want to.

1. Create the API token and `.env` as in the first item of *Run remote setup*.
2. Generate the key in PowerShell, pressing Enter twice when asked for a passphrase:

   ```powershell
   ssh-keygen -t ed25519 -a 100 -f "$env:USERPROFILE\.ssh\video-optimiser-hetzner-ed25519" -C "video-optimiser-hetzner"
   ```

3. Copy only the public key:

   ```powershell
   Get-Content "$env:USERPROFILE\.ssh\video-optimiser-hetzner-ed25519.pub" | Set-Clipboard
   ```

4. In the same Hetzner project, open **Security > SSH Keys**, select **Add SSH Key**, paste the public key, and name it `video-optimiser-hetzner`. Leave **Set as default key** unchecked. Never upload the file without the `.pub` extension; that is the private key.
5. Add these settings to the existing YAML blocks, with the real path to the private key. Paths are relative to the YAML file unless absolute.

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
       identityFile: "C:\\Users\\YOUR_USER\\.ssh\\video-optimiser-hetzner-ed25519"
       # Keep this path for Hetzner: the bootstrap creates only this workspace.
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
         sshKeyName: "video-optimiser-hetzner"
         serverNamePrefix: "video-optimiser"
         bootstrapScriptPath: "scripts/bootstrap-remote-worker.sh"
         bootstrapTimeout: "90m"
         deleteAfterRun: true
   ```

6. Validate the settings without creating a server:

   ```powershell
   .\artifacts\local\video-optimiser.exe config validate --config .\video-optimiser.yaml
   ```

</details>

## 3. Optional preflight

Normal processing creates the worker once you approve its price. To create it early and wait for the full toolchain build:

```powershell
.\artifacts\local\video-optimiser.exe worker create --config .\video-optimiser.yaml
.\artifacts\local\video-optimiser.exe doctor --config .\video-optimiser.yaml
.\artifacts\local\video-optimiser.exe worker status --config .\video-optimiser.yaml
```

`doctor` checks the worker's tools, resources and workspace only while it is running. Without one, it checks the Hetzner token and API access and warns that the host checks were skipped. It follows `lifecycle` alone, like the `worker` commands: with `lifecycle: "hetzner"` and `mode: "local"` it still checks the token and API access, and warns (`ManagedWorkerBilling`) when a worker or its IPv4 address is left billing. It takes no lock, so it answers while a run is creating the worker.

The first SSH connection uses a dedicated known-hosts file and OpenSSH's `accept-new` behavior. Every later connection requires that exact recorded host key. VideoOptimiser does not modify the user's global OpenSSH configuration.

During the first build, the controller prints phase-based setup percentages for dependency installation, libvmaf, SVT-AV1, FFmpeg, ab-av1, SSH configuration, and final verification. Long unchanged phases emit an elapsed-time heartbeat every minute. Each SSH readiness check is independently bounded, so a stalled OpenSSH session is terminated and retried instead of leaving the command frozen indefinitely. Every other SSH and SFTP connection sends a keepalive after 15 seconds with nothing received, so one to a worker that stops answering ends within about a minute instead of hanging.

`worker create` deliberately leaves the worker running. Delete it explicitly if no processing command will use it:

```powershell
.\artifacts\local\video-optimiser.exe worker delete --config .\video-optimiser.yaml
```

## 4. Process videos

For the watch folders:

```powershell
.\artifacts\local\video-optimiser.exe queue add --config .\video-optimiser.yaml
.\artifacts\local\video-optimiser.exe run --config .\video-optimiser.yaml
.\artifacts\local\video-optimiser.exe queue list --config .\video-optimiser.yaml
```

For one video, queue it, then run:

```powershell
.\artifacts\local\video-optimiser.exe queue add "D:\Videos\clip.mkv" --config .\video-optimiser.yaml
```

For an initial test with an empty queue, queue only one eligible video:

```powershell
.\artifacts\local\video-optimiser.exe queue add --first --config .\video-optimiser.yaml
```

`--first` limits only newly found videos; it does not remove existing queued jobs. An empty `run` does not create a server. With work to do and no server running, `run` shows the price and asks `Create the server? [y/N]` first; see [How the server is chosen](#how-the-server-is-chosen).

When a run finishes, failed videos included, `deleteAfterRun: true` deletes the server, after outputs have been downloaded, checksum-verified, and validated locally, and confirms that the separately billable Primary IPv4 was also deleted. With `false` the run ends with `The worker <name> stays up, billed hourly, until it is deleted.` and the `worker delete` command, as does a run that never used a worker kept from an earlier run. The original video remains untouched in `ReadyToFinalize` state.

When it creates a server, the command says setup takes several minutes and prints a `Worker setup: 35%, building SVT-AV1...` line at each stage, with the time spent in a stage if it runs long. SSH refusals while the server boots are expected and stay silent for the first 3 minutes; after that `Still waiting for the worker's SSH` appears at most once a minute. `Worker ready after 7m 36s.` ends the wait. While an original is staged, the command reports local hashing, remote capacity checks, resumable upload bytes/percentage, and checksum verification. Downloads report the same resumable bytes/percentage and verification phases. A step that runs longer than 10 seconds prints a progress line at most every 10 seconds: a transfer gives its percentage, rate and time left, and local hashing gives `Checking the checksum: N% of <size>` under a line that names what is being checked, such as `Checking the original's checksum before upload.`, so `Staging` or `Downloading` does not remain silent.

If the application crashes, the network becomes unreachable, or bootstrap times out, the worker is kept; an error once the worker exists ends with the same two lines. A lost connection is tried 3 times, and an upload resumes where it stopped; then the job is marked `Interrupted` and the run stops there, because every later job would fail the same way. The jobs after it stay queued, and the command prints how many videos were not converted, the command that resumes, and `worker delete` to delete the worker instead. Run the same command again to reconnect and resume.

Press `Ctrl+C` once and the run stops after the video it is converting. With a managed worker up, it asks `Delete the worker when the run stops? [y/N]` straight away, so you can answer and leave: `y` deletes the worker once the video is done (keep the window open until it says so), `N` keeps it up, billed hourly. The video keeps converting while the question waits; its progress lines print after your answer. Press `Ctrl+C` again to stop at once. That stops only this PC's side: a CRF search or encode keeps running on the worker, which keeps billing, and the command prints what is still running and the command that picks it up again, then asks `Delete the worker now?`, naming the work deleting it would lose. Stopped while downloading, the finished encode is waiting on the worker, and deleting the worker loses it too. A `y` given at the first press is asked again when deleting would lose work. With redirected input nothing is asked: the worker is kept, and the lines name `worker delete`. The rerun follows a stage that is still running: it prints `Picked up the encode running on the worker.` (or `Picked up the CRF search running on the worker.`) and shows only what is new, not the log the earlier run already showed. If the encode finished in the meantime it prints `The encode on the worker had already finished.` and goes straight to the download, without uploading the original again. On a new worker it uploads and encodes again with the CRF it found. An upload or download stopped by `Ctrl+C` resumes where it stopped. A small recovery file and a worker-specific known-hosts file are stored beside the configured SQLite database; neither contains credentials.

Inspect or remove a retained worker with:

```powershell
.\artifacts\local\video-optimiser.exe worker status --config .\video-optimiser.yaml
.\artifacts\local\video-optimiser.exe worker delete --config .\video-optimiser.yaml
```

The delete command verifies the exact server ID and ownership labels. It refuses ambiguous or unowned resources. When the server is already gone from Hetzner, it says `The worker <name> is already gone from Hetzner; its record on this PC is removed.` and still deletes a retained Primary IPv4 address and the record.

`worker status` takes no lock, so it answers while a run is creating the worker. It prints the worker's line, or `No managed Hetzner worker exists.`; when Hetzner can't be asked (no API token, no answer within 15 seconds) it prints the worker recorded on this PC, or `No managed Hetzner worker is recorded on this PC.`, and then `Hetzner wasn't checked: <reason>.`. `worker create` and `worker delete` need the lock: while another command holds it, such as a run that is setting up a worker, they print `Waiting for another video-optimiser command that is using the worker; Ctrl+C stops waiting.` and keep waiting. `Ctrl+C` in `worker create` after the server exists stops the command with exit code `9`, prints the two `stays up` lines and leaves the server running; before the server exists it creates nothing.

`status` shows a worker that bills too, with its type, location, age and hourly price, checked against Hetzner. It takes no lock, so it answers while a run is creating the worker. When Hetzner can't be asked (no API token, no answer within 15 seconds), it shows the worker recorded on this PC and says why it wasn't checked. A worker is `Worker: none.` when Hetzner has no server for this database, and `Worker: <name>'s server is gone, but its IPv4 address still bills.` when only the Primary IPv4 is left; `worker delete` removes it.

The `worker` commands follow `lifecycle` alone, so they still work after you set `mode` back to `"local"`, and they take no `--local` or `--remote`. With any other `lifecycle`, `worker create` and `worker delete` exit `3` and `worker status` exits `0`; each names the setting and its current value.

## Failed jobs and retained workers

A run creates the managed worker only once a job will use it. Each job is first checked on this PC (the original exists, sits under a watch root, passes the eligibility rules and can save `savings.minimumBytesSaved`); a run in which every job fails those checks, or is already ready to finalise, creates no server and prints nothing about one. A file still being written, or one ffprobe cannot read yet, counts as needing the server, since it may be fine by the time its job runs. A video stopped while its AV1 was being checked, with the AV1 still on this PC, is checked here and needs no server. Before creating a managed worker, `run` checks each queued or resumable job against the current processing mode, remote identity, and deterministic workspace. A never-started queued job adopts the current configuration. A partially processed or interrupted job with a different configuration fails before provisioning. Under `--local` or `--remote`, which set the mode for one run, a job started in the other mode is skipped instead and printed as `Skipped <file>: started on ...`; the next run without the flag resumes it.

A video that joins a run after it started (released with `queue release`, moved up with `queue next`, or queued from another terminal) is checked the same way when its turn comes. If it needs the worker and the run created none, it waits for the next run: `<file> waits for the next run: it needs a worker, and this run has none.`

`queue drop` does not reach the worker. Dropping a video whose CRF search or encode was stopped there says it `may still be running on the worker`: deleting a managed worker stops it, and on a manual worker it runs until it finishes.

When `deleteAfterRun` is enabled, a run that finishes deletes the worker, failed videos included: a `queue retry` is a new job that starts again from the upload, so nothing a failed video left on the worker is reused. A run you stop asks instead, and with redirected input it keeps the worker and prints the `worker delete` command.

To retry a job that failed because its remote configuration changed, create a new queued job. The failed job stays in history, and the new job does not reuse its previous remote output or workspace:

```powershell
.\artifacts\local\video-optimiser.exe queue retry <job-id> --config .\video-optimiser.yaml
```

## 5. Finalise locally

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
