# SimWatch

Torch plugin for Space Engineers dedicated servers. **Full manual: [docs/MANUAL.md](docs/MANUAL.md).** It averages the sim speed over a short window and,
only if the average is below the threshold, runs a list of commands one after another (by default the
Profiler plugin's `!profile ...` commands), captures their output, saves a report and can post the whole
report to a Discord channel.

Two optional triggers (use either, both, or neither and run it by command):
- **Timer**: check every N minutes (default 30). Set it to **0 for no timer**.
- **Live monitor**: watches the sim speed every second and runs the diagnostics as soon as the average
  over the window drops below the threshold. No timer needed. After a run it pauses for the cooldown
  (default 30 min) and it ignores the first 2 minutes after the server starts.

Reports: `<Torch instance>\SimWatch-Reports\simwatch-YYYYMMDD-HHMMSS.txt` (also written to the Torch log). Saved reports older than 2 days are deleted automatically (setting: "Delete reports older than", 0 keeps them; the "keep this many reports" limit still applies).

## How often diagnostics run
Diagnostics never run while the averaged sim speed is at or above the threshold (default 0.9; set it to 1.0 to react to
any drop below full speed). When it is below, how often they run depends on the trigger:
- **Timer** (default, every 30 min): at each check the sim speed is averaged. If it is below the threshold the
  diagnostics run, so at most once per interval (30 min, or whatever you set). If the speed is fine at that moment, nothing runs.
- **Live monitor** (optional): runs as soon as the average drops below the threshold, then waits for the cooldown
  (default 30 min, adjustable) before it can run again, even if the speed stays low.
- Both set to off/0: nothing runs automatically; use the `!simwatch` commands.

## Build
Easiest: double-click `build.bat`. It asks for your Torch folder once (and finds DedicatedServer64 inside it),
checks the DLLs, builds, and puts `SimWatch.zip` next to the bat file.

Manual build:
Needs the .NET SDK (the project uses WPF, so build on Windows). Point it at your Torch folder
(Torch.dll) and DedicatedServer64 folder (Sandbox.Game.dll):

    dotnet build -c Release -p:TorchDir="C:\path\to\Torch" -p:GameDir="C:\path\to\DedicatedServer64"

(or create `TorchBinaries` / `GameBinaries` junctions next to SimWatch.csproj, like Profiler's Setup .bat does.)
Output: `bin\Release\net48\SimWatch.zip`

## Install
Copy SimWatch.zip into Torch's `Plugins` folder, enable SimWatch in the Torch plugin list, restart.
The Profiler plugin must also be installed and enabled for the default commands to work. SimWatch looks for it
(a loaded plugin whose assembly/namespace is `Profiler`) and shows the result in several places: a coloured line at
the top of the plugin page in the Torch UI (green installed / red missing, with a "Re-check Profiler" button), the Torch
log when the server starts ("SimWatch: Profiler check: ..."), the `!simwatch profiler` command, `!simwatch status`,
the webhook test message and every report. When it is missing, SimWatch skips `!profile ...` commands (marked "skipped" in
the report) instead of waiting for them to time out.

## Repeat offender flag
For every sim speed alert SimWatch remembers the top result of each profile command that names a specific object
(grids, scripts, players and any other `!profile` command you add; frames, session, entities and block types are left out
because their top entry is nearly always the same). The names are kept in `SimWatch-Reports\history.tsv`, which the 2-day
report cleanup does not touch (it keeps up to 200 alerts / 90 days). If the same name was the top result in 2 or more of
the latest alerts (default: the last 5 within 7 days, this one included), the estimated cause gets a line such as
`repeat_offender : grids: "Base Alpha" was the top grids result in 3 of the last 5 alerts (first seen 2026-10-08 14:30)`.
With the option on, the line always appears, saying so when there are no earlier alerts or nothing repeats. Only real
low-sim-speed alerts are recorded, not test runs. Names are compared after the ranking numbers, timings and percentages
are stripped from the profiler line, so this depends on the Profiler printing the name on that line.

## Galaxy Offenders (second webhook, optional)
A separate Discord channel for a roleplay-style "sector bulletin". Turn it on and give it its own webhook URL. Every real
low-sim-speed alert is recorded in the history (see the repeat offender flag above). When a grid, script or player was the
top result in 3 or more alerts within the window (default 7 days), it goes on the **Offenders Register** and SimWatch
posts the whole register: names, kind (structure / script / pilot), number of incidents and when it was last seen. The
bulletin is only posted when the register contains a name that wasn't announced yet; a name that is still an offender is
announced again after the window has passed. The bulletin is written as an in-universe incident record from the "Galactic Traffic & Systems Authority": a header block
(record number, UK time, threat level from how far the sim speed fell, mission status), a briefing, detected anomalies
(last event, reading at the end of the diagnostics, register size), numbered case files for structures and scripts
(incidents, first/last seen, status ON REGISTER / ESCALATED / BEACON LOCKED) and an operator advisory. Every player gets a
separate "pilot file" embed with the retro engineer badge as its icon (up to 4 files, the rest are listed in the record).
All times in the bulletin are UK time (GMT/BST). It carries the Galaxy Offender logo (embedded in the plugin, sent
with the message as a small thumbnail). When a grid reaches **10 incidents** (setting "Show grid GPS after") it is
announced again with its current GPS position, as a `GPS:name:x:y:z:#FF0000:` string players can copy into their GPS list.
The grid is found by the name the Profiler printed (an exact match wins; among equal matches the grid with the most blocks
is used); if no grid matches, for example because it is concealed or was renamed, the bulletin says the position is unknown.
The numbers are adjustable ("Register after (alerts)", default 3).
`!simwatch offenders` shows the current register, and "Send test bulletin" posts a sample so you can check the look.
The bulletin works from profiler data and names the entry as printed by the Profiler, so it is a performance flag and
not proof of who is responsible; it says so at the end of the post. Remember that player names would be visible to
whoever can read that channel.

## Posting only when the sim speed is still low
By default an automatic alert report is posted to Discord only if the sim speed at the end of the diagnostics ("Sim speed
when diagnostics finished") is below 1.00. If it is back at 1.00 the report is still saved in `SimWatch-Reports` and logged,
but nothing is posted (and no role is pinged). Manual scans (`!simwatch scan`) always post. The check can be switched off
with "Don't post alert reports when the sim speed is back at 1.00". The alert still counts for the repeat offender flag
and the Galaxy Offenders register.

## Cross-server config sharing (optional)
Run several servers with the same settings by sharing one small file (`SimWatch.shared.cfg`) on a path all of them can
reach, for example a network share or a synced folder. Set "Shared folder or file" to that location, then:
- **Publish** on ONE server (the master): its shared settings are written to the file at start, and again a few seconds after
  every change you make in the GUI or a reload.
- **Follow** on all the others: they read the file at start and whenever it changes (checked every 15 seconds) and apply the
  settings that differ. Edit shared settings on the publishing server, because the file wins on the followers each time it changes.
- **Off**: nothing is shared (default).

Never shared, always local: the sector name, and the sharing settings themselves. The Discord webhook URLs and the ping role are
only shared when "Also share webhook URLs and the ping role" is on, on the publisher and on the follower, because the file then
contains the webhook URLs, so keep the folder private. Everything else (thresholds, timer, live monitor, commands, limits,
Concealment, repeat offender and Galaxy Offenders options) is shared. The history, reports and the offender register stay
per server. "Publish now" and "Pull now" (and `!simwatch share push` / `pull` / `status`) do a one-off write or read in any mode.
The file is written next to its final name and swapped in, so a follower never reads a half-written file. If the path can't be
reached the server keeps its current settings and shows the problem in the status line.

## Pinging a Discord role (optional)
Enter the role's numeric ID in "Ping role ID" (Discord: Developer Mode on, then Server Settings > Roles > right-click the
role > Copy Role ID; a pasted `<@&123...>` works too). The first message of each sim speed alert then starts with that
role's mention, and only that one role is allowed to be pinged. Manual scans and test runs don't ping. The "Send test
message" button does ping, so you can confirm the ID is right. The role must be mentionable, or the webhook's channel
must let it ping, otherwise Discord shows the text but doesn't notify. Empty = no ping.

## Concealment (optional)
If the Concealment plugin is installed, SimWatch can run `!conceal 2000` after a diagnostics run (the command is editable).
It is off by default and only runs when the diagnostics were triggered by a sim speed below the threshold, not for
manual test runs. The command's output is added to the report as its own section. If Concealment isn't found the step is
skipped and says so in the report. The plugin page shows whether Concealment was found, and `!simwatch profiler` checks both plugins.

## GUI
Torch UI > Plugins > SimWatch. Every field is saved to `SimWatch.cfg` as soon as you leave it.
- Averaging window, sim speed threshold, timer interval (0 = no timer), live monitor on/off + cooldown, per-command extra wait, reports kept
- Ping role ID (optional): the role to ping in Discord when a sim speed alert is posted
- Galaxy Offenders: on/off, its own webhook URL, bot name, how many alerts put a name on the register (default 3) and when a grid also gets its GPS (default 10)
- Repeat offender: how many of the latest alerts to compare (default 5, 0 = off) and how many days back to look (default 7)
- Sector name (optional): shown in the saved report and in the Discord post header
- Show top results per profile command (default 10, 0 = all): grids, blocktypes, scripts etc. are cut to their top entries in the report and the Discord post
- Diagnostic commands (one per line, run in order)
- Physics follow-up: after a `!profile physics` command, if cluster 0 has fewer than N entities (default 20),
  `!profile physics --inspect=0` is run too
- Discord: enable, webhook URL, bot name, "Send test message"
- Buttons: Run diagnostics now, Open reports folder, Reload from file

Default command list: frames, session, entities, grids, blocktypes, scripts, players (15 s each) and
`!profile physics --tics=600`. A SimWatch.cfg created by an earlier version won't have the physics line:
add it in the GUI.

## Discord
Create a webhook in the channel settings (Integrations > Webhooks), paste the URL into the GUI.
The report is posted straight into the channel as embeds in a terminal style: a header block (host, time,
trigger, sim speed, process load, profiler), an **estimated cause** block, then one code block per command, then a closing block carrying the footer
"Created by Eldo Rosen for The Destiny Universe". Long reports are split over several messages.
Tick "Also attach the full report as a .txt file" if you want the plain-text file as well.
Every saved report file contains the same estimated-cause block and ends with the same footer.

The estimate is a rule of thumb, not a diagnosis. It combines the sim speed pattern (sustained or spiky), how many CPU
cores the server is using (a low sim speed with an idle process points to waiting on disk/network/GC or another program),
the top line of each `!profile` result (the largest millisecond figure is named as the top suspect) and the size of the
top physics cluster. Always confirm it against the full output below it.

## Chat/console commands (Admin)
- `!simwatch scan`   run the whole sequence now and send the full report to Discord (needs a webhook URL; works even if the Discord checkbox is off)
- `!simwatch test`   run the whole sequence now, regardless of sim speed (sent to Discord only if enabled)
- `!simwatch check`  sample now; diagnose only if below threshold
- `!simwatch status`  settings, live average, Profiler plugin state, last check
- `!simwatch reload`

## Changelog
**1.5.1**
- Fix: editing the diagnostic commands in the GUI on the publishing server now republishes the shared file
- Fix: an illegal character in the shared path no longer throws (publish/pull/status report "no shared path set" instead)
- Fix: a failed pull of an unreadable shared file is retried only when the file changes (no warning every 15 s)
- Fix: a grid or script with "profiling" in its name is no longer mistaken for the Profiler banner
- Fix: names like "Miner 1s" are no longer cut down to "Miner" in the repeat offender history
- Command output is read 1.5 s after the completion marker so results printed after it aren't cut off
- Galaxy Offenders: a name keeps its original announce time when another name triggers a bulletin; a grid whose GPS couldn't be found is looked up again after 6 hours
- Manual runs (`!simwatch check|test|scan`, "Run diagnostics now") stop when the server session unloads

**1.5.0**
- Cross-server config sharing through a shared file (publish on one server, follow on the others), with push/pull buttons and `!simwatch share`

**1.4.0**
- Alert reports are not posted to Discord when the sim speed is back at 1.00 at the end of the diagnostics (setting)
- Galaxy Offenders bulletin rewritten as an in-universe incident record (threat level, mission status, anomalies, case files, advisory, UK time); each pilot gets its own file embed with the retro engineer badge as icon

**1.3.0**
- Galaxy Offenders bulletin: logo added (scaled to 512x512), milky way icon removed from the title, grid GPS position at 10 incidents

**1.2.0**
- Timer is optional (interval 0 = no timer); new live monitor that runs diagnostics as soon as the average sim speed drops below the threshold, with a cooldown
- Profiler plugin check (plugin page, `!simwatch profiler`, startup log, reports); `!profile` commands are skipped when it is missing
- Estimated cause block in reports and Discord (replaces the summary block); each profile result limited to its top entries (default 10)
- Reports older than 2 days are deleted automatically (setting)
- Optional `!conceal 2000` after a low-sim-speed run when the Concealment plugin is installed
- Optional sector name in reports; optional Discord role ping on alerts
- Repeat offender flag and the Galaxy Offenders bulletin (second webhook)

**1.1.0** Earlier changes.
**1.0.0** First version.
