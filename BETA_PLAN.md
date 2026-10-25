# Chaos Arena — Beta Plan

Working plan for launch. One task at a time, each ending in a commit and push. Rewritten whenever a decision changes; `CORE_DATA_SET.md` holds the content contract and `TOOLTIP_TEMPLATES.md` holds the tooltip architecture.

Feature work finishes first. Verification is a single pass at the end of the list, not a per-task ritual.

## Decisions locked in this revision

| Decision | Choice |
|---|---|
| Ability implementation | **Core engine effect system** (generic RTS), map-supplied definitions and map-level tuning. No per-ability hacks in `MapScript.cs` unless the engine genuinely has no generic home for it. |
| Ability scaling | One primary attribute, one documented shape (linear, ratio, saturating, threshold, inverse, conversion), optional `ScalesDuration`. |
| Stat vocabulary | **Canonical keys only.** The alias map was removed: a key outside `StatKeys` is reported and ignored, never remapped. Old authored names were migrated by the map's sequential migrations. |
| Items / upgrades | Pure stats, 1–3 modifiers, negatives allowed, stacks are literal copies, no passives. Cards **render** the modifier list; authors write flavor only. |
| Tooltips | `{{Scope.Key}}` templates resolved **at hover** against ECS stats and map-published values. See `TOOLTIP_TEMPLATES.md`. |
| Player instructions | None. The map teaches through tooltips, cards and the wave loop itself; `HowToPlayInstructions` is deleted rather than rewritten. Chat commands stay developer-only. |
| Elements | Pure draft slots. No cross-hero role or damage-type mapping. |
| Elixirs | 20 scripted permanent passives, one per unit, inherited by clones, plus a flush consumable. The record is the only source of the numbers; metadata carries the prose. |
| Match end | Waves stop at 25:00 and nothing is eliminated before then. From 25:00 on, a player whose last fielded wave has lost its final unit is eliminated on the next one-second match tick; placement is reverse elimination order. Any field still standing at 30:00 is ranked by living units, then kills, then slot. |
| Rerolls | 30 s cooldown **per tile**, paid in stone (item 3, element 3, hero 5, all 8, swap 5). Stone accrues +1 per kill and +1 per 10 s, cap 40. An action that cannot do anything is hidden rather than left as a dead button. |

Two invariants for all engine work: the engine stays a **generic RTS** (no Chaos Arena rules), and map-specific behaviour enters through metadata, `IGameAPI`, or map script — never through `if (mapName == ...)` branches.

## Status

| Task | State |
|---|---|
| E1 canonical stat keys, complete modifier wiring | done |
| E2 buff → modifier bridge | done |
| E3 level → stats | done |
| E4 resolver-backed scripted damage | done |
| E5 ability effect system + schema migration | done |
| E6 autocast policy (engine) | done |
| E7 permanent modifiers for elixirs | done |
| E8 map-authorable combat tuning | done |
| M1 hero roster, items, upgrades, elixirs | done |
| M2 60 ability definitions, map tuning | done |
| M3 circle UX | **open** |
| M4 clone loadout | done |
| M5 rerolls and swaps | done |
| M6 altar elixirs | done |
| M7 blacksmith upgrades | done |
| M8 match end and economy | done |
| T1 tooltip templates | done |

## Open defects found in the 25 Oct audit

1. ~~**Nothing casts an ability in a real match.**~~ Fixed in M4b: `SpawnWaveForPlayer` now calls `EnableElementAutocast`, which turns the bit on for the drafted element and explicitly off for the other two. The mana gating, the autocast policies and the elixir Cast hooks are live as a result.
2. **Mana pools are authored as residuals.** The committed `MaxMana` values are what is left after subtracting `Vitality × VitalityManaPerPoint`, so a hero's real pool is `authored + Vitality × 8`. Stone Guardian authors 0 and carries 14–22 mana abilities. Fix by authoring absolute pools and pinning `VitalityManaPerPoint` to 0 in the map's tuning block.
3. **"Per wave" means "per clone life" — wording fixed in T1.** Wave clones are never cycled between waves, so an elixir cap of 25 heals is really 25 heals for as long as that clone lives — and a clone that survives three waves banks three waves of allowance. Cards now say "per unit" / "the whole life of the unit"; resetting elixir state at wave start is still an open design question if the caps should refresh.
4. **Real players are not allied to each other.** `MapScript` sets real↔proxy and proxy↔proxy but never real↔real. Decide between finishing the alliance graph and declaring free-for-all.
5. **Generated constants are stale.** `MapTemplates.g.cs` still exports dropped heroes, `ability/upgrade_critical_strike`, and an empty `UpgradeIds`. Nothing reads them yet, which is the only reason this is cosmetic.
6. **Debug command naming is inconsistent.** `-targetDummy` is implemented; the deleted instructions documented `-target_dummy`. Keep one spelling and record it here, not in player-facing text.

## Engine workstream (Realm) — complete

E1 through E8 shipped. The load-bearing outcomes, restated so later work does not re-derive them:

- **One stat pipeline.** `AttributeStatCalculator` owns derived stats; `StatService` reads the snapshot it writes, so a read never re-applies modifiers. `StatKeys` is the sole vocabulary and reports unknown keys with their source.
- **One damage path.** `CombatResolver` orders resistance, penetration, mitigation, floor and caps for physical, magic and pure, and emits a `DamageResult` per application. Weapons carry their own damage type.
- **Resistance, not a type chart.** An earlier revision planned a 36-row damage/armor matrix; that was replaced by the resistance model (`R / (R + K)` with `MaxResistMitigation`) because a map-authored matrix could not be kept honest across future damage types. `CombatTuningMetadata` now carries the coefficients, caps and floors — 60-odd optional fields, all defaulting to the engine's previous constants.
- **Abilities are data.** An ability is a list of effects (damage, heal, shield, buff, debuff, summon, teleport, knockback) with delivery, target side, scaling and per-ability autocast policy. Hidden and disabled abilities are excluded from autocast.
- **Levels are read.** `UnitBaseStats` growth is applied on spawn and on level change.
- **Scripts get a typed damage entry point** and source-tagged permanent modifiers.

Chaos Arena authors exactly four tuning values: `ResistTuningConstantK`, `MaxResistMitigation`, `MinDamageFloor`, `IntelligenceManaRegenPerPoint`.

## Map workstream (ugc-ChaosArena)

### M3. Circle UX — open
Circles hold three element-description abilities, one visible, plus the item icon with its stack count. No more 60-ability bars, no global `SetAbilityTooltip` writes; the element name becomes `{{Source.Element}}` published per unit, and each card's numbers come from the ability template.

### M4b. Enable autocast on clones — done
`EnableElementAutocast` runs after `ApplyElement` on every clone: the drafted element's ability gets an autocast bit, the other two get an explicit off. Every hero carries its three element abilities in Water/Earth/Fire order, which is the same order `ApplyElement` uses, so the bit and the visible button agree.

Unverified until a match is run: the cast itself. The engine path (query, range, mana, cooldown, the per-ability policy authored in M2) is what the verification pass watches.

### T1. Tooltip templates — done
Shipped in two commits (engine, then this map). Cards author `{{Scope.Key}}` tokens and the string is produced on hover from the stat snapshot and the authored effect list, so a card cannot quote a number the game is not using.

- Engine: `TooltipTemplate`, `TooltipContext`, `TooltipValueStore`, hover-time resolution in `CommandCardButton`, item and upgrade cards rendering their own modifier block, and single-value publishing through `IGameAPI` (arrays cannot cross the wasm boundary).
- Map: 60 ability descriptions quote `Ability.Effect{N}.*` fields; the 20 elixir cards quote the four values the script publishes from `ElixirDefinition` (`Amount`, `Cap`, `Procs`, `Cost`), so the price and the cap now come from one place.
- `verify_tooltips.py` resolves every card against the map's own data and fails on a token the data cannot answer; it reports 80 cards, 0 problems.

Still open from the design (T2): `Target` scope and computed damage previews, a `Map` scope, conditionals, and the editor-side validation panel.

### Balance pass — after T1
Not beta-blocking except for obvious errors. Move one knob at a time: damage per gold, effective health per armor point, mana per second of regeneration, elixir value per wave. Record each change in `CORE_DATA_SET.md`.

## Execution order

```
M3 → balance pass → verification pass
```

Everything feature-side before M3 is done, so the next change is the draft surface the player actually touches, then the balance pass, then one end-to-end verification.

## Verification pass (end of the list, run once)

1. `dotnet build Realm.slnx` and `dotnet build MapScript.csproj` both clean.
2. A full match to 30:00 observed: casts happen, the hidden element slots never cast, an elixir proc fires, mana gates a rotation, level 9 differs from level 1 in health and damage.
3. Draft, reroll, swap, altar and blacksmith each exercised once, including a stone-starved tile and an eliminated player's tiles.
4. Elimination and placement: 25:00 cutoff, first field emptied is last place, the survivor list at 30:00 matches the display.
5. Tooltips: every card renders with no `{{` left visible, and an item's card matches the modifiers it grants.
6. Fix what the pass finds, in its own commits.

## Commit discipline

Every task = one commit in the repo it touches, pushed to `devin`. Engine and map commits are separate. Messages state the behaviour change, not the file list.

## Phase 2 (explicitly not beta)

Windup / hit-frame / backswing · projectile-carried attacks · multi-weapon units · crit sharing to splash · full CC diminishing returns and dispel · DoT / absorb / execute / vampirism beyond E5's basics · deterministic replay RNG and divergence self-check · combat log and debug overlay · editor authoring UI for the effect schema · competitive bot policy · additional families (Wind, Aether) and roles (Assassin, Bruiser) · `Target` scope and computed damage previews.

Each is justified by "no beta content authors it" or "invisible in a 45-second auto-battle". Any that becomes necessary moves into beta with its own task.
