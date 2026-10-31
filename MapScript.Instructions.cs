namespace Realm.Maps;

public partial class MapScript
{
    private void RegisterInstructions()
    {
        _api.AddInstructionsSection(
            "How a Match Works",
            "Chaos Arena is an auto-battler for up to twelve players.\n\n"
            + "You build a board of nine Circles of Power, and the board fights for you. You never steer a battle: "
            + "every 45 seconds your board sends out a wave of clones and the wave decides the fight on its own.\n\n"
            + "A wave marches on a rival's base, then turns for the arena centre, where the survivors of every board meet.\n\n"
            + "The last player with units on the field wins.");

        _api.AddInstructionsSection(
            "Drafting Heroes",
            "Your board is nine Circles of Power. The first unlocks when the match starts and another at "
            + "[b]1:00, 2:00, 3:00, 5:00, 8:00, 12:00, 17:00 and 23:00[/b].\n\n"
            + "Three heroes are always on offer. Draft one with the Builder's [b]Build[/b] menu [B], or by selecting "
            + "a Circle of Power or the Altar.\n\n"
            + "A drafted hero stays on its circle and never fights there. It is the template: every wave clones it, and "
            + "the circle's level — its unlock order, 1 through 9 — is the level its clones start at.");

        _api.AddInstructionsSection(
            "Water, Earth and Fire",
            "Every circle holds one element. The element is a draft slot, not a school of magic: it decides which of "
            + "the hero's three abilities the clone casts.\n\n"
            + "Water, Earth and Fire each belong to every hero. Rerolling a circle's element moves it to a different "
            + "slot, and the ability shown on that tile changes with it.");

        _api.AddInstructionsSection(
            "Waves and the Arena",
            "Every [b]45 seconds[/b] each player's drafted heroes spawn as clones beside that player's base. Spawn "
            + "moments are staggered across the round, so waves reach the arena one after another rather than all at "
            + "once.\n\n"
            + "A clone carries its circle's stats, items and elixir. It attack-moves to the rival it was drawn against, "
            + "then turns for the centre once it arrives.\n\n"
            + "Waves stop at [b]25:00[/b].");

        _api.AddInstructionsSection(
            "Items",
            "Every circle carries one item. Items are pure stats, and stacks are literal copies: a two-modifier item "
            + "at five stacks applies each modifier five times.\n\n"
            + "Each clone inherits the item and its stack count from the circle it came from, so stacking items on a "
            + "circle equips every clone that circle will ever send.");

        _api.AddInstructionsSection(
            "Rerolls and Swaps",
            "A circle can reroll its item, its element or its hero, reroll all three at once, or swap its hero with "
            + "another circle. Every action is paid in [b]stone[/b] and then recharges for [b]30 seconds[/b] on that "
            + "tile:\n\n"
            + "[b]Reroll item[/b] — 3 stone\n"
            + "[b]Reroll element[/b] — 3 stone\n"
            + "[b]Reroll hero[/b] — 5 stone\n"
            + "[b]Reroll all[/b] — 8 stone\n"
            + "[b]Swap units[/b] — 5 stone\n\n"
            + "Stone arrives at 1 per kill and 1 every 10 seconds, up to 40.");

        _api.AddInstructionsSection(
            "Elixirs at the Altar",
            "Elixirs are bought with [b]gold[/b] at the Altar and cast onto one of your own heroes. Each elixir is a "
            + "permanent, scripted passive rather than a bundle of stats, and only the hero that took it is changed.\n\n"
            + "A hero carries one elixir at a time, and every clone it spawns inherits a fresh copy — an elixir paid "
            + "for once fights until the match ends.\n\n"
            + "To change it, buy the flush consumable. It clears the elixir the hero carries so another can be applied.");

        _api.AddInstructionsSection(
            "Blacksmith Upgrades",
            "Blacksmith upgrades are bought with [b]wood[/b], each one only once, and they are team-wide: every clone "
            + "you send from then on carries them.\n\n"
            + "Wood arrives at 1 every 5 seconds, so the full set is out of reach in one match. Buy the few that fit "
            + "the army you are building.");

        _api.AddInstructionsSection(
            "Victory and Placement",
            "Waves stop at [b]25:00[/b]. From then on your last wave is your match: a player whose fielded wave loses "
            + "its final unit is eliminated, and placement is the reverse of the order players fall.\n\n"
            + "At [b]30:00[/b] whatever is still standing is ranked by living units, then kills, then slot.\n\n"
            + "Winning is the only thing that ends the match early: be the last board with units on the field.");
    }
}
