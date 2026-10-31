"""Resolve every tooltip template in metadata.json against the map's own data.

This is the map's authoring check: a template that names a field the effect list does not have,
or an elixir value the script does not publish, fails here instead of showing a hole in game.

Run from the map root. Pass --print to see the resolved cards.
"""

import json
import re
import sys
from decimal import Decimal, ROUND_HALF_UP

SRC = "metadata.json"
TOKEN = re.compile(r"\{\{(.+?)\}\}")

# Must stay in step with MapScript.Elixirs.cs: the script publishes the amount and stack cap per
# elixir, while the purchase price is authored in Templates.Consumables.
ELIXIR_SOURCE = {
    "elixir_blood_debt": (0.4, 30),
    "elixir_siphon_sigil": (5, 25),
    "elixir_rending_edge": (45, 5),
    "elixir_executioner": (12, 20),
    "elixir_warded_hide": (0.25, 16),
    "elixir_retaliation": (20, 10),
    "elixir_phantom_step": (0.02, 10),
    "elixir_soul_harvest": (0.04, 10),
    "elixir_trophy_hunter": (1, 20),
    "elixir_feeding_frenzy": (1, 15),
    "elixir_void_pact": (18, 20),
    "elixir_arcane_echo": (10, 6),
    "elixir_swift_incantation": (1.5, 8),
    "elixir_ritual_echo": (30, 5),
    "elixir_opening_gambit": (4, 1),
    "elixir_vanguards_oath": (8, 1),
    "elixir_slow_regeneration": (2, 90),
    "elixir_battle_trance": (1.5, 8),
    "elixir_iron_constitution": (22, 10),
    "elixir_flush": (0, 0),
}

# Values the script publishes for abilities that are handled in code rather than by effect data.
SCRIPTED_ABILITIES = {
    "ability/soul_siphon": {"Threshold": 30, "Amount": 1, "Cap": 3},
    "ability/healing_wave": {"Percent": 25},
}


def dotnet_format(value, spec):
    """Formats the way the C# resolver does for the specifiers the cards use."""
    spec = spec or "0.##"
    percent = spec.endswith("%")
    if percent:
        spec = spec[:-1]
    if spec.startswith("+"):
        spec = spec[1:]
        sign = "+" if value >= 0 else ""
    else:
        sign = ""

    decimals = 2 if spec == "0.##" else 1
    scaled = Decimal(str(value)) * (100 if percent else 1)
    text = str(scaled.quantize(Decimal(1).scaleb(-decimals), rounding=ROUND_HALF_UP))
    if "." in text:
        text = text.rstrip("0").rstrip(".")
    return sign + text + ("%" if percent else "")


def effect_values(ability):
    """Key -> value for the fields the ability scope exposes, keyed without the Ability. prefix."""
    values = {}
    effects = ability.get("Effects") or []

    for index, effect in enumerate(effects):
        base = f"Effect{index}."
        scal = effect.get("Scaling") or {}
        for field, value in (
            ("Amount", effect.get("Amount")),
            ("Duration", effect.get("Duration")),
            ("Radius", effect.get("Radius")),
            ("Length", effect.get("Length")),
            ("Angle", effect.get("AngleDegrees")),
            ("MaxTargets", effect.get("MaxTargets")),
            ("SummonCount", effect.get("SummonCount")),
            ("SummonCap", effect.get("SummonCap")),
            ("Coefficient", scal.get("Coefficient")),
        ):
            if value:
                values[base + field] = value
        if scal.get("Divisor"):
            values[base + "Divisor"] = scal["Divisor"]
        if scal.get("Cap"):
            values[base + "Cap"] = scal["Cap"]
        for m_index, modifier in enumerate(effect.get("Modifiers") or []):
            values[f"{base}Modifier{m_index}"] = modifier.get("Value") or 0
        values[base + "ModifierCount"] = len(effect.get("Modifiers") or [])
        if scal.get("Attribute"):
            values[base + "Attribute"] = scal["Attribute"]

    values["ManaCost"] = ability.get("ManaCost") or 0
    values["Cooldown"] = ability.get("Cooldown") or 0
    values["Range"] = ability.get("TargetRange") or 0
    values["Radius"] = ability.get("AreaOfEffectRadius") or 0
    values["Name"] = ability.get("Name") or ""
    return values


def elixir_values(template_id, consumables):
    slug = template_id.split("/", 1)[1]
    if slug not in ELIXIR_SOURCE:
        return None

    amount, stacks = ELIXIR_SOURCE[slug]
    purchase = consumables.get(template_id) or {}
    return {
        "Amount": amount,
        "Cap": amount * stacks,
        "Procs": stacks,
        "Cost": purchase.get("CostGold", 0),
        "Name": slug.replace("elixir_", "").replace("_", " ").title(),
    }


def consumable_values(consumables, ability_id):
    purchase = consumables.get(ability_id)
    if not purchase:
        return None

    return {
        "Cost": purchase.get("CostGold", 0),
        "CostGold": purchase.get("CostGold", 0),
        "CostWood": purchase.get("CostWood", 0),
        "CostStone": purchase.get("CostStone", 0),
        "MaxPerUnit": purchase.get("MaxPerUnit", 0),
        "Range": purchase.get("PurchaseRange", 0),
        "SlotGroup": purchase.get("SlotGroup", ""),
    }


def resolve(description, values, scoped=None):
    missing = []
    scoped = scoped or {}

    def substitute(match):
        body = match.group(1)
        path, _, spec = body.partition(":")
        path = path.strip()
        scope = None
        if "." in path:
            scope, _, path = path.partition(".")

        if scope is not None and scope not in ("Ability", "Consumable"):
            missing.append(match.group(1))
            return match.group(0)

        active = values if scope != "Consumable" else scoped
        if active is None or path not in active:
            missing.append(match.group(1))
            return match.group(0)

        value = active[path]
        if isinstance(value, str):
            return value
        return dotnet_format(value, spec.strip() or None)

    return TOKEN.sub(substitute, description), missing


def main():
    with open(SRC, encoding="utf-8-sig") as handle:
        metadata = json.load(handle)

    consumables = {
        entry.get("AbilityId") or entry["TemplateID"]: entry
        for entry in metadata["Templates"].get("Consumables", [])
    }

    show = "--print" in sys.argv
    problems = []
    checked = 0

    for ability in metadata["Templates"]["Abilities"]:
        description = ability.get("Description") or ""
        template_id = ability["TemplateID"]
        if "{{" not in description:
            continue

        values = elixir_values(template_id, consumables) or effect_values(ability)
        for key, value in SCRIPTED_ABILITIES.get(template_id, {}).items():
            values[key] = value

        resolved, missing = resolve(description, values, consumable_values(consumables, template_id))
        checked += 1

        if missing:
            problems.append((template_id, sorted(set(missing))))
            continue

        if show:
            print(f"{template_id}\n   {resolved}\n")

        if "%" in resolved and "--" in resolved:
            problems.append((template_id, ["suspicious double sign"]))

        if "{{" in resolved:
            problems.append((template_id, ["unresolved token"]))

    print(f"checked {checked} cards, {len(problems)} problems")
    for template_id, detail in problems:
        print(f"  {template_id}: {detail}")

    if problems:
        sys.exit(1)


if __name__ == "__main__":
    main()
