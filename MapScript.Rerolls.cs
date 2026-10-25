namespace Realm.Maps;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Realm.MapAPI;

public partial class MapScript
{
    private const int TileAbilityCooldownSeconds = 30;

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

    private void RefreshPlayerTileAbilities(int playerIndex)
    {
        UpdateDraftOfferAbilities(playerIndex);
        RefreshTileAbilityVisibility(playerIndex);
    }

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
