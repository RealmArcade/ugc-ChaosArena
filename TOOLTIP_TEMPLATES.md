# Tooltip templates — design

## The problem, stated precisely

Every number a player reads exists in up to three places today:

1. **Authored prose.** `metadata.json` → `AbilityMetadata.Description` → `"Each hit this unit lands grants +0.4 attack damage, up to +12. 40 gold."`
2. **UI code that formats the same fact.** `UIHelper.GetUnitCostTooltip` builds `"Cost: {gold} Gold, {wood} Wood, {pop} Pop"` from metadata; `CommandPanel` builds `"Buy {name} (Cost: {gold} Gold) — {desc}"` and the blacksmith cost line the same way.
3. **The math.** `ElixirDefinition(Amount: 0.4f, MaxStacks: 30, CostGold: 40f)` in the map, `AbilityEffectService` for engine abilities, `AttributeStatCalculator` for stats.

The altar bug was the extreme case — the card said `+12`, the code applied `+186` because of triangular stacking. The ordinary case is cheaper and more common: change `CostGold` from 40 to 45 and every card that says "40 gold" is now lying. We have 60 abilities, 20 items, 40 upgrades, 19 elixirs and 20 heroes, so the ordinary case happens constantly during the balance pass.

Templates fix the *text* side of that. Two other rules fix the rest, and without them templating is cosmetic:

- **Stats blocks are rendered, never authored.** An item's or upgrade's numeric lines come from its `Modifiers` list. There is no prose to drift.
- **A tooltip can only print numbers a gameplay function returns.** The tooltip calls the same resolve path the cast calls.

## Recommendation in one line

Resolve `{{Scope.Key}}` tokens against a small, explicit property bag **at hover time**, where the base game fills the scope from the ECS and definitions, and the map script fills it for anything the base game cannot know.

## Syntax

```
{{Key}}                     sugar for {{Source.Key}}
{{Source.Armor}}            one scope, one key
{{Ability.Amount:+0.#}}     with a .NET numeric format
{{Owner.Gold:0}}
{{Source.Name}}             text values allowed
\{{not a token}}            escaped literal
```

Rules:

- **Scopes are closed.** `Source`, `Target`, `Ability`, `Item`, `Upgrade`, `Owner`, `Map`. An unknown scope is an authoring error, not a fallback.
- **A bare key means `Source`.** Authors write `{{Armor}}` in a unit card and `{{Ability.Damage}}` in an ability card. Documented, one-way sugar.
- **Formatting is explicit and culture-invariant.** Default format `0.##`, so `0.4000001` prints `0.4` and `12` prints `12`. Percent stats are stored as `0..1`, so `{{Source.CritChance:0%}}` renders `15%` — the `%` specifier multiplies by 100, which is the number a player expects.
- **No expressions.** No arithmetic, conditionals, or fallbacks in the token. If a card needs a computed sentence, that is a hint the sentence belongs in a published key (`{{Ability.Refund}}`) rather than in the template.

## Where the values come from

### Base game fills the scopes

| Scope | Keys | Source of truth |
|---|---|---|
| `Source` | every canonical `StatKeys` name: `Armor`, `Attack`, `MaxHp`, `SpellPower`, `CooldownReduction`, … | `StatService.GetStatValue(entity, StatId)` — the same snapshot the combat resolver and the AI read |
| `Source` | `Health`, `HealthPercent`, `Mana`, `ManaPercent`, `Level`, `Stacks`, `Count`, `Name` | components, read directly |
| `Target` | the same set | the currently hovered or selected enemy, if any |
| `Ability` | `ManaCost`, `Cooldown`, `Range`, `Radius`, `Damage`, `Healing` | the runtime `AbilityDefinition` |
| `Ability` | `Effect{N}.Amount`, `.Duration`, `.Radius`, `.Length`, `.Angle`, `.MaxTargets`, `.Coefficient`, `.Divisor`, `.Cap`, `.ModifierCount`, `.Modifier{M}`, and the text `.Attribute` | the authored effect list, indexed in cast order, so a card describes the entries the cast reads |
| `Item` / `Upgrade` | `CostGold`, `CostWood`, `CostStone`, `Charges`, `ItemLevel`, `MaxLevel`, `ModifierCount` | the runtime definition |
| `Owner` | `Gold`, `Wood`, `Stone`, `Kills`, `Wave`, `TimeRemaining`, `Alive`, `Eliminated` | the player snapshot the HUD already reads |

