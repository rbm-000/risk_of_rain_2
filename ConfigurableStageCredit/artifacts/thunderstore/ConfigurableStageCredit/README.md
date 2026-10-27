# ConfigurableStageCredit

Lets you tune the stage's **enemy credit** and **loot/interactable credit** independently.

## Configuration

After launching the game once with the mod installed, edit `BepInEx/config/com.configurablestagecredit.plugin.cfg`.

The `[Enemy Credit]` and `[Loot Credit]` sections each have the following settings:

| Setting | Meaning |
|---|---|
| `StartingMultiplier` | Multiplier on stage 1. `1.15` means 115% of the initial credit budget. If below `MinimumMultiplier`, it is set to `MinimumMultiplier`. |
| `StageIncrement` | Additive multiplier change between stages, except when entering a new 5-stage loop. |
| `LoopIncrement` | Additive multiplier change when entering the next loop (stage 6, 11, 16, etc.). This replaces `StageIncrement` for that transition only. |
| `MinimumMultiplier` | Lower bound. Negative values are corrected to `0`. |
| `MaximumMultiplier` | Upper bound. If lower than the minimum, it is changed to equal the minimum. |

The `[General]` section has this setting:

| Setting | Default | Meaning |
|---|---:|---|
| `PrintCreditOnStageStart` | `false` | When enabled, prints the stage number, multiplier, original credit budget, and modified credit budget for both enemy and loot credits in chat. |

### How the multiplier is calculated

The Stage 1 multiplier is clamped to the configured bounds. Each later stage adds its increment to the current multiplier, then clamps it again. Stages 2-5 use `StageIncrement` from the previous stage. Entering stage 6 uses `LoopIncrement` from stage 5 instead of `StageIncrement`; stages 7-10 go back to `StageIncrement`. The same rule repeats at stages 11, 16, and so on. Because every transition starts from the already-clamped multiplier, later positive or negative increments behave naturally after a bound is reached.

Example with `StartingMultiplier = 1.05`, `StageIncrement = -0.05`, and `LoopIncrement = -0.30`:

| Stage | Multiplier | Reason |
|---:|---:|---|
| 1 | 1.05 | StartingMultiplier |
| 2 | 1.00 | StageIncrement (-0.05) |
| 3 | 0.95 | StageIncrement (-0.05) |
| 4 | 0.90 | StageIncrement (-0.05) |
| 5 | 0.85 | StageIncrement (-0.05) |
| 6 | 0.55 | LoopIncrement (-0.30) |
| 7 | 0.50 | StageIncrement (-0.05) |

Example config:

```ini
[General]
PrintCreditOnStageStart = false

[Enemy Credit]
StartingMultiplier = 1.05
StageIncrement = -0.05
LoopIncrement = -0.30
MinimumMultiplier = 0.0
MaximumMultiplier = 10.0

[Loot Credit]
StartingMultiplier = 1.0
StageIncrement = 0.1
LoopIncrement = 0.25
MinimumMultiplier = 0.5
MaximumMultiplier = 3.0
```

When `PrintCreditOnStageStart = true`, the mod sends one chat message for each map after it has captured both budgets. It waits briefly for the new scene to load, then automatically opens and focuses the local chat input so the report is visible. This takes keyboard focus until you close the chat; press Escape if you do not want to type. In multiplayer, each player needs the mod and this setting enabled for their own chat input to open. For example:

```text
<style=cEvent>[ConfigurableStageCredit]</style> Stage 6
   Enemy x0.55: vanilla 100, new 55
   Loot x1.25: vanilla 400, new 500
```

The credit values in that message are the budgets seen immediately before and after this mod applies its multiplier. If other mods also alter scene director credits, their hook order can affect the value this mod sees as the “vanilla” baseline.

The mod applies the changes on the server when the scene director prepares its spawn budgets. In multiplayer, the host/server's configuration determines the budgets. This adjusts the total budget available to the directors, not the individual credit cost of each spawn card.

## Building from source (Windows)

### Requirements

- Risk of Rain 2 installed.
- .NET 8 SDK.
- BepInExPack for Risk of Rain 2 installed in the modding profile used for development.

### Configure paths

1. Copy `Config.Build.user.props.template` to `Config.Build.user.props` in this folder.
2. Set `RoR2Dir` to the directory containing `Risk of Rain 2.exe`.
3. Set `BepInExCoreDir` to the `BepInEx\core` directory for your modded profile. With r2modman, it is normally inside the selected profile directory, not the Steam game directory.

### Build and create the Thunderstore zip

Open PowerShell in this directory and run:

```powershell
.\build-and-pack.ps1
```

If the build succeeds, the uploadable package zip will be created under `artifacts\thunderstore\ConfigurableStageCredit-0.3.0.zip`.

The source project does not include the compiled plugin DLL because it must be compiled against your installed Risk of Rain 2 (`RoR2.dll`) and BepInEx assemblies. The script builds the DLL and places it in the Thunderstore package layout.

## Testing checklist

1. Set `StartingMultiplier = 1.05`, `StageIncrement = -0.05`, and `LoopIncrement = -0.30`; verify stage 4 is `0.90`, stage 5 is `0.85`, and stage 6 is `0.55`.
2. Verify stages 7-10 use `StageIncrement`, and stage 11 uses `LoopIncrement` again.
3. Set `MinimumMultiplier = 0.8` and use a sufficiently negative increment; verify it stops at 0.8.
4. Set `MaximumMultiplier` lower than `MinimumMultiplier`; verify the config corrects the maximum to match the minimum.
5. Enable `PrintCreditOnStageStart`; verify one chat message reports enemy and loot multipliers and before/after budgets on each map, including entering and leaving the Bazaar, and verify the chat input opens automatically.
6. Test in a multiplayer lobby with the host running the mod.

## Notes

This source release has not been compiled or play-tested in this environment because Risk of Rain 2's local managed assemblies and BepInEx profile files are not available here. The build script checks for the required reference assemblies and gives a specific error if paths are missing.
