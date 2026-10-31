namespace Realm.Maps;

using System;
using System.Collections.Generic;
using System.Numerics;
using Realm.MapAPI;

public enum ElixirHook
{
    HitDealt,
    HitTaken,
    Kill,
    Cast,
    WaveStart,
    Always
}

public sealed record ElixirDefinition(
    string Id,
    ElixirHook Hook,
    float Amount,
    int MaxStacks)
{
    public string AbilityId => $"ability/{Id}";
    public string SourceId => $"elixir/{Id}";
}

public sealed class ElixirState
{
    public ElixirDefinition Definition { get; init; } = null!;
    public int PlayerIndex { get; set; }
    public int Stacks { get; set; }
    public int Procs { get; set; }
    public float Accumulator { get; set; }
}

public partial class MapScript
{
    private static readonly ElixirDefinition[] Elixirs =
    {
        new("elixir_blood_debt", ElixirHook.HitDealt, 0.4f, 30),
        new("elixir_siphon_sigil", ElixirHook.HitDealt, 5f, 25),
        new("elixir_rending_edge", ElixirHook.HitDealt, 45f, 5),
        new("elixir_executioner", ElixirHook.HitDealt, 12f, 20),
        new("elixir_warded_hide", ElixirHook.HitTaken, 0.25f, 16),
        new("elixir_retaliation", ElixirHook.HitTaken, 20f, 10),
        new("elixir_phantom_step", ElixirHook.HitTaken, 0.02f, 10),
        new("elixir_soul_harvest", ElixirHook.Kill, 0.04f, 10),
        new("elixir_trophy_hunter", ElixirHook.Kill, 1f, 20),
        new("elixir_feeding_frenzy", ElixirHook.Kill, 1f, 15),
        new("elixir_void_pact", ElixirHook.Kill, 18f, 20),
        new("elixir_arcane_echo", ElixirHook.Cast, 10f, 6),
        new("elixir_swift_incantation", ElixirHook.Cast, 1.5f, 8),
        new("elixir_ritual_echo", ElixirHook.Cast, 30f, 5),
        new("elixir_opening_gambit", ElixirHook.WaveStart, 4f, 1),
        new("elixir_vanguards_oath", ElixirHook.WaveStart, 8f, 1),
        new("elixir_slow_regeneration", ElixirHook.Always, 2f, 90),
        new("elixir_battle_trance", ElixirHook.Always, 1.5f, 8),
        new("elixir_iron_constitution", ElixirHook.Always, 22f, 10),
        new("elixir_flush", ElixirHook.Always, 0f, 0),
    };

    private static readonly Dictionary<string, ElixirDefinition> ElixirById = BuildElixirLookup();

    private readonly Dictionary<int, ElixirState> elixirStatesByUnitId = new();

    private static Dictionary<string, ElixirDefinition> BuildElixirLookup()
    {
        var lookup = new Dictionary<string, ElixirDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (ElixirDefinition elixir in Elixirs)
        {
            lookup[StripSlugPrefix(elixir.Id)] = elixir;
            lookup[StripSlugPrefix(elixir.AbilityId)] = elixir;
        }

        return lookup;
    }

    private static bool TryResolveElixir(string spellId, out ElixirDefinition definition)
    {
        definition = null!;
        if (string.IsNullOrWhiteSpace(spellId))
            return false;

        return ElixirById.TryGetValue(StripSlugPrefix(spellId), out definition!);
    }

    private void ConfigureAltarAbilities()
    {
        foreach (ElixirDefinition elixir in Elixirs)
        {
            _api.AddUnitTypeAbility(BuildingIds.Altar, elixir.AbilityId);
            PublishElixirTooltipValues(elixir);
        }

    }

    private void PublishElixirTooltipValues(ElixirDefinition elixir)
    {
        _api.SetAbilityTooltipValue(elixir.AbilityId, "Amount", elixir.Amount);
        _api.SetAbilityTooltipValue(elixir.AbilityId, "Cap", elixir.Amount * elixir.MaxStacks);
        _api.SetAbilityTooltipValue(elixir.AbilityId, "Procs", elixir.MaxStacks);
    }

