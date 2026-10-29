# Remote SSH processing

In remote SSH mode, VideoOptimiser and its job database stay on Windows while a Linux worker runs the CRF search and the AV1 encode. Validation, review and finalisation stay on this PC. The recommended Hetzner lifecycle creates a worker when a video first needs one, using the first server type on your list that Hetzner has in stock, once you approve its hourly price. When the run ends it deletes the worker if `deleteAfterRun` is true, which is the default.

Setup is one-time: create a Hetzner API token and run `remote setup`. Do **not** create a server in the Hetzner console; VideoOptimiser creates the worker through the API.

The commands below assume you run them in the folder that holds `video-optimiser.exe`, as in the README's [Quick start](../README.md#quick-start). If you put the exe on `PATH`, drop the `.\`.

## 1. What runs on the worker

The release zip holds `scripts/bootstrap-remote-worker.sh` beside the exe. The exe itself never goes to the worker: when VideoOptimiser creates one, cloud-init runs that script there.

The bootstrap installs this pinned stack under `/opt/video-optimiser`:

- `ab-av1` 0.11.7
- FFmpeg 8.0
- SVT-AV1 3.1.0
- libvmaf 3.0.0
- libdav1d AV1 decoding support

It creates the password-disabled, non-sudo `videoopt` SSH user and the `/var/tmp/video-optimiser` workspace.

## 2. Run remote setup

```powershell
.\video-optimiser.exe remote setup --config .\video-optimiser.yaml
```