The important choice: **bind to canonical stat keys, not to component fields.** Reflection over `DerivedCombatStats` would be shorter to write, and it is wrong — it would let a tooltip read a field gameplay does not use, which is the exact drift we are removing. `StatKeys` is already the one vocabulary the pipeline, the resolver and `WarnIfUnknown` share; tooltips should read it too.

Percent stats (`CritChance`, `Evasion`, `CooldownReduction`, `LifeSteal`, `Tenacity`, `BountyMultiplier`…) are all stored as fractions. Document that on the scope, once, so no author guesses.

### The map fills what the base game cannot know

One value per call, because the wasm type generator only marshals primitives — arrays of strings or floats cannot cross the boundary, so a batched `SetAbilityTooltipValues(id, string[], float[])` does not compile:

```csharp
void SetAbilityTooltipValue(string abilityId, string key, float value);
void SetAbilityTooltipText(string abilityId, string key, string value);
void SetUnitTooltipValue(UnitId unit, string key, float value);
void SetUnitTooltipText(UnitId unit, string key, string value);
void SetItemTooltipValue(string itemId, string key, float value);
void SetUpgradeTooltipValue(string upgradeId, string key, float value);
```

A map publishes its handful of values once at load, so the crossing count is bounded by the number of scripted effects, not by hover. These merge into the scope named by the call; **engine keys win a collision**, so a map cannot redefine `Source.Armor` and quietly disagree with the resolver.

This is where the elixirs land. The record stays the single execution source:

```csharp
new("elixir_blood_debt", "elixir_blood_debt", ElixirHook.HitDealt, 0.4f, 30, 40f)
```

and at init the script publishes `Amount`, `Cap` (`Amount × MaxStacks`), `Cost` for each. The card, authored once in metadata, reads:

> `Each hit this unit lands grants {{Ability.Amount:+0.#}} attack damage, up to {{Ability.Cap:+0.#}}. {{Item.CostGold:0}} gold. One elixir per hero.`

Change `0.4f` in the record and the card is correct on the next load, with no second edit anywhere. Change `CostGold` and the card is correct because the cost line is generated from metadata, not typed.

### The one piece that is generated, not templated

Items and blacksmith upgrades are pure stat packages: 1–3 modifiers, a price, and flavor text. Their numeric block should be **rendered from `Modifiers`** — `ModifierValue` formatted with its sign, `ModifierType.Percentage` printed as a percent, negatives in the warning colour, `MaxLevel` shown as a stack count. Authors write flavor only. There is no template and no prose number to fall out of date, and a modifier added to an item appears on its card without anyone touching a string.

This is the single highest-value part of the design: it deletes the possibility of drift for 60 of the map's 139 authored cards instead of detecting it.

## Where resolution happens

At **hover**, not when the card is built.

```
metadata Description (template)
  → AbilityDefinition.TooltipTemplate            stored raw, never substituted
  → CommandCardItem.TooltipTemplate + TooltipContext
  → CommandCardButton._MakeCustomTooltip()
  → TooltipTemplate.Resolve(template, context)
  → RichTooltip.Create(resolved)
```

`CommandCardButton._MakeCustomTooltip` is already where Godot asks for the text, and it already receives a string. Give the button the compiled template and the context instead of a finished string, and resolve inside that override. Godot calls it once when the hover begins, for one control, so this costs nothing measurable — and it makes every live value correct for free: current armor, stacks, remaining cooldown, current mana, the circle's current element, whether the player can still afford it.

The alternative — resolve when populating the card and cache the string — is what the code does today, and it is why the tooltip can be stale in the same frame it was written. There is no invalidation scheme that is cheaper than just resolving on hover.

`TooltipContext` is a small struct (source entity, target entity, ability/item/upgrade id, player index). Copying it into each `CommandCardItem` is free.

