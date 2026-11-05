namespace Realm.Maps;

using System;
using System.Numerics;
using Realm.MapAPI;

public partial class MapScript
{
    private const string SoulSiphonAbilityId = "ability/soul_siphon";
    private const string HealingWaveAbilityId = "ability/healing_wave";

    private const float SoulSiphonRadius = 4f;
    private const float SoulSiphonMaximumExecuteChance = 0.25f;
    private const float SoulSiphonWoodPerKill = 5f;
    private const float SoulSiphonStonePerKill = 1f;

    private const float HealingWaveRadius = 3.5f;
    private const float HealingWaveMissingHealthFraction = 0.25f;

    private void ConfigureAvatarAbilities()
    {
        _api.AddUnitTypeAbility(UnitIds.Builder, SoulSiphonAbilityId);
        _api.AddUnitTypeAbility(UnitIds.Builder, HealingWaveAbilityId);

        _api.SetAbilityTooltipValue(SoulSiphonAbilityId, "Threshold", SoulSiphonMaximumExecuteChance * 100f);
        _api.SetAbilityTooltipValue(SoulSiphonAbilityId, "Amount", SoulSiphonWoodPerKill);
        _api.SetAbilityTooltipValue(HealingWaveAbilityId, "Percent", HealingWaveMissingHealthFraction * 100f);
    }

    /// <summary>
    ///     The abilities on the Avatar fly their own projectile, named by the ability template's ProjectileId, so the
    ///     engine owns the flight and tells the map where it landed. Nothing here has to know a speed or how long the
    ///     trip takes.
    /// </summary>
    private void HandleSpellImpact(UnitId caster, string abilityId, Vector3 position, int playerIndex)
    {
        if (SlugsEqual(abilityId, SoulSiphonAbilityId))
        {
            ResolveSoulSiphonImpact(caster, position, ResolveRealPlayerIndex(playerIndex));
            return;
        }

        if (SlugsEqual(abilityId, HealingWaveAbilityId))
        {
            ResolveHealingWaveImpact(position, ResolveRealPlayerIndex(playerIndex));
        }
    }

    private void ResolveSoulSiphonImpact(UnitId caster, Vector3 targetPosition, int playerIndex)
    {
        PlayerId player = _api.GetPlayer(playerIndex);
        int executions = 0;

        foreach (UnitId target in _api.GetUnitsInRadius(targetPosition, SoulSiphonRadius))
        {
            if (!target.IsValid || target.IsDead || target.Invulnerable)
                continue;

            if (caster.IsValid && target.UniqueId == caster.UniqueId)
                continue;

            if (IsFriendlySource(caster, target))
                continue;

            if (target.MaxHealth <= 0f)
                continue;

            float healthFraction = Math.Clamp(target.Health / target.MaxHealth, 0f, 1f);
            if (healthFraction >= 1f)
                continue;

            float executeChance = SoulSiphonMaximumExecuteChance * (1f - healthFraction);
            if (_api.RandomFloat(0f, 1f) > executeChance)
                continue;

            if (caster.IsValid && !caster.IsDead)
                _api.DealTypedDamage(caster, target, target.Health + 1f, "pure");
            else
                _api.KillUnit(target);

            executions++;
        }

        if (executions == 0)
        {
            player.SendMessage("Soul Siphon found no soul within reach.");
            return;
        }

        float wood = executions * SoulSiphonWoodPerKill;
        float stone = executions * SoulSiphonStonePerKill;
        player.AdjustWood(wood);
        player.AdjustStone(stone);
        player.SendMessage($"Soul Siphon executed {executions}, recovered {(int)wood} wood and {(int)stone} stone.");
    }

    private void ResolveHealingWaveImpact(Vector3 targetPosition, int playerIndex)
    {
        int healed = 0;

        foreach (UnitId target in _api.GetUnitsInRadius(targetPosition, HealingWaveRadius))
        {
            if (!target.IsValid || target.IsDead)
                continue;

            if (ResolveRealPlayerIndex(target.Player) != playerIndex)
                continue;

            float missingHealth = MathF.Max(0f, target.MaxHealth - target.Health);
            if (missingHealth <= 0f)
                continue;

            _api.HealUnit(target, missingHealth * HealingWaveMissingHealthFraction);
            healed++;
        }

        if (healed == 0)
            _api.GetPlayer(playerIndex).SendMessage("Healing Wave found nothing to mend.");
    }
}
