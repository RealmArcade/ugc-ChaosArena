namespace Realm.Maps;

using System;
using System.Collections.Generic;
using System.Numerics;
using Realm.MapAPI;

/// <summary>
/// When an elixir's passive is allowed to fire. The hook decides which map callback drives it.
/// </summary>
public enum ElixirHook
{
    HitDealt,
    HitTaken,
    Kill,
    Cast,
    WaveStart,
    Always
}

/// <summary>
/// One altar elixir. The hook and the cap live here so an effect cannot scale past its budget,
/// and every value an author would want to tune is on this record rather than buried in a switch.
/// </summary>
public sealed record ElixirDefinition(
    string Id,
    string Name,
    string Description,
    ElixirHook Hook,
    float Amount,
    int MaxStacks,
    float CostGold)
{
    public string AbilityId => $"ability/{Id}";
    public string SourceId => $"elixir/{Id}";
}

/// <summary>
/// Live state for the elixir a unit is carrying. Clones get a fresh state at spawn, so stacks
/// never compound across waves.
/// </summary>
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
    private const float ElixirPurchaseRadius = 4f;

    private static readonly ElixirDefinition[] Elixirs =
    {
        new("elixir_blood_debt", "Blood Debt",
            "Each hit this unit lands grants +0.4 attack damage, up to +12.", ElixirHook.HitDealt, 0.4f, 30, 40f),
        new("elixir_siphon_sigil", "Siphon Sigil",
            "Each hit this unit lands heals it 5 health, up to 25 heals per wave.", ElixirHook.HitDealt, 5f, 25, 30f),
        new("elixir_rending_edge", "Rending Edge",
            "Every sixth hit lands an extra 45 damage, up to 5 times per wave.", ElixirHook.HitDealt, 45f, 5, 45f),
        new("elixir_executioner", "Executioner",
            "Hits against targets below 35% health deal 12 extra damage, up to 20 times per wave.", ElixirHook.HitDealt, 12f, 20, 50f),
        new("elixir_warded_hide", "Warded Hide",
            "Each hit taken grants +0.25 armor, up to +4 armor.", ElixirHook.HitTaken, 0.25f, 16, 35f),
        new("elixir_retaliation", "Retaliation",
            "Each hit taken deals 20 damage back to the attacker, up to 10 times per wave.", ElixirHook.HitTaken, 20f, 10, 40f),
        new("elixir_phantom_step", "Phantom Step",
            "Each hit taken grants +2% movement speed, up to +20%.", ElixirHook.HitTaken, 0.02f, 10, 30f),
        new("elixir_soul_harvest", "Soul Harvest",
            "Kills heal 4% of maximum health, up to 10 kills per wave.", ElixirHook.Kill, 0.04f, 10, 45f),
        new("elixir_trophy_hunter", "Trophy Hunter",
            "Kills pay 1 extra gold, up to 20 kills per wave.", ElixirHook.Kill, 1f, 20, 30f),
        new("elixir_feeding_frenzy", "Feeding Frenzy",
            "Each kill grants +1 attack damage, up to +15.", ElixirHook.Kill, 1f, 15, 45f),
        new("elixir_void_pact", "Void Pact",
            "Each kill grants +18 maximum health, up to +360.", ElixirHook.Kill, 18f, 20, 40f),
        new("elixir_arcane_echo", "Arcane Echo",
            "Every fifth cast refunds 10 mana, up to 6 refunds per wave.", ElixirHook.Cast, 10f, 6, 40f),
        new("elixir_swift_incantation", "Swift Incantation",
            "Each cast cuts the caster's other cooldowns by 1.5 seconds, up to 8 times per wave.", ElixirHook.Cast, 1.5f, 8, 45f),
        new("elixir_ritual_echo", "Ritual Echo",
            "Every fourth cast erupts for 30 magic damage around the caster, up to 5 times per wave.", ElixirHook.Cast, 30f, 5, 50f),
        new("elixir_opening_gambit", "Opening Gambit",
            "Wave units start with +4 armor for the whole wave.", ElixirHook.WaveStart, 4f, 1, 35f),
        new("elixir_vanguards_oath", "Vanguard's Oath",
            "Wave units start with +8 attack damage for the whole wave.", ElixirHook.WaveStart, 8f, 1, 40f),
        new("elixir_slow_regeneration", "Slow Regeneration",
            "Regenerates 2 health per second, up to 180 health per wave.", ElixirHook.Always, 2f, 90, 35f),
        new("elixir_battle_trance", "Battle Trance",
            "Every 10 seconds alive grants +1.5 attack damage, up to +12.", ElixirHook.Always, 1.5f, 8, 50f),
        new("elixir_iron_constitution", "Iron Constitution",
            "Every 6 seconds alive grants +22 maximum health, up to +220.", ElixirHook.Always, 22f, 10, 45f),
        new("elixir_flush", "Flush",
            "Clears the elixir this hero carries so a different one can be applied.", ElixirHook.Always, 0f, 0, 10f),
    };

    private static readonly Dictionary<string, ElixirDefinition> ElixirById = BuildElixirLookup();
    private static readonly string FlushElixirId = "elixir_flush";

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

    /// <summary>
    /// Puts the elixir shop on the altar. The altar is a building, so the same ability-bar paging
    /// the command card already does for buildings handles the twenty entries.
    /// </summary>
    private void ConfigureElixirsOnAltar()
    {
        foreach (ElixirDefinition elixir in Elixirs)
            _api.AddUnitTypeAbility(BuildingIds.Altar, elixir.AbilityId);
    }

    /// <summary>
    /// Handles an elixir ability cast from the altar. The player targets one of their own board
    /// heroes; the elixir is paid for in gold and replaces nothing but an empty slot.
    /// </summary>
    private bool TryBuyElixir(UnitId altar, string spellId, Vector3 targetPosition, int playerIndex)
    {
        if (!altar.IsValid || !TryResolveElixir(spellId, out ElixirDefinition elixir))
            return false;

        PlayerId player = _api.GetPlayer(playerIndex);
        bool isFlush = string.Equals(elixir.Id, FlushElixirId, StringComparison.OrdinalIgnoreCase);

        UnitId target = FindElixirTarget(playerIndex, targetPosition);
        if (!target.IsValid)
        {
            player.SendMessage($"{elixir.Name}: target one of your own board heroes.");
            return true;
        }

        bool hasElixir = elixirStatesByUnitId.ContainsKey(target.UniqueId);

        if (isFlush)
        {
            if (!hasElixir)
            {
                player.SendMessage("Flush: that hero is not carrying an elixir.");
                return true;
            }

            if (player.Gold < elixir.CostGold)
            {
                player.SendMessage($"Flush costs {elixir.CostGold:F0} gold.");
                return true;
            }

            player.AdjustGold(-elixir.CostGold);
            ClearElixir(target);
            player.SendMessage("Elixir flushed. The hero can carry a different one now.");
            return true;
        }

        if (hasElixir)
        {
            player.SendMessage($"{elixir.Name} not applied: that hero already carries an elixir. Buy a Flush first.");
            return true;
        }

        if (player.Gold < elixir.CostGold)
        {
            player.SendMessage($"{elixir.Name} costs {elixir.CostGold:F0} gold.");
            return true;
        }

        player.AdjustGold(-elixir.CostGold);
        ApplyElixir(target, elixir, playerIndex);
        player.SendMessage($"{elixir.Name} bound to {target.Name}. Every wave clone it spawns inherits it.");
        return true;
    }

    private UnitId FindElixirTarget(int playerIndex, Vector3 position)
    {
        UnitId best = UnitId.Invalid;
        float bestDistance = ElixirPurchaseRadius;

        foreach (UnitId candidate in _api.GetUnitsInRadius(position, ElixirPurchaseRadius))
        {
            if (!candidate.IsValid || candidate.IsDead)
                continue;

            if (ResolveRealPlayerIndex(candidate.Player) != playerIndex)
                continue;

            if (!tileStatesByUnitId.ContainsKey(candidate.UniqueId))
                continue;

            float distance = Vector3.Distance(candidate.Position, position);
            if (distance > bestDistance)
                continue;

            bestDistance = distance;
            best = candidate;
        }

        return best;
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

    /// <summary>
    /// Carries an elixir from a board hero onto the wave clone it spawned. Stacks start empty so
    /// a long match does not inflate them.
    /// </summary>
    private void ApplyInheritedElixir(UnitId clone, TileState? tileState)
    {
        if (tileState?.ElixirId == null)
            return;

        if (!TryResolveElixir(tileState.ElixirId, out ElixirDefinition definition))
            return;

        ApplyElixir(clone, definition, ResolveRealPlayerIndex(clone.Player));
        OnElixirWaveStart(clone);
    }

    // ---- Hooks ---------------------------------------------------------------------------

    private bool _elixirHookActive;
    private readonly List<(UnitId Attacker, UnitId Target, float Damage, string DamageType)> _pendingElixirDamage = new();

    /// <summary>
    /// Elixir procs land inside the engine's damage callback, which is inside an entity query.
    /// Killing a unit there would despawn entities mid-iteration, so procs that deal damage are
    /// queued and applied a fraction of a second later instead.
    /// </summary>
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

        // Damage an elixir proc deals must not trigger another elixir proc, exactly as with the
        // synchronous path.
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

    /// <summary>
    /// Runs the damage hooks once per real hit. Bonus damage the elixirs themselves deal re-enters
    /// the damage callback, so the flag stops a proc from triggering another proc in the same tick.
    /// </summary>
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

    /// <summary>
    /// Repoints the one stat modifier an elixir owns at its new stacked total. Elixirs grow a stack
    /// at a time, so adding a fresh modifier per stack would make the total triangular (1+2+3...)
    /// instead of the linear cap the card promises, and would also eat the entity's modifier slots.
    /// The max-health path is safe to re-point because a recalculation keeps the health ratio.
    /// </summary>
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

    /// <summary>
    /// One-second tick for the elixirs whose effect is time based rather than event based.
    /// </summary>
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
