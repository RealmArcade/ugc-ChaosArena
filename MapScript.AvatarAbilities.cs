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
    private const string SoulSiphonProjectileId = "weapon/shadow_kunai";
    private const float SoulSiphonProjectileSpeed = 25f;

    private const float HealingWaveRadius = 3.5f;
    private const float HealingWaveMissingHealthFraction = 0.25f;
    private const string HealingWaveProjectileId = "weapon/prism_shard";
    private const float HealingWaveProjectileSpeed = 40f;

    private void ConfigureAvatarAbilities()
    {
        _api.AddUnitTypeAbility(UnitIds.Builder, SoulSiphonAbilityId);
        _api.AddUnitTypeAbility(UnitIds.Builder, HealingWaveAbilityId);

        _api.SetAbilityTooltipValue(SoulSiphonAbilityId, "Threshold", SoulSiphonMaximumExecuteChance * 100f);
        _api.SetAbilityTooltipValue(SoulSiphonAbilityId, "Amount", SoulSiphonWoodPerKill);
        _api.SetAbilityTooltipValue(HealingWaveAbilityId, "Percent", HealingWaveMissingHealthFraction * 100f);
    }

    private bool TryCastSoulSiphon(UnitId caster, Vector3 targetPosition, int playerIndex)
    {
        if (!CanRunAvatarAbility(caster))
            return false;

        Vector3 castPosition = caster.Position;
        _api.SpawnProjectile(SoulSiphonProjectileId, castPosition, targetPosition, SoulSiphonProjectileSpeed);

        float travelSeconds = Vector3.Distance(castPosition, targetPosition) / SoulSiphonProjectileSpeed;
        _api.ScheduleTimer(MathF.Max(0.05f, travelSeconds),
            () => ResolveSoulSiphonImpact(caster, targetPosition, playerIndex));
        return true;
    }

    private bool TryCastHealingWave(UnitId caster, Vector3 targetPosition, int playerIndex)
    {
        if (!CanRunAvatarAbility(caster))
            return false;

        Vector3 castPosition = caster.Position;
        _api.SpawnProjectile(HealingWaveProjectileId, castPosition, targetPosition, HealingWaveProjectileSpeed);

        float travelSeconds = Vector3.Distance(castPosition, targetPosition) / HealingWaveProjectileSpeed;
        _api.ScheduleTimer(MathF.Max(0.05f, travelSeconds),
            () => ResolveHealingWaveImpact(targetPosition, playerIndex));
        return true;
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

    /// <summary>
    ///     OnSpellCast only fires after the engine's spell command has already validated the cast, spent the mana and armed
    ///     the ability cooldown, so the script must not charge or gate the cast a second time: doing so refused casts the
    ///     player could afford, double-spent mana and left a cooldown behind on a cast that never happened.
    /// </summary>
    private bool CanRunAvatarAbility(UnitId caster)
    {
        return caster.IsValid && !caster.IsDead;
    }
}
