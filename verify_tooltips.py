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

# Must stay in step with MapScript.Elixirs.cs: the script publishes these four values per elixir.
ELIXIR_KEYS = ("Amount", "Cap", "Procs", "Cost")
ELIXIR_SOURCE = {
    "elixir_blood_debt": (0.4, 30, 40),
    "elixir_siphon_sigil": (5, 25, 30),
    "elixir_rending_edge": (45, 5, 45),
    "elixir_executioner": (12, 20, 50),
    "elixir_warded_hide": (0.25, 16, 35),
    "elixir_retaliation": (20, 10, 40),
    "elixir_phantom_step": (0.02, 10, 30),
    "elixir_soul_harvest": (0.04, 10, 45),
    "elixir_trophy_hunter": (1, 20, 30),
    "elixir_feeding_frenzy": (1, 15, 45),
    "elixir_void_pact": (18, 20, 40),
    "elixir_arcane_echo": (10, 6, 40),
    "elixir_swift_incantation": (1.5, 8, 45),
    "elixir_ritual_echo": (30, 5, 50),
    "elixir_opening_gambit": (4, 1, 35),
    "elixir_vanguards_oath": (8, 1, 40),
    "elixir_slow_regeneration": (2, 90, 35),
    "elixir_battle_trance": (1.5, 8, 50),
    "elixir_iron_constitution": (22, 10, 45),
    "elixir_flush": (0, 0, 10),
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


def elixir_values(template_id):
    slug = template_id.split("/", 1)[1]
    if slug not in ELIXIR_SOURCE:
        return None
    amount, stacks, cost = ELIXIR_SOURCE[slug]
    return {
        "Amount": amount,
        "Cap": amount * stacks,
        "Procs": stacks,
        "Cost": cost,
        "Name": slug.replace("elixir_", "").replace("_", " ").title(),
    }


def resolve(description, values):
    missing = []

    def substitute(match):
        body = match.group(1)
        path, _, spec = body.partition(":")
        path = path.strip()
        if path.startswith("Ability."):
            path = path[len("Ability."):]
        if path not in values:
            missing.append(path)
            return match.group(0)
        value = values[path]
        if isinstance(value, str):
            return value
        return dotnet_format(value, spec.strip() or None)

    return TOKEN.sub(substitute, description), missing


def main():
    with open(SRC, encoding="utf-8-sig") as handle:
        metadata = json.load(handle)

    show = "--print" in sys.argv
    problems = []
    checked = 0

    for ability in metadata["Templates"]["Abilities"]:
        description = ability.get("Description") or ""
        template_id = ability["TemplateID"]
        if "{{" not in description:
            continue

        values = elixir_values(template_id) or effect_values(ability)
        resolved, missing = resolve(description, values)
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
