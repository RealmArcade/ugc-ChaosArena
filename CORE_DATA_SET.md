# Chaos Arena — Core Data Set

The beta content set for the map. Targets: **20 heroes, 3 abilities each (60), 20 reroll items, 20 elixirs, 20 blacksmith upgrades.** Nothing else ships in beta.

Design authority: this file. Numeric tuning is deferred to a later balance pass; what is fixed here is *concept, role, uniqueness and scaling shape*.

---

## 1. "Family" — what the word means

A **family** is an art and identity group, not a rules group. It is the shared model library, palette, weapon set and animation vocabulary that a hero is built from. Ten families exist in the source data:

| Family | Look / feel | Bodies available |
|---|---|---|
| Iron | heavy plate, siege hardware | Stone Guardian, Iron Warlord, Vanguard Sentinel, Bulwark Automaton, Shieldwall Marshal |
| Shadow | hooded, void-touched, tattered | Nether Assassin, Shadow Strider, Shadow Weaver, Bone Choir, Wraithbinder, Hollow Reaper |
| Grove | bark, thorn, fungal | Verdant Druid, Thornback Behemoth, Grove Stalker, Spore Matron, Pack Alpha |
| Abyss | reef plate, brine, harpoon | Coral Vanguard, Abyssal Warden, Tidecaller, Deepwater Colossus, Saltspray Raider |
| Fire | ember, ash, molten | Flame Archon, Cinder Hound, Magma Smith, Ashwing, Pyre Zealot |
| Frost | rime, glacier, ice-crystal | Frost Warden, Glacier Sentinel, Rimeweaver, Winterfang, Hoarfrost Oracle |
| Storm | wind, dust, static | Storm Caller, Tempest Chaser, Windblade Dancer, Skyrend Harrier, Dune Skirmisher |
| Arcane | scholar, sigil, prism | Arcane Scholar, Crystal Weaver, Chrono Weaver, Rune Scribe, Prism Savant |
| Holy | sun, prayer, consecration | Sun Priest, Lightbringer Monk, Astral Paragon, Dawnblade Herald, Sanctum Guardian |
| Beast | feral, hulking, pack | Pack Alpha, Cinder Hound, Razor Dervish (shared with Iron/Shadow) |

Family carries **no mechanical meaning**: no family bonuses, no family resistances, no family ability rules. It exists so the draft feels varied and so future families can be added without touching rules.

---

## 2. Roster — 20 heroes, two per family

Bodies are taken verbatim from `metadata_alternate.json` (HP / damage / armor / range / weapon swing). 13 already ship in the map; 7 are new and fill real gaps.

| # | Hero | Family | Body | Kit hook |
|---|---|---|---|---|
| 1 | Stone Guardian | Iron | 1295 / 2.6 heavy / 2.44 / 1.6 s | immovable anchor |
| 2 | Iron Warlord | Iron | 1062 / 2 heavy / 2.82 / 1.5 s | cleave bruiser |
| 3 | Nether Assassin | Shadow | 549 / 1 light / 2.88 / 0.95 s | single-target execution |
| 4 | Shadow Weaver | Shadow | 556 / 0.9 medium / 3.98 / 1.2 s | curse caster |
| 5 | Verdant Druid | Grove | 823 / 1.6 light / 3.26 / 1.25 s | regen support |
| 6 | Thornback Behemoth | Grove | 1260 / 2.6 light / 2.5 / 1.4 s | thorns tank *(new)* |
| 7 | Coral Vanguard | Abyss | 1013 / 2 medium / 3.24 / 1.3 s | pull / displace |
| 8 | Tidecaller | Abyss | 650 / 1.1 medium / 6.1 / 1.2 s | long-range zoner *(new)* |
| 9 | Flame Archon | Fire | 538 / 0.9 light / 5.28 / 1.4 s | burst mage |
| 10 | Magma Smith | Fire | 994 / 2 light / 2.7 / 1.4 s | fire bruiser *(new)* |
| 11 | Frost Warden | Frost | 636 / 0.9 medium / 5.2 / 1.1 s | control mage |
| 12 | Glacier Sentinel | Frost | 1283 / 2.6 medium / 2.8 / 1.25 s | supertank *(new)* |
| 13 | Storm Caller | Storm | 540 / 0.9 unarmored / 5.14 / 1.05 s | chain damage |
| 14 | Tempest Chaser | Storm | 685 / 1.3 unarmored / 4.36 / 1.15 s | mobile skirmisher |
| 15 | Arcane Scholar | Arcane | 547 / 0.9 unarmored / 5.2 / 1.3 s | burst caster |
| 16 | Rune Scribe | Arcane | 633 / 1.1 unarmored / 6.4 / 1.2 s | siege caster *(new)* |
| 17 | Sun Priest | Holy | 829 / 1.6 medium / 4.6 / 1.15 s | healer |
| 18 | Lightbringer Monk | Holy | 1036 / 2 medium / 2.8 / 0.85 s | fast support bruiser |
| 19 | Pack Alpha | Beast | 977 / 2 light / 2.8 / 1.4 s | pack buffer *(new)* |
| 20 | Razor Dervish | Beast | 599 / 1 light / 2.6 / 1.05 s | spin AoE *(new)* |

