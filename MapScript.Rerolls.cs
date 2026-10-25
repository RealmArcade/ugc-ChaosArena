namespace Realm.Maps;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Realm.MapAPI;

/// <summary>
/// Stone price, per-tile cooldown and ability visibility for rerolls and swaps.
///
/// Both live in the map rather than in ability metadata because the engine's cooldown gate is
/// per player per ability id: authoring a 30 s cooldown on "reroll_item" would grey that button
/// on every other tile the moment one tile rerolls. The map keeps the cooldown on the tile
/// instead, and answers the engine's own check with the value it just wrote — see
/// <see cref="TryPayTileAbility"/>.
/// </summary>
public partial class MapScript
{
    private const int TileAbilityCooldownSeconds = 30;

    /// <summary>
    /// Written instead of the real cooldown when a cast is refused. The engine applies its own
    /// cooldown after the script callback, and its fallback is a per-player 10 s that would lock
    /// the same button on every tile; a short positive value makes that check pass over instead,
    /// while still greying the button long enough to stop a refused cast being spammed.
    /// </summary>
    private const float RefusedCastCooldownSeconds = 0.5f;

    private const float RerollItemStoneCost = 3f;
    private const float RerollElementStoneCost = 3f;
    private const float RerollHeroStoneCost = 5f;
    private const float RerollAllStoneCost = 8f;
    private const float SwapTileStoneCost = 5f;

    private static float GetTileAbilityStoneCost(string abilitySlug)
    {
        if (SlugsEqual(abilitySlug, RerollItemAbilityId)) return RerollItemStoneCost;
        if (SlugsEqual(abilitySlug, RerollElementAbilityId)) return RerollElementStoneCost;
        if (SlugsEqual(abilitySlug, RerollHeroAbilityId)) return RerollHeroStoneCost;
        if (SlugsEqual(abilitySlug, RerollAllAbilityId)) return RerollAllStoneCost;
        if (SlugsEqual(abilitySlug, SwapTileUnitsAbilityId)) return SwapTileStoneCost;
        return 0f;
    }

    /// <summary>
    /// Charges the tile's stone and starts its cooldown. Returns false — charging nothing — when
    /// the tile is still recharging or the player cannot afford the reroll.
    /// </summary>
    private bool TryPayTileAbility(UnitId caster, TileState state, string spellId, int playerIndex)
    {
        string slug = StripSlugPrefix(spellId);
        float cost = GetTileAbilityStoneCost(slug);
        if (cost <= 0f)
            return true;

        PlayerId player = _api.GetPlayer(playerIndex);

        int remaining = GetTileAbilityCooldownRemainingSeconds(state, slug);
        if (remaining > 0)
        {
            player.SendMessage($"{_api.GetAbilityName(spellId)} is recharging: {remaining}s left.");
            WriteUnitAbilityCooldown(caster, slug, remaining);
            return false;
        }

        float stone = player.GetStone();
        if (stone < cost)
        {
            player.SendMessage(
                $"{_api.GetAbilityName(spellId)} costs {(int)cost} stone; you have {(int)stone}.");
            WriteUnitAbilityCooldown(caster, slug, RefusedCastCooldownSeconds);
            return false;
        }

        player.AdjustStone(-cost);

        state.AbilityReadyAtSeconds[slug] = gameSecondsElapsed + TileAbilityCooldownSeconds;
        WriteUnitAbilityCooldown(caster, slug, TileAbilityCooldownSeconds);
        return true;
    }

    private int GetTileAbilityCooldownRemainingSeconds(TileState state, string abilitySlug) =>
        state.AbilityReadyAtSeconds.TryGetValue(abilitySlug, out int readyAtSeconds)
            ? Math.Max(0, readyAtSeconds - gameSecondsElapsed)
            : 0;

    /// <summary>
    /// Writes the cooldown on the unit so the command card shows the sweep. The id has to be the
    /// one the card uses, which is the unprefixed metadata id, so it is resolved per unit rather
    /// than hard-coded.
    /// </summary>
    private void WriteUnitAbilityCooldown(UnitId unit, string abilitySlug, float seconds)
    {
        if (!unit.IsValid || unit.IsDead || seconds <= 0f)
            return;

        foreach (string abilityId in _api.GetUnitTypeAbilities(unit.TemplateID))
        {
            if (!SlugsEqual(abilityId, abilitySlug))
                continue;

            _api.SetAbilityCooldown(unit, abilityId, seconds);
            return;
        }
    }

