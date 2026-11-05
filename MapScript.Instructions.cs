namespace Realm.Maps;

public partial class MapScript
{
    private void RegisterInstructions()
    {
        _api.AddInstructionsSection(
            "How a Match Works",
            "Chaos Arena is an auto-battler for up to twelve players.\n\n"
            + "You draft a board of nine heroes and the board fights for you."
            + "Periodically your board sends out a wave of clones to battle for ownership of the center.\n\n"
            + "Near the end of the game, it switches to sudden death mode, where new soldiers are no longer spawned. The last player with units on the field wins.");

        _api.AddInstructionsSection(
            "Drafting Heroes",
            "Each circle is a draft location with a pre-defined loadout including a hero level, an inventory item, and an elemental power (Earth, Water, Fire)."
            + "You can draft additional heroes periodically throughout the match, maxing at 9 heroes.\n\n"
            + "Three heroes are offered each draft. Pick one with the Avatar's [b]Build[/b] menu [B] and place it onto an elemental circle\n\n"
            + "A drafted hero stays on its circle and never moves. It is the template: every wave clones it and enters the battle.");

        _api.AddInstructionsSection(
            "Elements (Water, Earth and Fire)",
            "Every circle holds one element. The element determines which of "
            + "the hero's three abilities the clone casts.");

        _api.AddInstructionsSection(
            "Waves and the Arena",
            "Periodically, each player's drafted heroes spawn as clones beside that player's base and march towards a rival. Spawn "
            + "timings and selected rival are randomized and staggered across the round to ensure you're always fighting someone new.\n\n"
            + "A clone carries its circle's stats, items, upgrades, and elixir.");

        _api.AddInstructionsSection(
            "Hero Levels",
            "Heroes do not gain experience. Their level is predefined based on the circle they were drafted onto.\n\n"
            + "Higher levels means more raw stats, each hero scales differently.");

        _api.AddInstructionsSection(
            "Items",
            "Every circle grants one item to the drafted hero. Items are pure stats and they scale with the hero level.\n\n"
            + "Items can be rerolled");

        _api.AddInstructionsSection(
            "Rerolls and Swaps",
            "A circle can reroll its item, its element or its hero, reroll all three at once. Swapping locations between heroes keeps their loadouts but also swaps their experience level."
            + "Every action is paid in [b]stone[/b] and then recharges for [b]30 seconds[/b] on that "
            + "tile:\n\n"
            + "[b]Reroll item[/b] — 10 stone\n"
            + "[b]Reroll element[/b] — 25 stone\n"
            + "[b]Reroll hero[/b] — 10 stone\n"
            + "[b]Reroll all[/b] — 5 stone\n"
            + "[b]Swap units[/b] — 5 stone\n\n"
            + "Stone arrives at 1 every 10 seconds, and every kill your Avatar lands with Soul Siphon brings 1 more.");

        _api.AddInstructionsSection(
            "Elixirs at the Altar",
            "Elixirs are bought with [b]gold[/b] at the Altar and cast onto one of your own heroes. Each elixir is a "
            + "permanent, scripted passive rather than a bundle of stats, and only the hero that took it is changed.\n\n"
            + "A hero carries one elixir at a time, and every clone it spawns inherits a fresh copy — elixirs last "
            + "until the match ends.\n\n"
            + "To change it, buy the flush consumable. It clears the elixir the hero carries so another can be applied.\n\n"
            + "Gold arrives at 1 for whichever player lands the killing blow on a unit, and nothing else pays it.");

        _api.AddInstructionsSection(
            "Blacksmith Upgrades",
            "Blacksmith upgrades are bought with [b]wood[/b], each one only once, and they are team-wide: every clone "
            + "you send from then on carries them.\n\n"
            + "Wood arrives at 1 every 5 seconds, and every kill your Avatar lands with Soul Siphon brings 5 more, so "
            + "the full set is out of reach in one match. Buy the few that fit the army you are building.");

        _api.AddInstructionsSection(
            "Strategy",
            "Heroes are cloned in the same pattern as the drafting grid."
            + "Put defensive units on the flanks to protect the damage dealers."
            + "Draft a mix of roles and damage types for a well-rounded composition."
            + "Elements unlock 1 of 3 abilities for a hero, which can completely change that hero's role."
            + "Purchase blacksmith upgrades that benefit your overall team composition."
            + "Consider a hero's ability scalings to pick the ideal item and elixir.");

        _api.AddInstructionsSection(
            "Victory and Rank",
            "Sudden death occurs towards the end of the match. Afterwards, you no longer spawn new waves"
            + "Last player with units alive wins 1st place with each subsequent player receiving the next rank.");
    }
}