    private void HandleConsumableChanged(UnitId unit, string consumableId)
    {
        if (!unit.IsValid)
            return;

        int playerIndex = ResolveRealPlayerIndex(unit.Player);

        if (string.IsNullOrEmpty(consumableId))
        {
            ClearElixir(unit);
            _api.GetPlayer(playerIndex).SendMessage($"{unit.Name} no longer carries an elixir.");
            return;
        }

        if (!TryResolveElixir(consumableId, out ElixirDefinition definition))
            return;

        ApplyElixir(unit, definition, playerIndex);
        _api.GetPlayer(playerIndex).SendMessage(
            $"{_api.GetAbilityName(definition.AbilityId)} bound to {unit.Name}. Every wave clone it spawns inherits it.");
    }

    private void ApplyElixir(UnitId unit, ElixirDefinition definition, int playerIndex)
    {
        var state = new ElixirState { Definition = definition, PlayerIndex = playerIndex };
        elixirStatesByUnitId[unit.UniqueId] = state;

        if (tileStatesByUnitId.TryGetValue(unit.UniqueId, out TileState? tileState))
            tileState.ElixirId = definition.Id;
    }

    private void ClearElixir(UnitId unit)
    {
        if (!elixirStatesByUnitId.TryGetValue(unit.UniqueId, out ElixirState? state))
            return;

        _api.RemoveStatModifiers(unit, state.Definition.SourceId);
        elixirStatesByUnitId.Remove(unit.UniqueId);

        if (tileStatesByUnitId.TryGetValue(unit.UniqueId, out TileState? tileState))
            tileState.ElixirId = null;
    }

    private void RemoveElixirState(UnitId unit) => elixirStatesByUnitId.Remove(unit.UniqueId);

    private void ApplyInheritedElixir(UnitId clone, TileState? tileState)
    {
        if (tileState?.ElixirId == null)
            return;

        if (!TryResolveElixir(tileState.ElixirId, out ElixirDefinition definition))
            return;

        ApplyElixir(clone, definition, ResolveRealPlayerIndex(clone.Player));
        OnElixirWaveStart(clone);
    }

    private bool _elixirHookActive;
    private readonly List<(UnitId Attacker, UnitId Target, float Damage, string DamageType)> _pendingElixirDamage = new();

    private void QueueElixirDamage(UnitId attacker, UnitId target, float damage, string damageType)
    {
        if (damage <= 0f || !target.IsValid || target.IsDead)
            return;

        _pendingElixirDamage.Add((attacker, target, damage, damageType));
    }

    private void FlushElixirDamage()
    {
        if (_pendingElixirDamage.Count == 0)
            return;

        var pending = _pendingElixirDamage.ToArray();
        _pendingElixirDamage.Clear();

        _elixirHookActive = true;
        try
        {
            foreach ((UnitId attacker, UnitId target, float damage, string damageType) in pending)
            {
                if (!target.IsValid || target.IsDead)
                    continue;

                _api.DealTypedDamage(attacker, target, damage, damageType);
            }
        }
        finally
        {
            _elixirHookActive = false;
        }
    }

    private void RunElixirDamageHooks(UnitId victim, UnitId attacker, float damage)
    {
        if (_elixirHookActive || elixirStatesByUnitId.Count == 0)
            return;

        _elixirHookActive = true;
        try
        {
            if (attacker.IsValid)
                OnElixirHitDealt(attacker, victim, damage);

            OnElixirHitTaken(victim, attacker);
        }
        finally
        {
            _elixirHookActive = false;
        }
    }