    /// <summary>
    /// Re-applies whatever is still running after a tile was replaced by a fresh body (an element
    /// reroll or a hero reroll). Without it a respawn would reset the cooldown and the same tile
    /// could be rerolled again for stone alone.
    /// </summary>
    private void ReapplyTileAbilityCooldowns(UnitId unit, TileState state)
    {
        foreach (KeyValuePair<string, int> entry in state.AbilityReadyAtSeconds)
        {
            int remaining = entry.Value - gameSecondsElapsed;
            if (remaining > 0)
                WriteUnitAbilityCooldown(unit, entry.Key, remaining);
        }
    }

    private void CarryTileAbilityCooldowns(TileState target, TileState? source)
    {
        if (source == null)
            return;

        foreach (KeyValuePair<string, int> entry in source.AbilityReadyAtSeconds)
            target.AbilityReadyAtSeconds[entry.Key] = entry.Value;
    }

    private int CountPlayerTiles(int playerIndex)
    {
        int tileCount = 0;
        foreach (KeyValuePair<int, TileState> entry in tileStatesByUnitId)
        {
            if (entry.Value.PlayerIndex == playerIndex)
                tileCount++;
        }

        return tileCount;
    }

    private List<string> GetHeroRerollCandidates(int playerIndex)
    {
        HashSet<string> draftedUnitTypeIds = GetPlayerDraftedUnitTypeIds(playerIndex);
        return DraftableUnits.Where(id => !draftedUnitTypeIds.Contains(id)).ToList();
    }

    private bool HasHeroRerollCandidate(int playerIndex) => GetHeroRerollCandidates(playerIndex).Count > 0;

    private bool TryGetSwapTarget(UnitId caster, Vector3 targetPosition, int playerIndex, out UnitId target)
    {
        target = UnitId.Invalid;
        foreach (UnitId candidate in _api.GetUnitsInRadius(targetPosition, 2f))
        {
            if (!candidate.IsValid || candidate.Player != playerIndex)
                continue;

            if (candidate.UniqueId == caster.UniqueId || !tileStatesByUnitId.ContainsKey(candidate.UniqueId))
                continue;

            target = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Refreshes the draft offers hidden across a player's circles and then the reroll buttons on
    /// every tile. Called after anything that changes a tile's shape, because a replaced tile unit
    /// comes back with all of its abilities visible.
    /// </summary>
    private void RefreshPlayerTileAbilities(int playerIndex)
    {
        UpdateDraftOfferAbilities(playerIndex);
        RefreshTileAbilityVisibility(playerIndex);
    }

    /// <summary>
    /// Shows only the tile actions that can actually do something: a circle has no hero to reroll,
    /// a single tile has nothing to swap with, a hero reroll needs an undrafted hero to land on,
    /// and an eliminated player has no tiles left to play with.
    /// </summary>
    private void RefreshTileAbilityVisibility(int playerIndex)
    {
        if (!playerStates.TryGetValue(playerIndex, out PlayerState? playerState))
            return;

        if (_api.GetPlayer(playerIndex).IsComputer)
            return;

        bool tilesLocked = matchComplete || playerState.Eliminated;
        bool canSwap = CountPlayerTiles(playerIndex) >= 2;
        bool canRerollHero = HasHeroRerollCandidate(playerIndex);

        foreach (KeyValuePair<int, TileState> entry in tileStatesByUnitId)
        {
            if (entry.Value.PlayerIndex != playerIndex)
                continue;

            UnitId unit = (UnitId)entry.Key;
            if (!unit.IsValid || unit.IsDead)
                continue;

            bool isHero = entry.Value.Kind == TileKind.Hero;

            SetTileAbilityHidden(unit, RerollItemAbilityId, tilesLocked);
            SetTileAbilityHidden(unit, RerollElementAbilityId, tilesLocked);
            SetTileAbilityHidden(unit, RerollAllAbilityId, tilesLocked);
            SetTileAbilityHidden(unit, RerollHeroAbilityId, tilesLocked || !isHero || !canRerollHero);
            SetTileAbilityHidden(unit, SwapTileUnitsAbilityId, tilesLocked || !canSwap);
        }
    }

    private void SetTileAbilityHidden(UnitId unit, string abilityId, bool hidden) =>
        _api.SetAbilityState(unit, abilityId, hidden, hidden);
}
