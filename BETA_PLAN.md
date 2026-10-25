# Chaos Arena — Beta Plan

Working plan for launch. One task at a time, each ending in a build plus a commit and push. This file is rewritten whenever a decision changes; `CORE_DATA_SET.md` holds the content contract.

## Decisions locked in this revision

| Decision | Choice |
|---|---|
| Ability implementation | **Core engine effect system** (generic RTS), with map-supplied definitions and map-level tuning. No per-ability hacks in `MapScript.cs` unless the engine genuinely has no generic home for it. |
| Ability scaling | One primary attribute out of Strength/Agility/Vitality/Intelligence/Wisdom/Fortune, one documented scaling shape (linear, ratio, saturating, threshold, inverse, conversion). |
| Buffs | Add the **buff → stat modifier bridge** in the engine. |
| Spell damage | New resolver-backed damage overload; spells no longer bypass armor. |
| Elements | Pure draft slots. No cross-hero role or damage-type mapping. |
| Uniqueness | Role tags per ability; no duplicate (effect + delivery + shape); a hero's three differ on role or shape. |
| Items | Pure stats, 1–3 modifiers (2 most common), negatives allowed, **stacks are literal copies**, no passives. |
| Elixirs | 20 scripted permanent passives, one per unit, inherited by clones, plus a flush consumable. |
| Blacksmith | 20 distinct one-shot upgrades. |
| Match end | Waves stop at 25:00 and nothing is eliminated before then. From 25:00 on, a player whose last fielded wave has lost its final unit is eliminated on the next one-second match tick; placement is reverse elimination order, so the first field to empty is last. Any field still standing at 30:00 is ranked by living units, then kills, then slot. |
| Rerolls | 30 s cooldown **per tile**, paid in stone (item 3, element 3, hero 5, all 8, swap 5). Stone accrues +1 per kill and +1 per 10 s, cap 40. A tile action that cannot do anything — a hero reroll with no undrafted hero, a swap with one tile, any action for an eliminated player — is hidden rather than left as a dead button. |

Two invariants for all engine work: the engine stays a **generic RTS** (no Chaos Arena rules), and map-specific behaviour enters through metadata, `IGameAPI`, or map script — never through `if (mapName == ...)` branches.

---

## Engine workstream (Realm)

Ordered by dependency. Everything here is reusable by future maps.

### E1. Canonical stat keys, complete modifier wiring
`Realm.Ecs/Common/StatId.cs`, `Realm.Ecs/Services/AttributeStatCalculator.cs`, `Realm.Shared/Metadata/StatModifierDefinition.cs`

Problems found: `AccumulateModifiersForStat` only reads seven stats (MaxHp, HpRegen, MaxMana, ManaRegen, Armor, Damage, Speed). Modifiers keyed `MovementSpeed`, `Attack`, `CooldownReduction`, `SpellPower`, `LifeSteal`, `Evasion`, `MagicPenetration`, `ProcRateMultiplier`, `BountyMultiplier`, `Tenacity`, `SpellWard` are read by nothing. The map ships upgrades keyed `Attack` and `MovementSpeed`, both silently inert.

- One canonical key table plus a single explicit alias map (`Attack`→`Damage`, `MovementSpeed`→`Speed`, `Health`→`MaxHp`, `Mana`→`MaxMana`, …).
- Wiring for every derived stat that has a modifier slot.
- Unknown stat keys log a warning naming the source (upgrade/item/buff id) and are skipped — never silently applied to nothing.
- Done when: an upgrade keyed `Damage`, `Speed`, `CooldownReduction`, `SpellPower`, `LifeSteal`, `Evasion`, `MagicPenetration`, `ProcRateMultiplier` and `BountyMultiplier` each measurably change the derived component it names.

### E2. Buff → stat modifier bridge
`Realm.Client/Unit_WasmRuntime.cs`, `Realm.Client/Services/SimulationService.cs`, `Realm.Ecs/Common/BuffRegistry.cs`, `ModifierState`

`AddBuff` writes `Buffs`/`BuffState` and never contributes `BuffRegistry.BuffModifiers`, never recalculates. Bridge it: active buffs contribute registered modifiers with buff source identity (for later removal), recalculate on apply and on expiry, and survive replication as timed modifiers. Done when a `haste`-style buff visibly changes attack interval while active and restores on expiry.

### E3. Level → stats
`SimulationService.SetUnitLevel`, `UnitBaseStats`, metadata

Hero level is stored and never read. Add an authorable per-level growth table (attribute and stat growth per level) applied on level change and on spawn. Done when setting level 1 → 9 on a unit changes HP, damage and armor by the authored curve.

### E4. Resolver-backed scripted damage
`Realm.Ecs/Services/CombatResolver.cs`, `Realm.MapAPI/IGameAPI.cs`, `GameHost.MapAPI.cs`

Add `DealDamage(attacker, target, amount, damageType)` that routes through the three-stage resolver (armor, mitigation, damage record) instead of subtracting HP. Keep the existing raw path for legitimate bypass cases, documented. Done when script damage against a high-armor target is reduced and produces a damage record.