    private void OnElixirHitDealt(UnitId attacker, UnitId victim, float damage)
    {
        if (!elixirStatesByUnitId.TryGetValue(attacker.UniqueId, out ElixirState? state))
            return;

        if (state.Definition.Hook != ElixirHook.HitDealt)
            return;

        switch (state.Definition.Id)
        {
            case "elixir_blood_debt":
                if (state.Stacks >= state.Definition.MaxStacks) return;
                state.Stacks++;
                SetElixirModifier(attacker, state, "Attack", false, state.Definition.Amount * state.Stacks);
                break;

            case "elixir_siphon_sigil":
                if (state.Procs >= state.Definition.MaxStacks) return;
                state.Procs++;
                _api.HealUnit(attacker, state.Definition.Amount);
                break;

            case "elixir_rending_edge":
                state.Stacks++;
                if (state.Stacks % 6 != 0) return;
                if (state.Procs >= state.Definition.MaxStacks) return;
                state.Procs++;
                QueueElixirDamage(attacker, victim, state.Definition.Amount, "physical");
                break;

            case "elixir_executioner":
                if (state.Procs >= state.Definition.MaxStacks) return;
                if (victim.MaxHealth <= 0f || victim.Health / victim.MaxHealth >= 0.35f) return;
                state.Procs++;
                QueueElixirDamage(attacker, victim, state.Definition.Amount, "physical");
                break;
        }
    }

    private void OnElixirHitTaken(UnitId victim, UnitId attacker)
    {
        if (!elixirStatesByUnitId.TryGetValue(victim.UniqueId, out ElixirState? state))
            return;

        if (state.Definition.Hook != ElixirHook.HitTaken)
            return;

        switch (state.Definition.Id)
        {
            case "elixir_warded_hide":
                if (state.Stacks >= state.Definition.MaxStacks) return;
                state.Stacks++;
                SetElixirModifier(victim, state, "Armor", false, state.Definition.Amount * state.Stacks);
                break;

            case "elixir_retaliation":
                if (state.Procs >= state.Definition.MaxStacks) return;
                if (!attacker.IsValid || attacker.IsDead) return;
                state.Procs++;
                QueueElixirDamage(victim, attacker, state.Definition.Amount, "physical");
                break;

            case "elixir_phantom_step":
                if (state.Stacks >= state.Definition.MaxStacks) return;
                state.Stacks++;
                SetElixirModifier(victim, state, "MovementSpeed", true, state.Definition.Amount * state.Stacks);
                break;
        }
    }

    private void OnElixirKill(UnitId killer)
    {
        if (!killer.IsValid || !elixirStatesByUnitId.TryGetValue(killer.UniqueId, out ElixirState? state))
            return;

        if (state.Definition.Hook != ElixirHook.Kill)
            return;

        switch (state.Definition.Id)
        {
            case "elixir_soul_harvest":
                if (state.Procs >= state.Definition.MaxStacks) return;
                state.Procs++;
                _api.HealUnit(killer, killer.MaxHealth * state.Definition.Amount);
                break;

            case "elixir_trophy_hunter":
                if (state.Procs >= state.Definition.MaxStacks) return;
                state.Procs++;
                _api.GetPlayer(state.PlayerIndex).AdjustGold(state.Definition.Amount);
                break;

            case "elixir_feeding_frenzy":
                if (state.Stacks >= state.Definition.MaxStacks) return;
                state.Stacks++;
                SetElixirModifier(killer, state, "Attack", false, state.Definition.Amount * state.Stacks);
                break;

            case "elixir_void_pact":
                if (state.Stacks >= state.Definition.MaxStacks) return;
                state.Stacks++;
                float gained = state.Definition.Amount;
                SetElixirModifier(killer, state, "MaxHp", false, gained * state.Stacks);
                killer.Health = MathF.Min(killer.MaxHealth, killer.Health + gained);
                break;
        }
    }

