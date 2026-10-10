# SimWatch Manual

Version 1.5.1 | Torch plugin for Space Engineers dedicated servers

SimWatch watches the server's simulation speed. When the speed drops below a threshold you choose, it runs a list of diagnostic commands for you (by default the Profiler plugin's `!profile ...` commands), collects the output into a report, makes an estimate of the cause, saves the report and can post it to Discord. It can also keep a register of repeat offenders (grids, scripts, players) and post a roleplay-style bulletin about them to a second Discord channel.

---

## Contents

1. [Requirements](#1-requirements)
2. [Building and installing](#2-building-and-installing)
3. [Quick start](#3-quick-start)
4. [How SimWatch works](#4-how-simwatch-works)
5. [Settings reference](#5-settings-reference)
6. [Commands](#6-commands)
7. [Discord setup](#7-discord-setup)
8. [Reading a report](#8-reading-a-report)
9. [Repeat offenders and Galaxy Offenders](#9-repeat-offenders-and-galaxy-offenders)
10. [Running several servers (config sharing)](#10-running-several-servers-config-sharing)
11. [Files and folders](#11-files-and-folders)
12. [Troubleshooting](#12-troubleshooting)
13. [Limitations](#13-limitations)
14. [Upgrading and uninstalling](#14-upgrading-and-uninstalling)

---

## 1. Requirements

| Needed | For |
|---|---|
| Windows server with Torch and a Space Engineers Dedicated Server | running the plugin |
| The **Profiler** plugin, installed and enabled in Torch | the default `!profile ...` diagnostic commands |
| A Discord webhook URL (optional) | posting reports |
| A second Discord webhook URL (optional) | the Galaxy Offenders bulletin |
| The **Concealment** plugin (optional) | the optional `!conceal` step after a diagnostics run |
| .NET SDK 6.0 or newer, on Windows | building the plugin yourself |

SimWatch only works while a game session is running. It starts watching when the world has loaded and stops when the session unloads.

---

## 2. Building and installing

### Build

1. Extract the whole source zip to a normal folder (do not run anything from inside the zip).
2. Double-click `build.bat`.
3. The first time, it asks for your **Torch folder** (the folder containing `Torch.dll`). It finds the `DedicatedServer64` folder inside it. If your game files are somewhere else it asks for that folder too.
4. It checks that the needed DLLs are there, builds, and puts `SimWatch.zip` next to `build.bat`. It remembers your folders in `build-paths.bat`.
5. If the build fails, the window shows the error lines and the full output is in `build-log.txt`.

Manual build instead:

```
dotnet build -c Release -p:TorchDir="C:\path\to\Torch" -p:GameDir="C:\path\to\DedicatedServer64"
```

The result is `bin\Release\net48\SimWatch.zip` (it contains `SimWatch.dll` and `manifest.xml`).

### Install

1. Copy `SimWatch.zip` into Torch's `Plugins` folder (`build.bat` can do this for you).
2. Start Torch, open the **Plugins** tab, tick **SimWatch**, and restart the server.
3. Open **Plugins > SimWatch** in the Torch UI to see the settings page.

On first start SimWatch creates `SimWatch.cfg` in the Torch instance folder with default settings.

---

## 3. Quick start

1. Install and enable the Profiler plugin. On the SimWatch page the line at the top should turn **green** ("Profiler plugin: installed").
2. Leave the defaults, or set the **sim speed threshold** (default 0.9; use 1.0 to react to any drop below full speed).
3. Pick how SimWatch should trigger (section 4.2): the **timer** (default every 30 minutes), the **live monitor**, or both.
4. Optional: paste a Discord webhook URL, tick **Enable**, press **Send test message**.
5. Press **Run diagnostics now** once to see a complete report and make sure all commands answer.
6. Look in `SimWatch-Reports` in the Torch instance folder for the saved report.

---

## 4. How SimWatch works

### 4.1 Measuring the sim speed

SimWatch reads the server's simulation ratio once per second. It averages the readings over a window (default 10 seconds, "Averaging window") and compares the average with the **threshold**. If the average is **below** the threshold, the diagnostics run. If it is at or above, nothing happens.

### 4.2 Triggers

You can use any combination. The master switch **Enable automatic checks** turns the timer and the live monitor on or off together.

| Trigger | When it runs | Notes |
|---|---|---|
| **Timer** | Every N minutes (default 30). At each check the sim speed is averaged; the diagnostics run only if it is below the threshold. | Set the interval to **0** for no timer. |
| **Live monitor** | As soon as the average over the window drops below the threshold. | Off by default. After a run it waits for the **cooldown** (default 30 minutes) before it can run again, even if the speed stays low. It ignores the first 2 minutes after the world loads. |
| **By command** | `!simwatch check`, `test`, `scan`, or the **Run diagnostics now** button. | Always available. |

So with the defaults, diagnostics run at most once per 30 minutes, and only if the speed is low at the moment of the check.

If both the timer (0) and the live monitor are off, SimWatch writes a warning to the log: nothing runs automatically.

### 4.3 The diagnostic run

When triggered, SimWatch runs the **diagnostic commands** one after another on the game thread, like typing them in the Torch console. Default list:

```
!profile frames --secs=15
!profile session --secs=15
!profile entities --secs=15
!profile grids --secs=15 --top=10
!profile blocktypes --secs=15
!profile scripts --secs=15
!profile players --secs=15
!profile physics --tics=600
```

- For `!profile` commands SimWatch waits for the "Finished profiling" line (the **completion marker**). The wait is the profiling time (from `--secs` or `--tics`, or 10 seconds if neither is given) plus the **extra wait per command** (default 30 seconds). Physics profiling counts game ticks, so SimWatch allows for a slow server when computing the wait.
- Other commands are finished when their output goes quiet. If a command prints nothing at all, the report says "no response: command not found, disabled, or it printed nothing".
- **Physics follow-up:** after a `!profile physics` command, if the top cluster (cluster 0) has fewer than 20 entities (setting), SimWatch also runs `!profile physics --inspect=0` to list what is in it.
- **Top results:** each `!profile` ranking is cut to its top 10 entries (setting, 0 = show everything). The report says how many entries were not shown. The `--inspect` output is not cut.
- **Missing Profiler:** if the Profiler plugin is not found, `!profile` commands are skipped and marked "skipped" in the report instead of timing out.
- Commands starting with `!simwatch` are never run as diagnostics.

### 4.4 Optional Concealment step

If the Concealment plugin is installed and **Run a Concealment command after diagnostics** is on, SimWatch runs `!conceal 2000` (the command can be edited) after the diagnostic commands. This only happens when the run was triggered by a low sim speed, not for manual test runs. The command's output appears in the report as its own section. If Concealment is not found the step is skipped and the report says so.

The "sim speed when diagnostics finished" reading is taken after this step, but concealment takes a while to show an effect.

### 4.5 Report and estimated cause

SimWatch builds the report (section 8), including an **estimated cause** block, saves it to `SimWatch-Reports`, writes it to the Torch log, and posts it to Discord if enabled.

### 4.6 Posting only when the speed is still low

By default an automatic alert report is posted to Discord only if the sim speed at the end of the diagnostics is **below 1.00**. If it has recovered to 1.00 the report is still saved and logged, but nothing is posted and no role is pinged. A reading of 0.995 or higher counts as 1.00 (the report prints it as 1.00). Manual scans always post. The behaviour can be switched off with "Don't post alert reports when the sim speed is back at 1.00". The alert still counts for the repeat offender flag and the Galaxy Offenders register.

### 4.7 Cleanup

Saved reports older than **2 days** are deleted automatically (setting "Delete reports older than", 0 = keep). Cleanup runs once an hour and after each new report. The "Keep this many reports" limit (default 50) also applies. The history files described in section 9 are not touched by this cleanup.

---

## 5. Settings reference

All settings are on the SimWatch page in the Torch UI and are saved to `SimWatch.cfg` as soon as you leave a field. You can also edit `SimWatch.cfg` by hand and use **Reload from file** or `!simwatch reload`.

### Sim speed check

| Setting | Default | Range | Meaning |
|---|---|---|---|
| Enable automatic checks (`Enabled`) | on | | Master switch for the timer and the live monitor. |
| Run diagnostics below sim speed (`SimSpeedThreshold`) | 0.9 | 0.01 to 2 | Diagnostics run when the averaged sim speed is below this. 1.0 = full speed. |
| Average sim speed over (seconds) (`SampleSeconds`) | 10 | 1 to 300 | Length of the averaging window. |
| Timer: check every (minutes) (`CheckIntervalMinutes`) | 30 | 0 to 100000 | 0 = no timer. |
| Live monitor (`LiveMonitorEnabled`) | off | | Run as soon as the average drops below the threshold. |
| Live monitor: pause after a run (`LiveCooldownMinutes`) | 30 | 1 to 100000 | Minimum time between two live runs. |
| Extra wait per command (`CommandGraceSeconds`) | 30 | 5 to 100000 | Added to each command's own profiling time before it counts as timed out. |
| Keep this many reports (`MaxReports`) | 50 | 1 to 100000 | Oldest saved reports are deleted beyond this. |
| Delete reports older than (days) (`ReportRetentionDays`) | 2 | 0 to 3650 | 0 = keep by count only. |
| Show top results per profile command (`MaxResultLines`) | 10 | 0 to 1000 | 0 = show everything. |
| Sector name (`SectorName`) | empty | up to 100 characters | Shown in the report and in the Discord posts. Not shared between servers. |
| Repeat offender: compare last (alerts) (`RepeatLookbackAlerts`) | 5 | 0 to 50 | 0 = no repeat offender line. |
| Repeat offender: only alerts from the last (days) (`RepeatWindowDays`) | 7 | 1 to 90 | Also the window for the Galaxy Offenders register. |

### Diagnostic commands

| Setting | Default | Meaning |
|---|---|---|
| Diagnostic commands (`DiagnosticCommands`) | the list in 4.3 | One command per line, run in order. Include the `!` prefix. |
| Completion marker (`CompletionMarker`) | `Finished profiling` | Text that ends a `!profile` command. Only in `SimWatch.cfg`. |
| Physics follow-up (`InspectSmallClusters`) | on | Run `!profile physics --inspect=0` after a physics command when the top cluster is small. |
| Physics follow-up threshold (`InspectEntityThreshold`) | 20 | "Small" means fewer entities than this (1 to 100000). |

### Concealment

| Setting | Default | Meaning |
|---|---|---|
| Run a Concealment command after diagnostics (`ConcealAfterDiagnostics`) | off | Needs the Concealment plugin. |
| Command (`ConcealCommand`) | `!conceal 2000` | Include the `!`. |

### Discord

| Setting | Default | Meaning |
|---|---|---|
| Enable (`DiscordEnabled`) | off | Post automatic reports. `!simwatch scan` posts regardless. |
| Webhook URL (`DiscordWebhookUrl`) | empty | Must start with `https://`. Keep it private. |
| Bot name (`DiscordUsername`) | SimWatch | Name shown on the posts. |
| Also attach the full report as a .txt file (`DiscordAttachFile`) | off | The report is always posted as embeds; this adds the plain-text file. |
| Don't post when the speed is back at 1.00 (`SkipDiscordWhenRecovered`) | on | See 4.6. |
| Ping role ID (`DiscordPingRoleId`) | empty | Numeric ID of the role to ping on alerts. Empty = no ping. |

### Galaxy Offenders (second webhook)

| Setting | Default | Range | Meaning |
|---|---|---|---|
| Post a Galaxy Offenders bulletin (`GalaxyEnabled`) | off | | |
| Webhook URL (`GalaxyWebhookUrl`) | empty | | The channel for the bulletin. |
| Bot name (`GalaxyUsername`) | Galaxy Offenders | | |
| Register after (alerts) (`GalaxyThreshold`) | 3 | 2 to 50 | Top result in this many alerts within the window puts a name on the register. |
| Show grid GPS after (alerts) (`GalaxyGpsThreshold`) | 10 | 2 to 1000 | A grid with this many incidents also gets its GPS position in the bulletin. |

### Cross-server config sharing

| Setting | Default | Meaning |
|---|---|---|
| Mode (`SharedConfigMode`) | Off | Off, Follow or Publish. See section 10. |
| Shared folder or file (`SharedConfigPath`) | empty | Folder or file all servers can reach. A folder gets the file name `SimWatch.shared.cfg`. |
| Also share webhook URLs and the ping role (`SharedIncludeDiscord`) | off | Must be on for both the publisher and the followers. |

---

## 6. Commands

All commands need Admin rank. In the Torch console or in game chat:

| Command | What it does |
|---|---|
| `!simwatch status` | Shows the settings, the live average, whether the Profiler and Concealment plugins were found, and the result of the last check. |
| `!simwatch check` | Samples the sim speed now and runs the diagnostics only if it is below the threshold. |
| `!simwatch test` | Runs the whole diagnostic sequence now, even if the sim speed is fine. Posts to Discord if Discord is enabled. |
| `!simwatch scan` | Runs the whole sequence now and always posts the report to Discord (needs a webhook URL). Recorded in the history if the speed was low. |
| `!simwatch profiler` | Checks whether the Profiler and Concealment plugins are installed. If one is missing it lists the plugins it did find. |
| `!simwatch offenders` | Shows the current Galaxy Offenders register. |
| `!simwatch share status` | Shows the sharing mode, file and last sync result. |
| `!simwatch share push` | Writes this server's shared settings to the shared file now. |
| `!simwatch share pull` | Applies the shared file to this server now. |
| `!simwatch reload` | Re-reads `SimWatch.cfg`. |

Only one check can run at a time; a second request is answered with "A check is already running".

On the SimWatch page the buttons are: **Run diagnostics now**, **Open reports folder**, **Reload from file**, **Re-check Profiler**, **Send test message**, **Send test bulletin**, **Publish now** and **Pull now**.

---

## 7. Discord setup

### Main report webhook

1. In Discord open the channel settings: **Integrations > Webhooks > New Webhook**.
2. Copy the webhook URL.
3. In SimWatch paste it into **Webhook URL**, tick **Enable**, and press **Send test message**. A short "online" message appears in the channel.
4. Treat the URL like a password. Anyone who has it can post to the channel.

### Pinging a role

1. In Discord turn on **Developer Mode** (User Settings > Advanced).
2. Server Settings > Roles, right-click the role, **Copy Role ID**.
3. Paste it into **Ping role ID** (a pasted `<@&123...>` also works; anything that is not a valid ID is cleared).
4. The first message of each sim speed alert then starts with the role mention. Only that one role can be pinged. Manual scans and test runs do not ping.
5. The **Send test message** button pings the role too, so you can check it. This notifies people.
6. The role has to be mentionable, otherwise Discord shows the text but does not notify.

### Galaxy Offenders webhook

Create a second webhook in another channel, paste it into the Galaxy Offenders URL field, tick the checkbox, and press **Send test bulletin** to see a sample. Keep in mind that player names can appear in that channel.

---

## 8. Reading a report

Saved reports are named `simwatch-YYYYMMDD-HHMMSS.txt`. The Discord post contains the same information as embeds, in a terminal style.

**Header**

| Line | Meaning |
|---|---|
| Server / Sector | Machine name and your sector name (if set). |
| Time | Server clock time. |
| Trigger | `sim speed below threshold`, `... (live monitor)`, `manual scan (!simwatch scan)` or `forced (test run)`. |
| Sim speed | Average, min and max over the window, and the threshold. |
| Server proc | How many CPU cores the server process used, out of the logical cores, and its memory. |
| Profiler / Concealment | Whether the plugins were found. |

**Command sections** (`===== !profile grids ... =====`): the output of each command, cut to the top results. Status can be completed, timed out, no response or skipped.

**Sim speed when diagnostics finished**: the reading after all commands.

**Estimated cause**: a rule-of-thumb guess (it never replaces reading the full output):

| Line | Meaning |
|---|---|
| `verdict` | If the speed was fine during the sample, it says so. If the server process was mostly idle (under 0.5 cores busy) while the speed was low, it suggests waiting on disk, network, a GC pause or another program taking CPU. Otherwise it calls the server CPU-bound (main thread overloaded). |
| `pattern` | **Sustained** (steady low speed) or **spiky** (dips well below the average, under 60 percent of it: stalls, saves, big events). |
| `cpu` | Cores busy and memory. |
| `top_suspect` | The `!profile` result line with the largest millisecond figure. If none has milliseconds, the first available in this order: grids, scripts, entities, blocktypes, session, players, frames. |
| `physics` | Size of physics cluster 0, and whether it counts as large. |
| `top_<command>` | The top line of each profile command. |
| `repeat_offender` | See section 9. |
| `caveats` | Commands that timed out or got no response, or a missing Profiler. |

Each report ends with the footer line.

---

## 9. Repeat offenders and Galaxy Offenders

### The history

For every real low-sim-speed alert (not test runs, not runs where the speed was fine) SimWatch remembers the top result of each profile command that names a specific object: grids, scripts, players and any other `!profile` command you add. Frames, session, entities and block types are left out because their top entry is nearly always the same kind of thing. The names are stored in `SimWatch-Reports\history.tsv` (up to 200 alerts or 90 days).

SimWatch gets a name by removing the rank number, timings and percentages from the profiler line. So this part depends on the Profiler printing the name on that line.

### Repeat offender line

If the same name was the top result of the same command in 2 or more of the latest alerts (default: the last 5 within 7 days, this alert included), the estimated cause gets a line like:

```
repeat_offender : grids: "Base Alpha" was the top grids result in 3 of the last 5 alerts (first seen 2026-10-08 14:30)
```

With the option on, the line always appears on real alerts and says so when there are no earlier alerts or nothing repeats.

### The Galaxy Offenders register and bulletin

A name goes on the **Offenders Register** when it was the top result in **3 or more** alerts within the window (default 7 days). When the register contains a name that has not been announced yet, SimWatch posts the whole register to the Galaxy Offenders webhook. A name that is still an offender is announced again after the window has passed.

The bulletin is written as an incident record from the "Galactic Traffic & Systems Authority":

- **Header block**: record number (`GOA-yyyyMMdd-HHmm`), sector name, issue time in UK time (GMT/BST), threat level and mission status.
  - Threat level comes from how far the sim speed fell in the latest alert: average below 0.5 is **CRITICAL**, below 0.75 **SEVERE**, below 0.9 **ELEVATED**, otherwise **ADVISORY**. The embed colour follows it.
  - Mission status says whether the simulation was stabilised (1.00) or still degraded at the end of the diagnostics.
- **Briefing** and **Detected anomalies**: how many drag events were logged in the window, the last event's speeds, the reading at the end, and the register size.
- **Case files**: numbered entries for structures and scripts (up to 5 listed) with incidents, first and last seen, and a status:
  - **ON REGISTER**: below the GPS threshold.
  - **BEACON LOCKED**: a grid at or above the GPS threshold (default 10 incidents).
  - **ESCALATED**: a script (or other non-grid) at or above that number.
- **Beacon fix**: for a BEACON LOCKED grid, SimWatch looks the grid up by the name the Profiler printed and posts its current position as a copyable `GPS:name:x:y:z:#FF0000:` string. An exact name match wins; among equal matches the grid with the most blocks is used. If nothing matches (renamed, concealed, deleted), the bulletin says the position is unresolved. A grid that was announced at 3 incidents is announced again when it reaches the GPS level.
- **Pilot files**: every player gets a separate embed with the retro engineer badge as its icon (up to 4; the rest are listed in the main record).
- **Operator advisory**: a closing recommendation, ending with a note that it is a load reading, not a finding of fault.

The Galaxy Offender logo is attached to the post as a small thumbnail. `!simwatch offenders` shows the current register in chat, and **Send test bulletin** posts a sample with made-up entries.

---

## 10. Running several servers (config sharing)

SimWatch can share its settings between servers through one small file, `SimWatch.shared.cfg`, in a folder all the servers can reach (a network share or a synced folder).

**Setup**

1. Pick the **master** server. Set its **Mode** to **Publish**, and **Shared folder or file** to the shared location. The shared settings are written to the file right away.
2. On every other server set **Mode** to **Follow** and the same **Shared folder or file**. They read the file at start and then check it every 15 seconds, applying any setting that differs.
3. Change shared settings only on the master. The master rewrites the file a few seconds after each change.

**What is shared**

- Shared: thresholds, timer, live monitor, diagnostic commands, limits, Concealment, repeat offender, Discord and Galaxy options (except the ones below).
- Never shared: the sector name and the three sharing settings.
- Shared only when **Also share webhook URLs and the ping role** is on (on the master **and** on the followers): the two webhook URLs and the ping role ID. The file then contains the URLs, so keep the folder private.
- Not shared: reports, the history and the offender register. Each server keeps its own.

**Good to know**

- Use Publish on **one** server only. Two publishers overwrite each other.
- On a follower, a shared setting you change locally stays until the file next changes, then the file wins.
- The file is written under a temporary name and swapped in, so a follower never reads a half-written file.
- If the path cannot be reached, a server keeps its current settings and the status line on the page (or `!simwatch share status`) shows the problem.
- **Publish now**, **Pull now** and `!simwatch share push` / `pull` do a one-off write or read in any mode (even Off).

---

## 11. Files and folders

All of these are in the Torch instance folder (where the other plugin config files are).

| Path | What it is |
|---|---|
| `SimWatch.cfg` | Your settings. Saved automatically. |
| `SimWatch.cfg.broken` | A copy of `SimWatch.cfg` made if it could not be read (then defaults are used). |
| `SimWatch-Reports\simwatch-*.txt` | Saved reports. Cleaned up by age and count. |
| `SimWatch-Reports\history.tsv` | The alert history for repeat offenders and the register. |
| `SimWatch-Reports\galaxy-announced.tsv` | Which register entries were already announced. |
| `SimWatch.shared.cfg` | The shared settings file, in the folder you chose for sharing (not necessarily here). |

The log lines all start with `SimWatch:` in the Torch log.

---

## 12. Troubleshooting

| Problem | What to check |
|---|---|
| The Profiler line on the page is red, or reports say the Profiler is missing | Install and enable the Profiler plugin and restart. `!simwatch profiler` lists the plugins SimWatch can see. SimWatch recognises the Profiler by its assembly/namespace name `Profiler`. |
| The Profiler state is "unknown" | SimWatch could not read Torch's plugin list. The commands are simply tried, and the report shows whether they answered. |
| Nothing runs automatically | Check **Enable automatic checks**, that the timer is above 0 or the live monitor is on, and that the sim speed really goes below the threshold. Look for the warning "nothing runs automatically" in the log. Check that a world session is running. |
| A report was saved but not posted | The speed was back at 1.00 at the end (4.6), Discord is not enabled, or the webhook URL is empty. The log says which. |
| Commands time out | Raise **Extra wait per command**. A command that prints nothing is reported as "no response". |
| Discord says 4xx or the test fails | Check the webhook URL (must start with `https://`) and that the webhook still exists. The error text from Discord is shown in the status line. |
| The role is not pinged | The role must be mentionable, and the ID must be the numeric role ID, not its name. An invalid ID is cleared when you leave the field. |
| The repeat offender / register entries have strange names | The Profiler line format may differ from what SimWatch expects. Send a saved report to whoever maintains the plugin so the name extraction can be adjusted. |
| "Beacon fix: unresolved" | No grid with that name was found. It may be concealed, renamed or deleted, or the Profiler printed a different name. |
| Shared config does nothing | `!simwatch share status` shows the mode, the resolved file and the last result. Check the path is reachable from the server's user account and that the publisher is in Publish mode. |
| Settings went back to defaults | `SimWatch.cfg` could not be read; look for `SimWatch.cfg.broken` and the error in the log. |
| `build.bat` says required DLLs are missing | One of the folders is wrong. Delete `build-paths.bat` and run again. |

---

## 13. Limitations

- SimWatch relies on the Profiler's output text. Finding the top line, the name of a grid, script or player, and the physics cluster size all depend on that text having the expected shape.
- The estimated cause is a rule of thumb from the sim speed pattern, CPU use and the top profiler lines. Always confirm it against the full output.
- Names in the Galaxy Offenders bulletin and pilot files come from profiler data. A listing means load was attributed to that entry, not that anyone did anything wrong.
- The Profiler and Concealment plugins are recognised by their assembly/namespace names (`Profiler`, `Concealment`).
- The GPS lookup finds a grid by name only; it cannot find concealed, renamed or deleted grids.
- Times in reports use the server's clock. Times in the Galaxy Offenders bulletin are UK time.

---

## 14. Upgrading and uninstalling

**Upgrading**: replace `SimWatch.zip` in the Torch `Plugins` folder and restart. Your `SimWatch.cfg` is kept; new settings get their defaults. A `SimWatch.cfg` made by an early version does not contain the `!profile physics` line; add it in the GUI if you want it.

**Uninstalling**: remove `SimWatch.zip` from the `Plugins` folder (or untick it in the Torch plugin list) and restart. You can delete `SimWatch.cfg` and the `SimWatch-Reports` folder if you no longer need them.