### E5. Ability effect system (largest task)
`Realm.Shared/Metadata/AbilityMetadata.cs`, `Realm.Client/Core/AbilityDefinition.cs`, `Realm.Client/Services/SimulationService.cs`, `Realm.Ecs/Services/*`, map schema + migration

Composable effects instead of `Damage XOR Healing`:

- Effect kinds: `damage`, `heal`, `shield`, `buff`, `debuff`, `status`, `summon`, `teleport`, `knockback`, `dispel`.
- Each effect carries its own magnitude, duration, radius, target filter and **scaling block** (primary attribute, shape, target, coefficient, cap).
- Targeting modes: self, single target, point, line, cone, aura — resolved through one query path.
- Summons get lifespan, count cap and ownership inheritance.
- Every field is either consumed or removed from the schema; no dead metadata.
- Sequential map migration plus build-version bump.
- Done when a hero can cast a damage+buff ability, a control ability, and a summon ability, all authored purely in metadata.

### E6. Autocast correctness
`AutoCastQueryAction` in `SimulationService`, `AbilityDefinition`

Today autocast reads the unit-type ability list, ignores `SetAbilityState(disabled/hidden)`, cannot see item-granted actives, and would fire all three element slots. Fix: per-ability autocast policy (enemy/ally, HP and mana thresholds, mana reserve), respects hidden/disabled, respects range/cooldown/mana, one cast attempt per ability per tick, deterministic order. Done when a wave clone with one enabled element slot casts exactly that ability and never the hidden ones.

### E7. Permanent modifiers for elixirs
`ModifierState`, tooltip/stat read paths

Permanent (duration ≤ 0) modifiers with source identity, visible through `GetModifiers` and a tooltip string, removable by source so the flush consumable works. Done when an elixir's effect survives wave cloning and can be cleared.

### E8. Map-level combat tuning block
`Realm.Shared/Metadata/MapMetadata.cs`, `CombatConfig.cs`

All coefficients, caps, floors and attribute conversion constants move to map-authorable configuration with defaults that preserve current behavior. Chaos Arena authors its own values here rather than in code.

---

## Map workstream (ugc-ChaosArena)

### M1. Content data set
Replace `metadata.json` units/abilities/items/upgrades with the 20/60/20/20/20 from `CORE_DATA_SET.md`. Regenerate `MapTemplates.g.cs`. Delete orphan templates and `MeleeRules.cs`.

### M2. Ability definitions and map tuning
Author all 60 abilities on the E5 schema, each with role tag, scaling block and caps. Author the map's E8 tuning block.

### M3. Circle UX
Circles hold three element-description abilities, one visible, plus the item icon with its stack count. No more 160-ability bars, no global `SetAbilityTooltip` writes.

### M4. Clone loadout
Each wave clone inherits level, items with stacks, element selection, elixir and team upgrades, and gets autocast enabled for exactly one ability.

### M5. Rerolls and swaps
Shipped in `MapScript.Rerolls.cs`. The tile owns the cooldown (kept in `TileState` so it survives the body being replaced) and charges the stone; the engine's own cooldown gate is per player per ability, so the map writes the caster's cooldown during the cast and the engine's check then passes over it. Ability visibility is refreshed after every change: inactive element abilities are hidden on a hero, a circle hides reroll-hero, one tile hides swap, and an eliminated player's tiles hide every action.

### M6. Altar — elixirs
Gold purchases, one per hero, permanent buff with tooltip, flush consumable, clone inheritance.

### M7. Blacksmith — 20 upgrades
Wood purchases, one-shot, applied to the proxy before each spawn.

### M8. Match end and economy
25:00 cutoff, per-wave survivor check, elimination order and placement display, stone income, damage/kill attribution to the real player, leaderboard pruning at wave end.

---

## Execution order

```
E1 → E2 → E3 → E4 → E6 → M1 → M3 → M5 → E7/M6 → M7 → M8
                          E5 ---^        ^
                          (E5 gates M2, and M2 gates M4)
```

E5 is the long pole. M1 can start once E1/E3 land because items and upgrades only need canonical keys and levels.

## Commit discipline

Every task = one commit in the repo it touches, pushed to `devin`. Engine and map commits are separate. Commit messages state the behaviour change, not the file list.

## Verification per task

- Engine: `dotnet build Realm.slnx` clean; targeted check of the changed behaviour (component values, resolver output, effect application) rather than compile-only.
- Map: `dotnet build MapScript.csproj` clean; in-game behaviour is confirmed by the user, since there is no headless harness for wave simulation yet.
- No stubs and no silent no-ops: a field that does nothing is a bug, not a deferral.

## Phase 2 (explicitly not beta)

Windup / hit-frame / backswing · projectile-carried attacks · multi-weapon units · crit sharing to splash · full CC diminishing-returns and dispel suite · damage-over-time / absorb / execute / vampirism matrix beyond E5's basics · deterministic replay RNG and divergence self-check · combat log and debug overlay · editor authoring UI for the new ability schema · competitive bot policy · additional families (Wind, Aether) and roles (Assassin, Bruiser).

Each is justified by "no beta content authors it" or "invisible in a 45-second auto-battle". Any that becomes necessary moves into beta with its own task.