Dropped: 31 alternate heroes, 40 existing map abilities, 7 unused weapons, all orphan scripts (`fireball`, `holylight`, `lightning`, `ancestral_spirit`).

---

## 3. Abilities — 60, engine-implemented

Implemented by the **core engine effect system**, not by map script one-offs. The engine stays a generic RTS: the effect vocabulary, scaling and targeting are generic; the map supplies the 60 definitions and any mode-specific tuning.

### 3.1 Slot semantics
Water / Earth / Fire are **draft slots only**. Each hero has three independently designed abilities. No cross-hero rule binds a slot to a role, a damage type or a status. Future slots (Wind, Aether) are added the same way.

### 3.2 Role tags
Every ability declares exactly one primary role:

`Damage` · `Control` · `Support` · `Tank` · `Mobility` · `Summon` · `Economy`

A hero's three abilities must differ in **role or shape**. Cross-hero duplicates are forbidden on (effect + delivery + shape).

### 3.3 Scaling — one primary attribute, one shape
Every ability scales from exactly one of **Strength, Agility, Vitality, Intelligence, Wisdom, Fortune**, using one of these shapes:

| Shape | Formula | Used for |
|---|---|---|
| Linear | `base + K × stat` | flat damage, flat heal, flat shield |
| Ratio | `base × (1 + stat / D)` | durations, radii, summons |
| Saturating | `cap × stat / (stat + K)` | damage reduction, resist, cooldown reduction |
| Threshold | step at `stat ≥ t1 / t2 / t3` | conditional effects, extra bounces/targets |
| Inverse | `base / (1 + stat / K)` | cooldowns, charge times |
| Conversion | `stat × K` into a secondary pool | shields from Vitality, mana from Wisdom |

Each ability documents: `primaryAttribute`, `scalingShape`, `scalingTarget` (Damage, Healing, Duration, Radius, Cooldown, Targets, SummonCount, Charges), `coefficient`, `cap`.

### 3.4 Balanceability rule
**No binary abilities.** Every effect must expose magnitude, a duration or count cap, and a cooldown. A resurrection is therefore a summon with a lifespan, a cooldown and a per-match cap — not an ultimate. This rule is enforced during authoring review.

### 3.5 Roster kits (role per slot)

| Hero | Water | Earth | Fire |
|---|---|---|---|
| Stone Guardian | Granite Fist — Damage | Hardened Crust — Tank | Seismic Rupture — Control |
| Iron Warlord | Cleaver Arc — Damage | War Engine Roar — Support | Earthsplitter — Control |
| Nether Assassin | Nether Sting — Damage | Shadow Mark — Control | Silent Verdict — Mobility |
| Shadow Weaver | Dusk Bolt — Damage | Void Bloom — Control | Devouring Dark — Support |
| Verdant Druid | Thorn Lash — Damage | Verdant Surge — Support | Bloomtide — Tank |
| Thornback Behemoth | Bramble Slam — Damage | Rooted Vigor — Tank | Overgrowth — Control |
| Coral Vanguard | Harpoon Jab — Damage | Tidal Sweep — Control | Undertow — Mobility |
| Tidecaller | Wave Dart — Damage | Riptide Crush — Control | Maelstrom — Damage (AoE) |
| Flame Archon | Ember Lance — Damage | Firestorm — Control | Pyroclasm — Damage (AoE) |
| Magma Smith | Forge Hammer — Damage | Molten Quench — Tank | Eruption — Control |
| Frost Warden | Rime Bolt — Damage | Frost Bulwark — Tank | Glacier Break — Control |
| Glacier Sentinel | Ice Hammer — Damage | Permafrost — Tank | Avalanche — Control |
| Storm Caller | Static Bolt — Damage | Thunder Clap — Control | Stormfront — Support |
| Tempest Chaser | Quick Slash — Damage | Gust Step — Mobility | Cyclone Rush — Damage (AoE) |
| Arcane Scholar | Arcane Lance — Damage | Mana Bloom — Support | Arcane Cataclysm — Damage (AoE) |
| Rune Scribe | Rune Dart — Damage | Sigil Detonation — Control | Grand Inscription — Support |
| Sun Priest | Solar Spark — Damage | Sunlit Mend — Support | Radiant Chorus — Support |
| Lightbringer Monk | Palm Strike — Damage | Inner Light — Support | Sanctified Ground — Tank |
| Pack Alpha | Savage Maul — Damage | Howl — Support | Blood Hunt — Mobility |
| Razor Dervish | Whirling Cut — Damage | Bleeding Waltz — Control | Thousand Edge — Damage (multi-hit) |