### Two files, two jobs

- `TooltipTemplate.cs` (new, `Realm.Client/UI/`): compile, cache, resolve, diagnose. No Godot dependency beyond logging, so it can be exercised headlessly — which matters because we have no Godot test harness and the template parser is exactly the kind of thing that needs one.
- `RichTooltip.cs` (existing): the panel, the margin, the BBCode conversion. It gains one overload, `Create(string text, TooltipContext context)`, that resolves first and then renders.

Putting the parser inside `RichTooltip` would tie it to a `Control` subclass and make it untestable. Everything else about the suggestion works.

## Failure policy

Silent substitution failure is worse than a missing feature, because it makes a card lie while looking fine.

- **Unknown scope or key:** render the token in warning colour (`<color=#ff6b6b>{{Path}}</color>`) and log once per `(template, path)`. One line per unique mistake, not per frame.
- **Missing value in a scope that exists** (e.g. `Target.*` with nothing hovered): same treatment, because a card that promises a target number is wrong whenever there is no target.
- **Formatting failure:** fall back to the default format and log the bad specifier.
- **Release builds:** no red text; render `?` and keep the log. Players see a hole, developers see the cause.

## Enforcement — why this actually stops the drift

1. **Abilities print magnitudes the cast uses.** `AbilityEffectService` exposes the resolved magnitude per effect for a given (caster, target); the tooltip scope calls it, the cast calls it. One function, two callers.
2. **Script-applied numbers are published from the fields that are applied.** `ElixirDefinition` keeps `Amount`/`MaxStacks`/`CostGold` and the tooltip reads exactly those.
3. **Items and upgrades render their modifier list** instead of describing it.
4. **Cost lines are generated.** Replace `UIHelper.GetUnitCostTooltip`'s hand-built `"Cost: {g} Gold"` and the two cost strings in `CommandPanel` with one shared renderer over `Item`/`Upgrade`/`Cost.*` scopes. Three sites, one format.
5. **Validation at authoring time.** The map editor and a map-repo CI script both walk every description, resolve it against a synthetic context that reports *any* missing key, and fail on unknown tokens. The CI script also warns when an ability description contains a digit that no token produced — advisory, because some prose legitimately has numbers, but it catches the "I edited the number in the sentence" habit.
6. **Localization order is fixed:** translate the template first, substitute second. Translators must preserve `{{…}}` tokens; the validator checks that a translation contains the same token set as its source.

## Migration plan

**T1 — needed for beta**

1. `TooltipTemplate` (parse, compile-cache, resolve, diagnostics) + `TooltipContext` + `RichTooltip.Create(text, context)`.
2. Wire the four hover surfaces: `CommandCardButton` (abilities, build, train), `CommandPanel` item cards, upgrade cards, `InventoryPanel` and `PortraitPanel`.
3. Generated stat blocks for items and upgrades.
4. `IGameAPI` value publishing (single-value setters, see above) with binary and XML refresh.
5. Convert the 60 ability descriptions and 19 elixir descriptions to templates; publish elixir amounts from the record.
6. Validator in the map editor and in the map repo's build; fix every token it reports.

**T2 — after beta**

A `Map` scope for values that belong to no ability (wave number, remaining time) · `Target` scope and damage previews computed through the resolver (`"deal 96 to {{Target.Armor}} armor"`, `"stuns {{Target.Tenacity:0%}}"`), conditionals for "if the target is below 35%", pluralization, tooltip diffing in the editor, and deleting `SetAbilityTooltip`/`SetItemTooltip`'s string overrides once nothing needs them.

## What I would not do

- **No reflection over components.** Bind to `StatKeys` and a published bag.
- **No build-time substitution.** That is today's snapshot bug with extra machinery.
- **No arithmetic in templates.** Publish the computed value instead; a template that can do math can also disagree with the code.
- **No map-supplied formatted strings.** Publish `12`, not `"+12"`, or the format lives in two places again.
- **No `{{Health}}` without a defined owner.** Bare keys mean `Source`; `Target` is always explicit. Ambiguity here produces the wrong number, not a missing one, which is worse.