`remote setup` creates no worker and costs nothing. It sets up managed Hetzner workers only: with `lifecycle: "manual"` and a `host` set, it stops with exit `3` and points you to [Manual SSH lifecycle](#manual-ssh-lifecycle). If it cannot reach the Hetzner API or run `ssh-keygen`, it exits `4`. In order, it:

1. **Checks the Hetzner API token.** With none, it prints how to create one and exits `3`. In the Hetzner Cloud project, open **Security > API Tokens** and create a token with **Read & Write** permission; Hetzner shows it only once. Then create the ignored token file beside your config file (`apiTokenFile` sets another path, relative to the config):

   ```powershell
   Copy-Item .\.env.example .\.env
   notepad .\.env
   ```

   Set `VIDEO_OPTIMISER_HETZNER_TOKEN=<paste-token>` and run `remote setup` again. A token Hetzner rejects also exits `3`.
2. **Finds or creates the SSH key.** It uses `processing.remoteSsh.identityFile` when that file exists. Otherwise it uses `%USERPROFILE%\.ssh\video-optimiser-hetzner-ed25519`, creating it with `ssh-keygen` if it is missing. A new key has no passphrase, because VideoOptimiser runs ssh without a prompt. It never overwrites a key file.
3. **Uploads the public key to the Hetzner project**, unless the project already has that key under any name; then it reuses that name. A new upload is named `video-optimiser-hetzner`, or `video-optimiser-hetzner-2` and so on when the name is taken. This is the only change it makes in Hetzner. A read-only token exits `3`.
4. **Shows the `processing:` section it will write** and asks `Write this to <config>? A backup goes to <config>.bak. [y/N]`. The section keeps your own values, sets `mode: "remoteSsh"` and `lifecycle: "hetzner"`, and fills in `identityFile` and `sshKeyName`. On `y`, it saves the old file as `<config>.bak`, replaces only the `processing:` section, and validates the result. If the edit itself makes the file invalid, it restores the backup and exits `3`. If the file already had other problems, such as a missing `watch.roots`, it keeps the edit, lists every remaining problem on standard error, prints no completion line or next command, and exits `3`; fix them, then run `config validate`. Comments inside the old `processing:` section are replaced; everything outside it stays as written. Any other answer, or no input, changes nothing, so you can paste the section yourself.
5. **Prints the next command**: `Next: run 'video-optimiser immich run --first'.` when Immich is configured, otherwise `Next: run 'video-optimiser run'.` The edit has already set `mode: "remoteSsh"`, so neither needs `--remote`. The line repeats `--config` only if you typed it.

Running it again is safe: it finds the same key file and the same Hetzner entry.

`.gitignore` already excludes `.env`, and VideoOptimiser loads it by itself. The token is never copied into YAML, the job database, recovery state, or command output. Do not commit or share `.env`. For CI or a temporary override, an existing `VIDEO_OPTIMISER_HETZNER_TOKEN` environment variable takes precedence over the file.

VideoOptimiser sends the key's Hetzner name in each create request. Hetzner installs the public key for `root`; the cloud-init bootstrap copies it to the restricted `videoopt` account, and Windows connects as `videoopt` with the private key. Deleting a worker does not delete the project's SSH key, so later workers reuse it.

The image must be Ubuntu 24.04 x86-64, or the ID of a snapshot of a worker (see [Starting workers from a snapshot](#starting-workers-from-a-snapshot)); the bootstrap refuses Ubuntu 26.04. VideoOptimiser gives the worker a public IPv4 address and no IPv6, marks the IPv4 address for automatic deletion, and labels the worker as its own; it reuses or deletes only a worker with those labels.

### How the worker is chosen

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
Create the worker? [y/N]
```

- The first line is the first type and location left after the checks above.
- The second line is the most expensive one left. It appears only when that one costs more than the first.
- Hetzner's availability is only a hint, so a create request can still fail with HTTP 412 (`resource_unavailable` or `placement_unavailable`). The next type and location is then tried without asking again: one `y` approves every candidate up to the second line's price.
- Any answer other than `y` or `yes`, or no input at all, creates nothing. The command prints `No worker was created.`, exits `0`, and leaves the jobs queued.
- A script approves by piping the answer: `'y' | .\video-optimiser.exe run --config .\video-optimiser.yaml`.
- A worker that's already running is reused without asking; it is already billing.

Before each create request it prints the type, location and price, such as `Creating Hetzner cpx42 in hel1 (EUR 0.134/h incl. VAT).` If nothing can be created, the error lists every type, the locations tried for it, and why each failed. No worker is left running.

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

### Starting workers from a snapshot

A new worker builds libvmaf, SVT-AV1 and FFmpeg from source before it's ready, and Hetzner bills for that time. A worker created from a Hetzner snapshot of an earlier worker already has these tools: the bootstrap finds each one at its pinned version and skips the build. It still installs packages, sets up the `videoopt` account and SSH, and checks the codecs.

To make the snapshot:

1. Set `serverTypes` to the type in your list with the smallest disk, then create a worker and wait for `Worker ready`:

   ```powershell
   .\video-optimiser.exe worker create --config .\video-optimiser.yaml
   ```

   Put your full `serverTypes` list back afterwards.
2. In the Hetzner Console, open the worker, shut it down under **Power**, then take a snapshot under **Snapshots**.
3. Delete the worker:

   ```powershell
   .\video-optimiser.exe worker delete --config .\video-optimiser.yaml
   ```

4. Look up the snapshot's ID with your Hetzner API token. The command also prints two sizes in GB: `disk_size` is the smallest server disk the snapshot fits on, and `image_size` is the size Hetzner bills for:

   ```powershell
   (Invoke-RestMethod 'https://api.hetzner.cloud/v1/images?type=snapshot' -Headers @{ Authorization = 'Bearer <paste-token>' }).images | Select-Object id, description, disk_size, image_size
   ```

5. Set `image` to that ID, in quotes:

   ```yaml
   processing:
     remoteSsh:
       hetzner:
         image: "123456789"
   ```

Hetzner can't create a server from a snapshot on a type whose disk is smaller than the snapshot's. That's why step 1 uses the smallest disk in your list: a snapshot made on a larger disk makes every create on a smaller type fail. A type with a larger disk works. Hetzner's price list shows each type's disk.

If that type is unavailable in every location, `worker create` says so. Make the snapshot on any available x86 type whose disk is no larger, such as `cpx32` in place of `cx43`, both 160 GB. A smaller type usually has fewer cores or less memory than `minimumCpuCount` and `minimumAvailableMemory` allow, so lower them to that type's figures for step 1 and put them back afterwards. The build takes longer on fewer cores, but a worker made from the snapshot runs on whichever type `serverTypes` picks.

A snapshot works in any location in its network zone. hel1, fsn1 and nbg1 are all in the `eu-central` zone, so with the default `locations` one snapshot serves every worker.

Hetzner charges for snapshots by the GB of `image_size` each month, and a worker's snapshot is a few GB. See [hetzner.com/cloud](https://www.hetzner.com/cloud/) for the price.

When an update changes the version of one of the tools, a worker from the old snapshot still works, but it builds that tool again while it bootstraps, and the bootstrap shows that tool's stage again, such as `Bootstrapping: 60%, building FFmpeg.` Make a new snapshot then, and delete the old one in the Hetzner Console.

<details>
<summary>Doing it by hand</summary>

`remote setup` does these steps for you; do them by hand only if you prefer.

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

6. Validate the settings without creating a worker:

   ```powershell
   .\video-optimiser.exe config validate --config .\video-optimiser.yaml
   ```

</details>

## 3. Optional preflight

A run creates the worker once you approve its price. To create it early and wait for bootstrapping to finish:

```powershell
.\video-optimiser.exe worker create --config .\video-optimiser.yaml
.\video-optimiser.exe doctor --config .\video-optimiser.yaml
.\video-optimiser.exe worker status --config .\video-optimiser.yaml
```

`doctor` checks the worker's tools, resources and workspace only while it is running. Without one, it checks the Hetzner token and API access and warns that the host checks were skipped. It follows `lifecycle` alone, like the `worker` commands: with `lifecycle: "hetzner"` and `mode: "local"` it still checks the token and API access, and warns (`ManagedWorkerBilling`) when a worker or its IPv4 address is left billing. It takes no lock, so it answers while a run is creating the worker.

VideoOptimiser generates each new worker's SSH host key on this PC and sends it in the request that creates the server. Before the first connection, it writes the public half to a known-hosts file kept for that worker alone, so every connection, the first one too, must find that exact key. A worker created by an earlier version keeps the key it recorded when it first connected. VideoOptimiser does not change your global OpenSSH configuration. The bootstrap script also checks what it downloads. The FFmpeg source and ab-av1 must match a SHA-256 pinned beside each version, and libvmaf and SVT-AV1 are fetched by a pinned commit. If a download doesn't match, the bootstrap stops with an error, which the run reports.

While a new worker bootstraps, VideoOptimiser prints a percentage at each stage: installing dependencies, libvmaf, SVT-AV1, FFmpeg, ab-av1, SSH configuration and the final check. A stage that runs long prints the time spent in it every minute. Each SSH readiness check has its own time limit, so a stalled OpenSSH session is ended and tried again instead of freezing the command. Every other SSH and SFTP connection sends a keepalive after 15 seconds with nothing received, so one to a worker that stops answering ends within about a minute instead of hanging. An SSH command that gets no answer, even over a live connection, is ended after 30 seconds and sent again, then given 60 and 90 seconds; after the third try the worker counts as unreachable.

`worker create` leaves the worker running. If no run will use it, delete it:

```powershell
.\video-optimiser.exe worker delete --config .\video-optimiser.yaml
```

## 4. Process videos

For the watch folders:

```powershell
.\video-optimiser.exe queue add --config .\video-optimiser.yaml
.\video-optimiser.exe run --config .\video-optimiser.yaml
.\video-optimiser.exe queue list --config .\video-optimiser.yaml
```

For one video, queue it, then run:

```powershell
.\video-optimiser.exe queue add "D:\Videos\clip.mkv" --config .\video-optimiser.yaml
.\video-optimiser.exe run --config .\video-optimiser.yaml
```

`run` converts everything in the queue. `queue list` shows the order, `queue next <job-id>` puts a video first, and `queue hold <job-id>` keeps one out until `queue release <job-id>`.

For an initial test with an empty queue, queue only one eligible video:

```powershell
.\video-optimiser.exe queue add --first --config .\video-optimiser.yaml
```

`--first` limits only newly found videos; it does not remove existing queued jobs. An empty `run` does not create a worker. With work to do and no worker running, `run` shows the price and asks `Create the worker? [y/N]` first; see [How the worker is chosen](#how-the-worker-is-chosen). Before that it prints where it will encode, such as `Encoding on a new Hetzner worker created for this run, billed hourly, trying cx43, cx53, cpx42, ccx33 at up to EUR 0.30/h.`

### How a run ends

When a run finishes, failed videos included, `deleteAfterRun: true` deletes the worker if the run used it, after outputs have been downloaded, checksum-verified, and validated locally, and confirms that the separately billable IPv4 address was also deleted. With `false` the run ends with `The worker <name> stays up, billed hourly, until it is deleted.` and `To delete it: video-optimiser worker delete`. The same ending applies to a run that never used a worker kept from an earlier run, and to a run that ends with an error once the worker exists, unless the worker never got ready (see below). The original videos stay untouched, ready to finalise.

If the delete fails, the run prints `Couldn't delete the worker <name>: <reason>` and `It's still billing hourly.` with the `worker delete` command, and exits `1` (`9` after `Ctrl+C`).

When it creates a worker, the command says bootstrapping takes several minutes and prints a `Bootstrapping: 35%, building SVT-AV1.` line at each stage, with the time spent in a stage if it runs long. SSH refusals while the worker boots are expected and stay silent for the first 3 minutes; after that `Still waiting for the worker's SSH` appears at most once a minute. `Worker ready after 7m 36s.` ends the wait.

If the bootstrap script fails on the worker, the next readiness check sees it within about 15 seconds, and the run stops instead of waiting out `bootstrapTimeout`. The error names the stage and the command that failed, for example `The Hetzner worker video-optimiser-1 failed to bootstrap while building FFmpeg: make -j8 exited with code 2 (line 150 of the bootstrap script).`, followed by `The end of its cloud-init log:` and up to 5 lines from that log. The run exits `6`, as it does when `bootstrapTimeout` runs out.

A worker that never got ready gets its own ending. That covers a worker whose bootstrap failed or ran out `bootstrapTimeout`, and one the run created that Hetzner returned an error for. In a terminal the run asks `Delete the worker now? [y/N]` after the error. `y` deletes it. `N` keeps it and prints that it bills hourly, with the `worker delete` command. With redirected input it is deleted without asking, since nobody is there to look at it. A background run deletes a worker whose bootstrap failed or timed out even if you chose to keep the worker, because no later run could use it. After a Hetzner error it follows the choice you made when you started it.

While an original is uploaded, the command reports the checksum taken on this PC, the worker's capacity check, the upload's bytes and percentage, and the checksum check on the worker. A download reports its bytes, percentage and checksum check the same way, and both resume where they stopped. A step that runs longer than 10 seconds prints a progress line at most every 10 seconds: a transfer gives its percentage, rate and time left, and local hashing gives `Checking the checksum: N% of <size>` under a line that names what is being checked, such as `Checking the original's checksum before upload.`, so an upload or download does not stay silent.

### When the worker stops answering

If the application crashes or the network becomes unreachable, the worker is kept. A bootstrap that fails or times out ends as [How a run ends](#how-a-run-ends) describes. When Hetzner's API takes more than 100 seconds to answer, the command stops with `Hetzner didn't answer <request> within 100 seconds.` and exit code 6. A run that was setting up a worker then ends as failed, and the worker is kept or deleted by the same rules. A lost connection is tried 3 times, and an upload resumes where it stopped. A transfer counts as a lost connection when ssh itself fails (exit 255) or sftp's messages say the connection died (`Connection closed`, `Connection reset`, `Broken pipe`, `Connection timed out`). Any other sftp failure, such as `Permission denied` or `No space left on device`, fails the video at once with sftp's own message.

When the worker can't be reached, the video's line is `Worker unreachable: <reason>.` and the run stops at that video, because every later one would fail the same way. The video is stopped, not failed, and resumes from its stage. The videos after it stay queued. The run then prints `Stopped: the worker <name> can't be reached, so 3 videos were not converted.` For a Hetzner worker it adds `The worker stays up, billed hourly, until it is deleted.`, `To resume: video-optimiser run` and `To delete it instead: video-optimiser worker delete`. A manual host gets only `To resume:`. Run the same command again to reconnect and resume.

### When a stage hangs

A CRF search or encode on the worker writes to its log as it works. When the log stays silent for longer than `processing.remoteSsh.stageSilenceTimeout` (default `30m`), the run stops that stage on the worker, keeps its log beside the original, and fails the video with `The encode on the worker wrote nothing to its log for 47m 00s, so it was stopped.` (or `The CRF search ...`). Then it goes on to the next video. ab-av1 writes less often the longer a step runs, so a long stage gets more time: up to 1.5 times as long as it had been running at its last line. A healthy two-hour encode can go about 34 minutes without a line. The times come from the worker's clock, so a later run that picks up the stage judges it the same way. `queue retry <job-id>` tries the video again.

### Stopping a run with Ctrl+C

The first `Ctrl+C` while a video converts stops the run after that video, so its CRF search, encode and download are kept. It prints `Stopping after this video (the encode has about 7m 36s left). Press Ctrl+C again to stop now.` The time appears only while encoding. When the video is done, the run prints `Stopped after clip.mp4; 3 videos are still waiting.`, its usual summary, and `To convert the rest: video-optimiser run`, and exits `9`.

With a Hetzner worker up, that first press also asks `Delete the worker when the run stops? [y/N]`, so you can answer and leave. `y` deletes the worker once the video is done (`The worker will be deleted when the run stops. Keep this window open until it says it's deleted.`). `N` keeps it up, billed hourly (`The worker will stay up when the run stops.`). The video keeps converting while the question waits, and its lines print after your answer. With redirected input nothing is asked: the worker is kept, and the lines name `worker delete`. An unanswered question is asked again as `Delete the worker now? [y/N]` when the run ends.

A second `Ctrl+C` stops at once, and so does a first one when no video is converting (during Immich discovery, while the worker is created, at a prompt). This stops only this PC's side:

- On this PC, or with nothing running on the worker, the run prints `Stopped now. clip.mp4 continues from its last finished step on the next run.` and `To resume: video-optimiser run`.
- A CRF search or encode keeps running on the worker, which keeps billing. The run prints `Stopped watching. The encode of clip.mp4 is still running on the worker <name>.`, `The worker is billed hourly until it is deleted.`, and the commands to pick it up again or to stop it. Then it asks `Delete the worker now? The encode in progress is lost. [y/N]`. A `y` given at the first press is asked again here, because deleting now loses work. An `N` prints `To delete it later: video-optimiser worker delete`.
- Stopped while downloading, the finished encode is waiting on the worker, and deleting the worker loses it too: `Delete the worker now? The finished encode is lost. [y/N]`.
- A press while the worker is being created first waits for Hetzner to answer the create request, so the new worker is recorded on this PC, and then stops with the worker up. If the request gets no answer and Hetzner can't be asked afterwards either, the run prints `The request to create the worker <name> got no answer, ...`, then `If it did, it's billing hourly.` with the `worker status` and `worker delete` commands, and exits `6`.
- A press while the worker is being deleted stops nothing. It prints `Deleting the worker; closing this window now leaves it billing.`

The rerun follows a stage that is still running: it prints `Picked up the encode running on the worker.` (or `Picked up the CRF search running on the worker.`) and shows only what is new, not the log the earlier run already showed. If the encode finished in the meantime it prints `The encode on the worker had already finished.` and goes straight to the download, without uploading the original again. On a new worker it uploads and encodes again with the CRF it found. If you edit the original while the video is stopped, the next run says `The original changed after this video was stopped, so the conversion starts over.`, stops the old work on the worker and converts the video again from the start. If the original can't be found when the run reaches it, for example because it's on a network or USB drive that is offline, a video whose encode started on the worker stays stopped and keeps that encode: the run prints `Original isn't reachable. The video stays stopped and resumes once it's back.` and moves on. Any other video whose original is missing fails. An upload or download stopped by `Ctrl+C` resumes where it stopped. A small recovery file and a worker-specific known-hosts file are stored beside the configured SQLite database; neither contains credentials.

### Background runs

A run started with `s` in the [full-screen view](tui.md) runs in the background, so closing the terminal doesn't stop it. Before it starts, it asks which worker converts (the one that is up, a new server from a list with hourly prices, or this PC) and, unless you picked this PC, whether to delete or keep the worker when the run ends. A new server is created before the run starts. Nothing is asked once it runs: change the worker's fate with `w` in the Activity pane, and stop the run with `x` or `X` there, or with `run stop` or `run stop --now`. A stop never cuts off the worker's delete.

### Kept workers

Inspect or delete a kept worker with:

```powershell
.\video-optimiser.exe worker status --config .\video-optimiser.yaml
.\video-optimiser.exe worker delete --config .\video-optimiser.yaml
```

The delete command verifies the exact server ID and ownership labels. It refuses ambiguous or unowned resources. When the worker is already gone from Hetzner, it says `The worker <name> is already gone from Hetzner; its record on this PC is removed.` and still deletes a leftover IPv4 address and the record.

`worker status` takes no lock, so it answers while a run is creating the worker. It prints the worker's line, or `No Hetzner worker exists.`; when Hetzner can't be asked (no API token, no answer within 15 seconds) it prints the worker recorded on this PC, or `No Hetzner worker is recorded on this PC.`, and then `Hetzner wasn't checked: <reason>.`. `worker create` and `worker delete` need the lock: while another command holds it, such as a run that is setting up a worker, they print `Waiting for another video-optimiser command that is using the worker; Ctrl+C stops waiting.` and keep waiting. `Ctrl+C` in `worker create` after the worker exists stops the command with exit code `9`, prints `The worker <name> stays up, billed hourly, until it is deleted.` and `To delete it: video-optimiser worker delete`, and leaves the worker running. A press before the create request is sent creates nothing. A press while Hetzner answers the request waits for the answer, so the worker exists and stays up.

`status`, and a bare `video-optimiser` whose output is redirected, print a card that shows a billing worker too, with its type, location, age and hourly price, checked against Hetzner. It takes no lock, so it answers while a run is creating the worker. When Hetzner can't be asked (no API token, no answer within 15 seconds), it shows the worker recorded on this PC and says why it wasn't checked. The card says `Worker: none.` when Hetzner has no worker for this database, and `Worker: <name> is gone from Hetzner, but its IPv4 address still bills.` when only the IPv4 address is left; `worker delete` removes it. The card also lists the next commands worth running, and `worker delete` is one of them when a worker bills and no run is going.

`cost` shows what the space freed on Hetzner workers cost, in EUR per TB freed, for all time and for each run, and `$` in the full-screen view shows the same. See [commands.md](commands.md#workers-and-what-they-cost).

The `worker` commands follow `lifecycle` alone, so they still work after you set `mode` back to `"local"`, and they take no `--local` or `--remote`. With any other `lifecycle`, `worker create` and `worker delete` exit `3` and `worker status` exits `0`; each names the setting and its current value.

## Failed videos and kept workers

A run creates the managed worker only once a job will use it. Each job is first checked on this PC (the original exists, sits under a watch root, passes the eligibility rules, can save `savings.minimumBytesSaved`, and isn't skipped for its audio); a run in which every job fails those checks, or is already ready to finalise, creates no server and prints nothing about one. A file still being written, or one ffprobe cannot read yet, counts as needing the server, since it may be fine by the time its job runs. A video stopped while its AV1 was being checked, with the AV1 still on this PC, is checked here and needs no server, unless its original changed since. Before it creates a managed worker, `run` checks that each queued or stopped job matches the current processing mode, worker and workspace. A job that never started takes the current settings. A job that started under other settings fails before any worker is created. Under `--local` or `--remote`, which set the mode for one run, a job started in the other mode is left for a later run instead and printed as `<file> is left for a later run: it started on ...`, with the command that resumes it: a plain `run` when the config matches where the video started, `run --local` or `run --remote` when the config uses the other mode, and when the config points at another worker (or the same worker under another `workingDirectory`), a line to set `processing.remoteSsh` back or to `queue drop` the video.

A video that joins a run after it started (released with `queue release`, moved up with `queue next`, or queued from another terminal) is checked the same way when its turn comes. If it needs the worker and the run created none, it waits for the next run: `<file> waits for the next run: it needs a worker, and this run has none.`

`queue drop` does not reach the worker. Dropping a video whose CRF search or encode was stopped there says `Its encode may still be running on the worker; the next run on that worker stops it.` (or `Its CRF search ...`), for a managed and a manual worker alike. The first time a run reaches a worker, it stops the stage processes and removes the workspaces under `workingDirectory` of every job this database holds as ended (completed, failed, dropped, rejected or not worth converting), so a dropped video's stage ends and frees its disk. This sweep never touches the workspace of a video that is still open, a stopped one included, because the next run resumes it. A workspace of a job this database does not know is left alone too, since a manual worker can be shared by two databases.

When `deleteAfterRun` is enabled, a run that finishes deletes the worker, failed videos included: a `queue retry` is a new job that starts again from the upload, so nothing a failed video left on the worker is reused. A run you stop asks instead, and with redirected input it keeps the worker and prints the `worker delete` command.

A video that needs more memory than the worker has, usually a 4K one, fails with `The worker ran out of memory.` Retrying it on the same worker fails the same way. Retry it and convert it on this PC with `run --local`, or put a server type with more memory first in `serverTypes` before the next run, deleting the worker first if it's still up, since a running worker keeps its type. A manual worker needs a host with more memory.

To try a failed video again, for example after its remote configuration changed, queue it as a new job. The failed job stays in `history`, which lists its ID, and the new job does not reuse its previous remote output or workspace:

```powershell
.\video-optimiser.exe queue retry <job-id> --config .\video-optimiser.yaml
```

## 5. Finalise locally

Finalisation remains local and can happen after the worker has been deleted:

```powershell
.\video-optimiser.exe finalize --ready --config .\video-optimiser.yaml
```

## Manual SSH lifecycle

To use a Linux server you manage yourself, use `lifecycle: "manual"` and an OpenSSH config alias:

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

Bootstrap it as root with `scripts/bootstrap-remote-worker.sh`, then change the alias to `User videoopt`. In manual mode VideoOptimiser never creates or deletes the worker. Delete the server and any leftover IPv4 address in Hetzner after all expected results are safely local; powering a server off does not stop billing.