Three heroes intentionally repeat a role across slots (Tidecaller, Flame Archon, Sun Priest, Razor Dervish) because their shapes differ: single-target vs zone vs aura vs multi-hit.

---

## 4. Items — 20, pure stats

Equipped on a Circle, inherited by the drafted hero and by every wave clone.

- **1 to 3 modifiers**, with 2 the most common (distribution: 3 × three-mod, 13 × two-mod, 4 × one-mod).
- Positive and **negative** modifiers are both allowed; negatives buy a larger positive. No item may have a negative that its own effect cannot justify.
- **Stacking = copies.** Tile level means literally N copies of the item, so a two-mod item at 5 stacks applies each modifier 5×. No formula, no escalation curve.
- **No scripted passives** on items in beta.
- No duplicate items across one player's nine tiles.

Composition targets: 8 offense, 5 defense, 4 hybrid, 3 risky (big positive + real negative).

---

## 5. Elixirs — 20 consumables, scripted permanent passives

Purchased at the **Altar with gold**, consumed by a board hero. Renamed from "Relics".

- Each elixir applies a **different scripted passive formula** — a permanent combat behaviour change, not a stat bundle. Examples: `+1 max HP per hit taken`, `−1% ability cooldown per hit dealt`, `+0.5% damage per kill this wave`, `heal 1% max HP on kill`, `+2 armor while below 50% HP`.
- Formula, trigger hook and cap live in map data. Hooks: `OnHitDealt`, `OnHitTaken`, `OnKill`, `OnCast`, `OnWaveStart`, `Always`.
- Applies **only to the hero that consumes it**; every future wave clone inherits it.
- **Max one elixir per unit**, displayed as a permanent buff with a tooltip.
- A separate **flush** consumable clears the current elixir so a different one can be applied. The flush is one of the 20, not a free action.
- Every elixir needs a hard cap so it cannot scale unbounded across a long match.

---

## 6. Blacksmith — 20 one-shot team upgrades

Wood-purchased, bought once each, team-wide, applied to wave clones. No tiers.

**Offense (8):** Sharpened Blades (+6 flat attack, 45w) · Brutal Edge (+10% attack, 95w) · Swift Hands (+7% attack speed, 70w) · Deadly Precision (+5% crit chance, 80w) · Piercing Rounds (+3 armor pen, 55w) · Arcane Resonance (+8 spell power, 65w) · Overload (+0.2 proc rate, 50w) · Arcane Piercing (+0.1 magic pen, 70w)

**Defense (6):** Reinforced Plate (+3 armor, 50w) · Bulwark (+12% max HP, 90w) · Second Wind (+1.5 HP regen, 45w) · Spell Ward (+0.08 spell ward, 60w) · Tenacity (+0.08 tenacity, 55w) · Evasion Drill (+0.06 evasion, 70w)

**Utility (5):** Forced March (+6% move speed, 50w) · Battle Rhythm (+0.08 cooldown reduction, 75w) · Bloodthirst (+0.05 lifesteal, 65w) · Ritual Focus (+25 max mana, 40w) · Field Medic (+12% healing, 60w)

**Economy (1):** Bounty Ledger (+20% gold per kill, 65w)

The full set costs 1,255 wood, against roughly 300 wood of passive income plus kill wood in a
25-minute match, so a player buys about five of the twenty. Every entry is one modifier on one
canonical stat key; `Scavenger` (wood income) and `War Industry` (gold income) were dropped because
they duplicated `Bounty Ledger` and had no stat key of their own.

Each upgrade is one engine stat modifier on a canonical stat key — no new mechanics.

---

## 7. Rerolls and swaps

All reroll and swap abilities: **30 s per-unit cooldown**, paid in **stone**. Cost bands: reroll item 3, reroll element 3, reroll hero 5, reroll all 8, swap 5. No mana cost.

---

## 8. Deleted content

- 40 shipped hero abilities (the 45 dmg / 30 heal / 90 dmg reskins).
- 4 orphan spells: `fireball`, `holylight`, `lightning`, `ancestral_spirit`.
- `ability/upgrade_critical_strike` research branch (the blacksmith panel drives upgrades).
- 40 unused item templates, 130 unused upgrades, 31 unused heroes, 7 unused weapons.
- `MeleeRules.cs` (never enabled by this map).
- Generated `MapTemplates.g.cs` constants that no longer match metadata (regenerate in the tooling task).