    private void OnElixirCast(UnitId caster, string spellId)
    {
        if (!elixirStatesByUnitId.TryGetValue(caster.UniqueId, out ElixirState? state))
            return;

        if (state.Definition.Hook != ElixirHook.Cast)
            return;

        switch (state.Definition.Id)
        {
            case "elixir_arcane_echo":
                state.Stacks++;
                if (state.Stacks % 5 != 0) return;
                if (state.Procs >= state.Definition.MaxStacks) return;
                state.Procs++;
                caster.Mana = MathF.Min(caster.MaxMana, caster.Mana + state.Definition.Amount);
                break;

            case "elixir_swift_incantation":
                if (state.Procs >= state.Definition.MaxStacks) return;
                state.Procs++;
                foreach (string abilityId in _api.GetUnitTypeAbilities(caster.TemplateID))
                {
                    if (SlugsEqual(abilityId, spellId)) continue;
                    float cooldown = _api.GetAbilityCooldown(caster, abilityId);
                    if (cooldown <= 0f) continue;
                    _api.SetAbilityCooldown(caster, abilityId, MathF.Max(0f, cooldown - state.Definition.Amount));
                }

                break;

            case "elixir_ritual_echo":
                state.Stacks++;
                if (state.Stacks % 4 != 0) return;
                if (state.Procs >= state.Definition.MaxStacks) return;
                state.Procs++;
                ElixirPulse(caster, state.Definition.Amount);
                break;
        }
    }

    private void ElixirPulse(UnitId caster, float damage)
    {
        const float radius = 4f;
        int casterPlayer = caster.Player;

        foreach (UnitId candidate in _api.GetUnitsInRadius(caster.Position, radius))
        {
            if (!candidate.IsValid || candidate.IsDead) continue;
            if (candidate.UniqueId == caster.UniqueId) continue;
            if (candidate.Invulnerable) continue;
            if (_api.ArePlayersAllied(casterPlayer, candidate.Player)) continue;

            QueueElixirDamage(caster, candidate, damage, "magic");
        }
    }

    private void SetElixirModifier(UnitId unit, ElixirState state, string statName, bool isPercentage, float total)
    {
        _api.RemoveStatModifiers(unit, state.Definition.SourceId);
        _api.AddStatModifier(unit, statName, isPercentage, total, state.Definition.SourceId);
    }

    private void OnElixirWaveStart(UnitId unit)
    {
        if (!elixirStatesByUnitId.TryGetValue(unit.UniqueId, out ElixirState? state))
            return;

        if (state.Definition.Hook != ElixirHook.WaveStart)
            return;

        _api.AddStatModifier(
            unit,
            state.Definition.Id == "elixir_opening_gambit" ? "Armor" : "Attack",
            false,
            state.Definition.Amount,
            state.Definition.SourceId);
    }

    private void HandleElixirTick()
    {
        FlushElixirDamage();

        if (elixirStatesByUnitId.Count == 0)
            return;

        List<int>? expired = null;

        foreach (KeyValuePair<int, ElixirState> entry in elixirStatesByUnitId)
        {
            UnitId unit = (UnitId)entry.Key;
            if (!unit.IsValid || unit.IsDead)
            {
                (expired ??= new List<int>()).Add(entry.Key);
                continue;
            }

            ElixirState state = entry.Value;
            if (state.Definition.Hook != ElixirHook.Always)
                continue;

            switch (state.Definition.Id)
            {
                case "elixir_slow_regeneration":
                    if (state.Procs >= state.Definition.MaxStacks) continue;
                    state.Procs++;
                    _api.HealUnit(unit, state.Definition.Amount);
                    break;

                case "elixir_battle_trance":
                    state.Accumulator++;
                    if (state.Accumulator < 10f) continue;
                    state.Accumulator = 0f;
                    if (state.Stacks >= state.Definition.MaxStacks) continue;
                    state.Stacks++;
                    SetElixirModifier(unit, state, "Attack", false, state.Definition.Amount * state.Stacks);
                    break;

                case "elixir_iron_constitution":
                    state.Accumulator++;
                    if (state.Accumulator < 6f) continue;
                    state.Accumulator = 0f;
                    if (state.Stacks >= state.Definition.MaxStacks) continue;
                    state.Stacks++;
                    SetElixirModifier(unit, state, "MaxHp", false, state.Definition.Amount * state.Stacks);
                    unit.Health = MathF.Min(unit.MaxHealth, unit.Health + state.Definition.Amount);
                    break;
            }
        }

        if (expired == null)
            return;

        foreach (int unitId in expired)
            elixirStatesByUnitId.Remove(unitId);
    }
}
