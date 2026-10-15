namespace Realm.Maps;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Realm.MapAPI;

public enum Element
{
    Water,
    Earth,
    Fire
}

public enum TileKind
{
    Circle,
    Hero
}

public record UpgradeDefinition(string TechId, string AbilityId, string Name);

public class TileState
{
    public TileKind Kind { get; set; }
    public Element Element { get; set; }
    public bool ElementAssigned { get; set; }
    public int CircleLevel { get; set; } = 1;
    public Vector3 AnchorPosition { get; set; }
}

public class PlayerState
{
    public IUnit? Builder { get; set; }
    public IUnit? Altar { get; set; }
    public IUnit? Blacksmith { get; set; }
    public List<string> Deck { get; } = new();
    public List<string> Offers { get; } = new();
    public List<Vector3> RemainingTilePositions { get; } = new();
    public int DraftedCount { get; set; }
    public int NextDraftSeconds { get; set; }
    public Vector3 BasePosition { get; set; }
    public Vector3 SpawnOffset { get; set; }
    public int CountdownTextHandle { get; set; }
    public float SpawnAtSeconds { get; set; }
    public bool HasSpawnedThisRound { get; set; } = true;
    public Dictionary<int, List<IUnit>> SpawnedUnitsPerWave { get; } = new();
    public Dictionary<string, Dictionary<int, float>> DamageDealtPerUnitTypePerWave { get; } = new();
}

public class MapScript : IWasmModule
{
    private const int RealPlayerSlotCount = 12;
    private const int ProxyPlayerOffset = 12;
    private const float WorldUnitsPerRealmUnit = 50f;
    private const float SpawnOffsetDistance = 500f / WorldUnitsPerRealmUnit;
    private const float TileSpacing = 125f / WorldUnitsPerRealmUnit;
    private const float BuildingSpacing = 200f / WorldUnitsPerRealmUnit;
    private const float EnemyAcquisitionRange = 1000f / WorldUnitsPerRealmUnit;
    private const float SoulSiphonRadius = 175f / WorldUnitsPerRealmUnit;
    private const float HealingWaveRadius = 350f / WorldUnitsPerRealmUnit;
    private const int WaveIntervalSeconds = 45;
    private const int WaveCountdownVisibleSeconds = 30;
    private const int SpawnSlotCount = 12;
    private const int TilesPerPlayer = 9;
    private const int CountdownFontSize = 216;
    private const float CountdownDistanceMultiplier = 3.75f;

    private const string BuilderUnitTypeId = "unit/builder";
    private const string AltarBuildingTypeId = "building/altar";
    private const string BlacksmithBuildingTypeId = "building/blacksmith";
    private const string CircleEarthUnitTypeId = "unit/circle_of_power_earth";
    private const string CircleFireUnitTypeId = "unit/circle_of_power_fire";
    private const string CircleWaterUnitTypeId = "unit/circle_of_power_water";

    private const string RerollItemAbilityId = "ability/reroll_item";
    private const string RerollElementAbilityId = "ability/reroll_element";
    private const string RerollHeroAbilityId = "ability/reroll_hero";
    private const string RerollAllAbilityId = "ability/reroll_all";
    private const string SwapTileUnitsAbilityId = "ability/swap_tile_units";
    private const string UpgradeCriticalStrikeAbilityId = "ability/upgrade_critical_strike";

    private const string SoulSiphonAbilityId = "ability/soul_siphon";
    private const string HealingWaveAbilityId = "ability/healing_wave";
    private const string AncestralSpiritAbilityId = "ability/ancestral_spirit";
    private const string AntiSnowballPoisonBuffId = "anti_snowball_poison";
    private const string CriticalStrikeTechId = "critical_strike";

    private static readonly Vector3 ArenaCenter = Vector3.Zero;

    private static readonly Dictionary<Element, Vector3> ElementColors = new()
    {
        [Element.Water] = new Vector3(0f, 0f, 1f),
        [Element.Earth] = new Vector3(0f, 1f, 0f),
        [Element.Fire] = new Vector3(1f, 0f, 0f)
    };

    private static readonly int[] FoodCapIncreaseMinutes = { 0, 1, 2, 3, 5, 8, 12, 17, 23 };

    private static readonly string[] ItemPool =
    {
        "item/titan_belt",
        "item/swiftness_boots",
        "item/cowl_of_the_wise",
        "item/iron_claws",
        "item/gauntlets_of_power",
        "item/crystal_pendant",
        "item/vitality_ring",
        "item/ring_of_protection",
        "item/tome_of_knowledge"
    };

    private static readonly string[] DraftableUnits =
    {
        "unit/shadow_strider",
        "unit/vanguard_sentinel",
        "unit/flame_archon",
        "unit/frost_warden",
        "unit/storm_caller",
        "unit/verdant_druid",
        "unit/iron_warlord",
        "unit/arcane_scholar",
        "unit/nether_assassin",
        "unit/sun_priest",
        "unit/blood_berserker",
        "unit/stone_guardian",
        "unit/chrono_weaver",
        "unit/dune_skirmisher",
        "unit/coral_vanguard",
        "unit/crystal_weaver",
        "unit/astral_paragon",
        "unit/lightbringer_monk",
        "unit/shadow_weaver",
        "unit/tempest_chaser"
    };

    private static readonly UpgradeDefinition[] DraftableUpgrades =
    {
        new(CriticalStrikeTechId, UpgradeCriticalStrikeAbilityId, "Critical Strike")
    };

    // Ordered elemental abilities: [0]=Water, [1]=Earth, [2]=Fire, [3]=Ultimate
    private static readonly Dictionary<string, string[]> DraftableUnitAbilities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["unit/shadow_strider"] = new[] { "ability/searing_aura", "ability/hardened_carapace", "ability/challengers_roar", "ability/shadow_veil" },
        ["unit/vanguard_sentinel"] = new[] { "ability/shield_bash", "ability/fortress_stance", "ability/banner_of_valor", "ability/earth_shatter" },
        ["unit/flame_archon"] = new[] { "ability/pyro_blast", "ability/blazing_shield", "ability/infernal_surge", "ability/supernova" },
        ["unit/frost_warden"] = new[] { "ability/ice_shard", "ability/glacial_barrier", "ability/frost_nova", "ability/absolute_zero" },
        ["unit/storm_caller"] = new[] { "ability/chain_lightning", "ability/wind_step", "ability/static_field", "ability/tempest_cataclysm" },
        ["unit/verdant_druid"] = new[] { "ability/tangle_vines", "ability/soothing_bloom", "ability/thorn_armor", "ability/wrath_of_nature" },
        ["unit/iron_warlord"] = new[] { "ability/heavy_strike", "ability/battle_cry", "ability/iron_will", "ability/war_stomp" },
        ["unit/arcane_scholar"] = new[] { "ability/arcane_missiles", "ability/spell_shield", "ability/mana_drain", "ability/time_dilation" },
        ["unit/nether_assassin"] = new[] { "ability/poison_blade", "ability/shadow_step", "ability/smoke_screen", "ability/death_mark" },
        ["unit/sun_priest"] = new[] { "ability/solar_flare", "ability/radiant_blessing", "ability/blinding_ray", "ability/dawn_judgement" },
        ["unit/blood_berserker"] = new[] { "ability/frenzy_slash", "ability/blood_lust", "ability/sanguine_leap", "ability/unending_rage" },
        ["unit/stone_guardian"] = new[] { "ability/boulder_toss", "ability/hardened_crust", "ability/seismic_pulse", "ability/granite_avatar" },
        ["unit/chrono_weaver"] = new[] { "ability/temporal_strike", "ability/haste_field", "ability/stasis_bubble", "ability/time_reversal" },
        ["unit/dune_skirmisher"] = new[] { "ability/sand_bolt", "ability/quick_roll", "ability/sandstorm_veil", "ability/quicksand_trap" },
        ["unit/coral_vanguard"] = new[] { "ability/wave_crash", "ability/coral_shield", "ability/tidal_pull", "ability/whirlpool_surge" },
        ["unit/crystal_weaver"] = new[] { "ability/prism_beam", "ability/crystal_ward", "ability/refractive_shield", "ability/crystalline_explosion" },
        ["unit/astral_paragon"] = new[] { "ability/star_fall", "ability/astral_grace", "ability/cosmic_binding", "ability/supernova_burst" },
        ["unit/lightbringer_monk"] = new[] { "ability/radiant_fist", "ability/mantra_of_healing", "ability/sanctuary_aura", "ability/divine_intervention" },
        ["unit/shadow_weaver"] = new[] { "ability/gloom_bolt", "ability/veil_of_shadows", "ability/soul_tether", "ability/abyssal_realm" },
        ["unit/tempest_chaser"] = new[] { "ability/gale_strike", "ability/wind_barrier", "ability/cyclone_kick", "ability/tornado_storm" }
    };

    private readonly Dictionary<int, PlayerState> playerStates = new();
    private readonly Dictionary<int, TileState> tileStatesByUnitId = new();
    private readonly Dictionary<int, TileState> waveUnitStatesByUnitId = new();

    private IGameAPI gameApi = null!;
    private int waveNumber;
    private float roundClockSeconds;
    private readonly List<float> spawnOffsetSlots = new();
    private readonly List<int> spawnSlotOrder = new();
    private int gameSecondsElapsed;
    private int foodCap;
    private int nextFoodCapIncreaseSeconds;
    private int aiCycle;

    public void Initialize(IGameAPI api)
    {
        gameApi = api;

        ConfigureAbilityHotkeys();
        ConfigureDraftChoicesOnTiles();
        InitializePlayers();

        gameApi.OnUnitDied += HandleUnitDied;
        gameApi.OnUnitDamaged += HandleUnitDamaged;
        gameApi.OnSpellCast += HandleSpellCast;

        gameApi.ShowSummaryTable("Chaos Arena - Combat Statistics", true);
        gameApi.SetSummaryTableHeaders("Player", "Kills", "Top Average Damage (Unit / Waves)");

        for (int slotIndex = 0; slotIndex < SpawnSlotCount; slotIndex++)
        {
            spawnOffsetSlots.Add(slotIndex * (float)WaveIntervalSeconds / SpawnSlotCount);
            spawnSlotOrder.Add(slotIndex);
        }

        gameApi.StartCountdownTimer(WaveIntervalSeconds, "Next wave round");

        gameApi.ScheduleRepeatingTimer(1f, HandleSpawnTimerTick);
        gameApi.ScheduleRepeatingTimer(1f, HandleGameTimerTick);
        gameApi.ScheduleRepeatingTimer(1f, RunAiLoop);
        gameApi.ScheduleRepeatingTimer(5f, GrantWoodPassive);
        gameApi.ScheduleRepeatingTimer(15f, ApplyNegativeHealthRegen);
        gameApi.ScheduleRepeatingTimer(60f, GrantPassiveExperience);
        gameApi.ScheduleRepeatingTimer(5f, UpdateSummaryTable);
        gameApi.ScheduleTimer(0f, UpdateSummaryTable);
    }

    public void Update(IGameAPI api, float delta)
    {
        foreach (KeyValuePair<int, TileState> entry in tileStatesByUnitId)
        {
            IUnit? unit = gameApi.GetUnitById(entry.Key);
            if (unit != null && !unit.IsDead)
            {
                if (Vector3.DistanceSquared(unit.Position, entry.Value.AnchorPosition) > 0.04f)
                {
                    unit.Teleport(entry.Value.AnchorPosition);
                    unit.HoldPosition();
                }
            }
        }
    }

    private static bool TryGetUnitAbilities(string unitId, out string[] abilities)
    {
        if (DraftableUnitAbilities.TryGetValue(unitId, out abilities!))
            return true;
        if (unitId.StartsWith("unit/", StringComparison.OrdinalIgnoreCase)
            && DraftableUnitAbilities.TryGetValue(unitId.Substring(5), out abilities!))
            return true;
        if (!unitId.StartsWith("unit/", StringComparison.OrdinalIgnoreCase)
            && DraftableUnitAbilities.TryGetValue($"unit/{unitId}", out abilities!))
            return true;

        abilities = Array.Empty<string>();
        return false;
    }

    private static bool IsDraftableUnit(string unitId) =>
        TryGetUnitAbilities(unitId, out _);

    private static string GetUnitDisplayName(string unitId)
    {
        string slug = unitId.StartsWith("unit/", StringComparison.OrdinalIgnoreCase)
            ? unitId.Substring(5)
            : unitId;
        return string.Join(" ", slug.Split('_')
            .Where(word => !string.IsNullOrEmpty(word))
            .Select(word => char.ToUpperInvariant(word[0]) + (word.Length > 1 ? word.Substring(1).ToLowerInvariant() : string.Empty)));
    }

    private static bool IsRealPlayerSlot(int playerIndex) =>
        playerIndex >= 0 && playerIndex < RealPlayerSlotCount;

    private static bool IsProxyPlayerSlot(int playerIndex) =>
        playerIndex >= ProxyPlayerOffset && playerIndex < ProxyPlayerOffset + RealPlayerSlotCount;

    private static int ResolveRealPlayerIndex(int playerIndex) =>
        IsProxyPlayerSlot(playerIndex) ? playerIndex - ProxyPlayerOffset : playerIndex;

    private static int GetNextDraftUnlockTimestamp(int draftedCount) =>
        draftedCount < FoodCapIncreaseMinutes.Length
            ? FoodCapIncreaseMinutes[draftedCount] * 60
            : int.MaxValue;

    private static Vector3 ToCoordinatePosition(Vector3 position, Vector3 offset) =>
        new(position.X + offset.X, position.Y, position.Z + offset.Z);

    private static string GetCircleUnitTypeId(Element element) => element switch
    {
        Element.Water => CircleWaterUnitTypeId,
        Element.Fire => CircleFireUnitTypeId,
        _ => CircleEarthUnitTypeId
    };

    private void ResetPlayerSelection(int playerIndex)
    {
        if (playerStates.TryGetValue(playerIndex, out PlayerState? playerState)
            && playerState.Builder != null
            && !playerState.Builder.IsDead)
        {
            gameApi.SelectUnit(playerState.Builder);
        }
    }

    private void ConfigureAbilityHotkeys()
    {
        foreach ((_, string[] abilities) in DraftableUnitAbilities)
        {
            if (abilities.Length > 0 && !string.IsNullOrEmpty(abilities[0]))
            {
                gameApi.SetAbilityGridPosition(abilities[0], 0, 0);
                gameApi.SetAbilityHotkey(abilities[0], "W");
            }
            if (abilities.Length > 1 && !string.IsNullOrEmpty(abilities[1]))
            {
                gameApi.SetAbilityGridPosition(abilities[1], 1, 0);
                gameApi.SetAbilityHotkey(abilities[1], "E");
            }
            if (abilities.Length > 2 && !string.IsNullOrEmpty(abilities[2]))
            {
                gameApi.SetAbilityGridPosition(abilities[2], 2, 0);
                gameApi.SetAbilityHotkey(abilities[2], "R");
            }
        }
    }

    private void ConfigureDraftChoicesOnTiles()
    {
        foreach ((_, string[] abilities) in DraftableUnitAbilities)
        {
            foreach (string abilityId in abilities)
            {
                gameApi.AddUnitTypeAbility(CircleEarthUnitTypeId, abilityId);
                gameApi.AddUnitTypeAbility(CircleFireUnitTypeId, abilityId);
                gameApi.AddUnitTypeAbility(CircleWaterUnitTypeId, abilityId);
                gameApi.AddUnitTypeAbility(AltarBuildingTypeId, abilityId);
            }
        }
    }

    private void InitializePlayers()
    {
        List<int> activePlayerIndices = gameApi.GetActivePlayerIndices(RealPlayerSlotCount).ToList();

        foreach (int playerIndex in activePlayerIndices)
        {
            int proxyPlayerIndex = playerIndex + ProxyPlayerOffset;
            gameApi.SetPlayerComputerControlled(proxyPlayerIndex, true);

            var playerState = new PlayerState();
            playerStates[playerIndex] = playerState;
            playerState.BasePosition = GetPlayerBasePosition(playerIndex);
            playerState.SpawnOffset = GetSpawnOffset(playerState.BasePosition);
            playerState.CountdownTextHandle = gameApi.CreateStaticText(
                string.Empty,
                ToCoordinatePosition(playerState.BasePosition, playerState.SpawnOffset * CountdownDistanceMultiplier),
                Vector3.One,
                CountdownFontSize);
            gameApi.SetStaticTextVisible(playerState.CountdownTextHandle, false);
        }

        foreach (int playerIndex in activePlayerIndices)
            InitializePlayerTiles(playerIndex);

        foreach (int playerIndex in activePlayerIndices)
            InitializePlayerBase(playerIndex);

        foreach (int playerIndex in activePlayerIndices)
            InitializePlayerDeck(playerIndex);

        InitializePlayerAlliances(activePlayerIndices);
    }

    private Vector3 GetPlayerBasePosition(int playerIndex)
    {
        Vector3 startLocation = gameApi.GetPlayerStartLocation(playerIndex);
        if (startLocation != Vector3.Zero)
            return startLocation;

        return gameApi.GetCoordinate($"PlayerStart{playerIndex + 1}").Center;
    }

    private static Vector3 GetSpawnOffset(Vector3 basePosition)
    {
        float relativeX = basePosition.X - ArenaCenter.X;
        float relativeZ = basePosition.Z - ArenaCenter.Z;

        if (MathF.Abs(relativeX) >= MathF.Abs(relativeZ))
            return new Vector3(relativeX > 0f ? -SpawnOffsetDistance : SpawnOffsetDistance, 0f, 0f);

        return new Vector3(0f, 0f, relativeZ > 0f ? -SpawnOffsetDistance : SpawnOffsetDistance);
    }

    private void InitializePlayerAlliances(List<int> activePlayerIndices)
    {
        foreach (int playerIndex in activePlayerIndices)
        {
            int proxyPlayerIndex = playerIndex + ProxyPlayerOffset;
            int team = gameApi.GetPlayerTeam(playerIndex);

            gameApi.SetPlayersAllied(playerIndex, proxyPlayerIndex, true);
            gameApi.SetPlayersAllied(proxyPlayerIndex, playerIndex, true);
            gameApi.SetPlayerTeam(proxyPlayerIndex, team);

            foreach (int otherPlayerIndex in activePlayerIndices)
            {
                if (otherPlayerIndex == playerIndex || gameApi.GetPlayerTeam(otherPlayerIndex) != team)
                    continue;

                int otherProxyPlayerIndex = otherPlayerIndex + ProxyPlayerOffset;
                gameApi.SetPlayersAllied(otherProxyPlayerIndex, proxyPlayerIndex, true);
                gameApi.SetPlayersAllied(proxyPlayerIndex, otherProxyPlayerIndex, true);
            }
        }
    }

    private void InitializePlayerTiles(int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];
        Vector3 tileCenter = ToCoordinatePosition(playerState.BasePosition, playerState.SpawnOffset);

        for (int columnIndex = -1; columnIndex <= 1; columnIndex++)
        {
            for (int rowIndex = -1; rowIndex <= 1; rowIndex++)
            {
                playerState.RemainingTilePositions.Add(new Vector3(
                    tileCenter.X + columnIndex * TileSpacing,
                    tileCenter.Y,
                    tileCenter.Z + rowIndex * TileSpacing));
            }
        }

        gameApi.Shuffle(playerState.RemainingTilePositions);
    }

    private void InitializePlayerBase(int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];
        Vector3 startLocation = playerState.BasePosition;
        Vector3 offset = playerState.SpawnOffset;

        IUnit builder = gameApi.SpawnUnitForPlayer(BuilderUnitTypeId, startLocation, playerIndex);
        builder.Damage = 0f;
        builder.Invulnerable = true;
        builder.Speed = 0f;
        gameApi.SetUnitFacing(builder, ArenaCenter);
        playerState.Builder = builder;

        Vector3 buildingStart = new(startLocation.X - offset.X * 0.5f, startLocation.Y, startLocation.Z - offset.Z * 0.5f);
        Vector3 altarOffset = MathF.Abs(offset.X) > MathF.Abs(offset.Z)
            ? new Vector3(0f, 0f, BuildingSpacing)
            : new Vector3(BuildingSpacing, 0f, 0f);

        Vector3 blacksmithOffset = MathF.Abs(offset.X) > MathF.Abs(offset.Z)
            ? new Vector3(0f, 0f, -BuildingSpacing)
            : new Vector3(-BuildingSpacing, 0f, 0f);

        IUnit altar = gameApi.SpawnUnitForPlayer(AltarBuildingTypeId, buildingStart + altarOffset, playerIndex);
        altar.Damage = 0f;
        altar.Invulnerable = true;
        gameApi.SetUnitFacing(altar, ArenaCenter);
        playerState.Altar = altar;

        IUnit blacksmith = gameApi.SpawnUnitForPlayer(BlacksmithBuildingTypeId, buildingStart + blacksmithOffset, playerIndex);
        blacksmith.Damage = 0f;
        blacksmith.Invulnerable = true;
        gameApi.SetUnitFacing(blacksmith, ArenaCenter);
        playerState.Blacksmith = blacksmith;

        UnlockPlayerTiles(playerIndex, 1);

        gameApi.PanCameraTo(builder.Position, 0f);
        gameApi.SelectUnit(builder);
    }

    private void InitializePlayerDeck(int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];
        playerState.Deck.AddRange(DraftableUnits);
        gameApi.Shuffle(playerState.Deck);
        DrawDraftOffers(playerIndex);
    }

    private void DrawDraftOffers(int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];
        playerState.Offers.Clear();

        for (int drawIndex = 0; drawIndex < 3 && playerState.Deck.Count > 0; drawIndex++)
        {
            playerState.Offers.Add(playerState.Deck[0]);
            playerState.Deck.RemoveAt(0);
        }

        if (playerState.Offers.Count == 0)
            return;

        string offerList = string.Join(", ", playerState.Offers.Select((typeId, index) =>
            $"{index + 1}: {GetUnitDisplayName(typeId)}"));

        gameApi.SendMessageToPlayer(playerIndex, $"Draft offers at Altar: {offerList}. Select a Circle of Power or Altar to draft.");

        UpdateDraftOfferAbilities(playerIndex);
    }

    private void UpdateDraftOfferAbilities(int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];
        HashSet<string> offeredAbilityIds = new(StringComparer.OrdinalIgnoreCase);

        foreach (string offerTypeId in playerState.Offers)
        {
            if (TryGetUnitAbilities(offerTypeId, out string[] abilities))
            {
                foreach (string abilityId in abilities)
                    offeredAbilityIds.Add(abilityId);
            }
        }

        var unitsToConfigure = GetPlayerCircles(playerIndex).ToList();
        if (playerState.Altar != null && !playerState.Altar.IsDead)
            unitsToConfigure.Add(playerState.Altar);

        foreach (IUnit unit in unitsToConfigure)
        {
            foreach ((_, string[] abilities) in DraftableUnitAbilities)
            {
                foreach (string abilityId in abilities)
                {
                    bool isOffered = offeredAbilityIds.Contains(abilityId);
                    gameApi.SetAbilityState(unit, abilityId, !isOffered, !isOffered);
                    if (isOffered)
                        gameApi.SetAbilityManaCost(unit, abilityId, 0f);
                }
            }
        }
    }

    private void UnlockPlayerTiles(int playerIndex, int count)
    {
        PlayerState playerState = playerStates[playerIndex];

        for (int tileCount = 0; tileCount < count && playerState.RemainingTilePositions.Count > 0; tileCount++)
        {
            int tileLevel = TilesPerPlayer - playerState.RemainingTilePositions.Count + 1;
            Vector3 position = playerState.RemainingTilePositions[0];
            playerState.RemainingTilePositions.RemoveAt(0);

            Element initialElement = gameApi.PickRandom(Enum.GetValues<Element>().ToList());
            string circleTypeId = GetCircleUnitTypeId(initialElement);
            IUnit circle = gameApi.SpawnUnitForPlayer(circleTypeId, position, playerIndex);
            circle.Damage = 0f;
            circle.Speed = 0f;
            circle.Invulnerable = true;
            circle.HideHealthAndManaBars = true;
            gameApi.SetUnitFacing(circle, ArenaCenter);

            var state = new TileState
            {
                Kind = TileKind.Circle,
                CircleLevel = tileLevel,
                Element = initialElement,
                ElementAssigned = true,
                AnchorPosition = position
            };
            tileStatesByUnitId[circle.UniqueId] = state;

            PerformItemReroll(circle);
            ApplyElement(circle, state, true);
        }

        UpdateDraftOfferAbilities(playerIndex);
    }

    private void PerformItemReroll(IUnit unit)
    {
        string? previousItemId = null;
        foreach (string itemId in unit.GetItems().ToList())
        {
            previousItemId = itemId;
            unit.RemoveItem(itemId);
        }

        List<string> availableItemIds = ItemPool.Where(itemId => itemId != previousItemId).ToList();
        string? newItemId = gameApi.PickRandom(availableItemIds) ?? previousItemId;
        if (newItemId != null)
        {
            int charges = tileStatesByUnitId.TryGetValue(unit.UniqueId, out TileState? state)
                ? Math.Max(state.CircleLevel, 1)
                : Math.Max(unit.Level, 1);
            unit.AddItem(newItemId, charges);
            unit.SetItemCharges(newItemId, charges);
        }

        ResetPlayerSelection(unit.Player);
    }

    private void PerformElementReroll(IUnit unit, TileState state)
    {
        List<Element> candidateElements = Enum.GetValues<Element>().ToList();
        if (state.ElementAssigned)
            candidateElements.Remove(state.Element);

        state.Element = gameApi.PickRandom(candidateElements);
        state.ElementAssigned = true;

        if (state.Kind == TileKind.Circle)
        {
            string newCircleTypeId = GetCircleUnitTypeId(state.Element);
            if (!unit.UnitId.Equals(newCircleTypeId, StringComparison.OrdinalIgnoreCase))
            {
                int playerIndex = unit.Player;
                Vector3 position = state.AnchorPosition;
                var items = unit.GetItems().Select(id => (id, unit.GetItemCharges(id))).ToList();
                tileStatesByUnitId.Remove(unit.UniqueId);
                gameApi.DestroyUnit(unit, false, false);

                IUnit newCircle = gameApi.SpawnUnitForPlayer(newCircleTypeId, position, playerIndex);
                newCircle.Damage = 0f;
                newCircle.Speed = 0f;
                newCircle.Invulnerable = true;
                newCircle.HideHealthAndManaBars = true;
                gameApi.SetUnitFacing(newCircle, ArenaCenter);

                foreach (var (itemId, charges) in items)
                {
                    newCircle.AddItem(itemId, charges);
                    newCircle.SetItemCharges(itemId, charges);
                }

                tileStatesByUnitId[newCircle.UniqueId] = state;
                unit = newCircle;
            }
        }

        ApplyElement(unit, state, true);
        ResetPlayerSelection(unit.Player);
    }

    private void ApplyElement(IUnit unit, TileState state, bool includeDisabledAbilities)
    {
        unit.Name = state.Kind == TileKind.Circle
            ? $"{state.Element} [Level {state.CircleLevel}]"
            : state.Element.ToString();

        gameApi.SetUnitColor(unit, ElementColors[state.Element]);

        if (!TryGetUnitAbilities(unit.UnitId, out string[] abilities))
            return;

        // abilities: [0]=Water, [1]=Earth, [2]=Fire, [3]=Ultimate
        for (int i = 0; i < 3 && i < abilities.Length; i++)
        {
            Element element = (Element)i;
            string abilityId = abilities[i];
            if (string.IsNullOrEmpty(abilityId))
                continue;

            bool isActive = element == state.Element;
            if (!isActive && !includeDisabledAbilities)
                continue;

            gameApi.SetAbilityState(unit, abilityId, !isActive, false);
            if (isActive)
                gameApi.SetAbilityTooltip(abilityId, $"[{element}] {abilityId}");
            else
                gameApi.SetAbilityTooltip(abilityId, $"[{element}] {abilityId} (disabled)");
        }
    }

    private static void ApplyHeroStats(IUnit unit, int level)
    {
        if (unit.IsHero)
            unit.Level = Math.Max(level, 1);
    }

    private void LockTileUnit(IUnit unit)
    {
        unit.Invulnerable = true;
        unit.Speed = 0f;
        unit.Damage = 0f;
        unit.ManaRegen = 0f;
        unit.HideHealthAndManaBars = true;
        gameApi.SetUnitFacing(unit, ArenaCenter);
        unit.Stop();
        unit.HoldPosition();
    }

    private void ConfigureDraftedHero(IUnit hero, int level, IEnumerable<string> itemIds, Element element, Vector3 anchorPosition)
    {
        ApplyHeroStats(hero, level);

        foreach (string itemId in itemIds)
        {
            int charges = Math.Max(level, 1);
            hero.AddItem(itemId, charges);
            hero.SetItemCharges(itemId, charges);
        }

        var state = new TileState
        {
            Kind = TileKind.Hero,
            Element = element,
            ElementAssigned = true,
            CircleLevel = level,
            AnchorPosition = anchorPosition
        };
        tileStatesByUnitId[hero.UniqueId] = state;

        ApplyElement(hero, state, true);
        LockTileUnit(hero);
    }

    private bool IsCircleUnit(IUnit unit) =>
        (tileStatesByUnitId.TryGetValue(unit.UniqueId, out TileState? state) && state.Kind == TileKind.Circle)
        || unit.UnitId.Contains("circle_of_power", StringComparison.OrdinalIgnoreCase)
        || unit.UnitId.Contains("CircleOfPower", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<IUnit> GetPlayerCircles(int playerIndex) =>
        gameApi.GetUnitsOwnedByPlayer(playerIndex, unit => !unit.IsDead && IsCircleUnit(unit));

    private HashSet<string> GetPlayerDraftedUnitTypeIds(int playerIndex) =>
        gameApi.GetUnitsOwnedByPlayer(playerIndex, unit => IsDraftableUnit(unit.UnitId))
            .Select(unit => unit.UnitId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void DraftUnit(int playerIndex, string unitTypeId, IUnit circle)
    {
        PlayerState playerState = playerStates[playerIndex];

        playerState.Offers.Clear();
        playerState.DraftedCount++;
        playerState.NextDraftSeconds = GetNextDraftUnlockTimestamp(playerState.DraftedCount);

        Vector3 position = circle.Position;
        List<string> itemIds = circle.GetItems().ToList();
        int circleLevel = 1;
        Element element = Element.Water;
        if (tileStatesByUnitId.TryGetValue(circle.UniqueId, out TileState? circleState))
        {
            element = circleState.Element;
            circleLevel = circleState.CircleLevel;
            position = circleState.AnchorPosition;
        }

        tileStatesByUnitId.Remove(circle.UniqueId);
        gameApi.DestroyUnit(circle, false, false);

        IUnit hero = gameApi.SpawnUnitForPlayer(unitTypeId, position, playerIndex);
        ConfigureDraftedHero(hero, circleLevel, itemIds, element, position);

        ResetPlayerSelection(playerIndex);

        DrawDraftOffers(playerIndex);
        UnlockPlayerTiles(playerIndex, 1);
    }

    private void PerformComputerDraft(int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];
        string? chosenUnitTypeId = gameApi.PickRandom(playerState.Offers);
        IUnit? chosenCircle = gameApi.PickRandom(GetPlayerCircles(playerIndex).ToList());

        if (chosenUnitTypeId == null || chosenCircle == null)
            return;

        DraftUnit(playerIndex, chosenUnitTypeId, chosenCircle);
    }

    private void PerformHeroReroll(IUnit unit, int playerIndex)
    {
        HashSet<string> draftedUnitTypeIds = GetPlayerDraftedUnitTypeIds(playerIndex);
        List<string> candidates = DraftableUnits.Where(id => !draftedUnitTypeIds.Contains(id)).ToList();
        string? replacement = gameApi.PickRandom(candidates);

        if (replacement == null)
        {
            gameApi.SendMessageToPlayer(playerIndex, "No other heroes are available to reroll into.");
            return;
        }

        TileState state = tileStatesByUnitId[unit.UniqueId];
        Vector3 position = state.AnchorPosition;
        int level = state.CircleLevel;
        List<string> itemIds = unit.GetItems().ToList();

        PlayerState playerState = playerStates[playerIndex];
        if (playerState.DamageDealtPerUnitTypePerWave.TryGetValue(unit.UnitId, out Dictionary<int, float>? damageRecords))
            damageRecords.Clear();

        tileStatesByUnitId.Remove(unit.UniqueId);
        gameApi.DestroyUnit(unit, false, false);

        IUnit newUnit = gameApi.SpawnUnitForPlayer(replacement, position, playerIndex);
        ConfigureDraftedHero(newUnit, level, itemIds, state.Element, position);
        ResetPlayerSelection(playerIndex);
    }

    private void PerformAllReroll(IUnit unit, TileState state, int playerIndex)
    {
        PerformItemReroll(unit);
        PerformElementReroll(unit, state);
        if (state.Kind == TileKind.Hero)
            PerformHeroReroll(unit, playerIndex);
        ResetPlayerSelection(playerIndex);
    }

    private void SwapTileUnits(IUnit caster, IUnit target, int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];

        if (playerState.DamageDealtPerUnitTypePerWave.TryGetValue(caster.UnitId, out Dictionary<int, float>? casterDamage))
            casterDamage.Clear();
        if (playerState.DamageDealtPerUnitTypePerWave.TryGetValue(target.UnitId, out Dictionary<int, float>? targetDamage))
            targetDamage.Clear();

        if (tileStatesByUnitId.TryGetValue(caster.UniqueId, out TileState? casterState)
            && tileStatesByUnitId.TryGetValue(target.UniqueId, out TileState? targetState))
        {
            int tempLevel = casterState.CircleLevel;
            casterState.CircleLevel = targetState.CircleLevel;
            targetState.CircleLevel = tempLevel;

            Vector3 tempPos = casterState.AnchorPosition;
            casterState.AnchorPosition = targetState.AnchorPosition;
            targetState.AnchorPosition = tempPos;

            ApplyTileLevelToUnit(caster, casterState);
            ApplyTileLevelToUnit(target, targetState);
        }

        gameApi.SwapUnitPositions(caster, target);
        ResetPlayerSelection(playerIndex);
    }

    private void ApplyTileLevelToUnit(IUnit unit, TileState state)
    {
        if (state.Kind == TileKind.Hero)
        {
            ApplyHeroStats(unit, state.CircleLevel);
            LockTileUnit(unit);
        }
        else if (state.Kind == TileKind.Circle)
        {
            unit.Name = $"{state.Element} [Level {state.CircleLevel}]";
        }

        foreach (string itemId in unit.GetItems().ToList())
        {
            int charges = Math.Max(state.CircleLevel, 1);
            unit.SetItemCharges(itemId, charges);
        }
    }

    private void HandleSpawnTimerTick()
    {
        roundClockSeconds += 1f;

        if (roundClockSeconds >= WaveIntervalSeconds)
        {
            roundClockSeconds -= WaveIntervalSeconds;
            StartWaveRound();
            gameApi.StartCountdownTimer(WaveIntervalSeconds, "Next wave round");
        }

        foreach (KeyValuePair<int, PlayerState> entry in playerStates)
        {
            PlayerState playerState = entry.Value;

            if (!playerState.HasSpawnedThisRound && roundClockSeconds >= playerState.SpawnAtSeconds)
            {
                playerState.HasSpawnedThisRound = true;
                if (gameApi.IsPlayerActive(entry.Key))
                    SpawnWaveForPlayer(entry.Key);
            }

            float secondsUntilSpawn = playerState.SpawnAtSeconds - roundClockSeconds;
            bool isCountdownVisible = !playerState.HasSpawnedThisRound && secondsUntilSpawn <= WaveCountdownVisibleSeconds;
            if (isCountdownVisible)
                gameApi.SetStaticText(playerState.CountdownTextHandle, ((int)MathF.Ceiling(secondsUntilSpawn)).ToString());
            gameApi.SetStaticTextVisible(playerState.CountdownTextHandle, isCountdownVisible);
        }
    }

    private void StartWaveRound()
    {
        waveNumber++;

        gameApi.Shuffle(spawnSlotOrder);

        int slotCursor = 0;
        foreach (int playerIndex in playerStates.Keys.OrderBy(index => index))
        {
            PlayerState playerState = playerStates[playerIndex];
            playerState.SpawnedUnitsPerWave[waveNumber] = new List<IUnit>();

            if (!gameApi.IsPlayerActive(playerIndex))
            {
                playerState.HasSpawnedThisRound = true;
                continue;
            }

            playerState.SpawnAtSeconds = spawnOffsetSlots[spawnSlotOrder[slotCursor]];
            playerState.HasSpawnedThisRound = false;
            slotCursor++;
        }
    }

    private void SpawnWaveForPlayer(int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];
        int proxyPlayerIndex = playerIndex + ProxyPlayerOffset;

        List<IUnit> draftedUnits = gameApi
            .GetUnitsOwnedByPlayer(playerIndex, unit => !unit.IsDead && IsDraftableUnit(unit.UnitId))
            .ToList();

        foreach (IUnit draftedUnit in draftedUnits)
        {
            Vector3 spawnPosition = ToCoordinatePosition(draftedUnit.Position, playerState.SpawnOffset);

            IUnit clone = gameApi.SpawnUnitForPlayer(draftedUnit.UnitId, spawnPosition, proxyPlayerIndex);
            ApplyHeroStats(clone, draftedUnit.Level);

            foreach (UpgradeDefinition upgrade in DraftableUpgrades)
            {
                int techLevel = gameApi.GetPlayerTechLevel(playerIndex, upgrade.TechId);
                if (techLevel > 0)
                {
                    gameApi.SetPlayerTechLevel(proxyPlayerIndex, upgrade.TechId, techLevel);
                    gameApi.SetAbilityState(clone, upgrade.AbilityId, false, false);
                }
                else
                {
                    gameApi.SetAbilityState(clone, upgrade.AbilityId, true, true);
                }
            }

            foreach (string itemId in draftedUnit.GetItems())
            {
                int charges = draftedUnit.GetItemCharges(itemId);
                clone.AddItem(itemId, charges);
                clone.SetItemCharges(itemId, charges);
            }

            Element element = tileStatesByUnitId.TryGetValue(draftedUnit.UniqueId, out TileState? tileState)
                ? tileState.Element
                : Element.Water;

            var cloneState = new TileState { Kind = TileKind.Hero, Element = element, ElementAssigned = true };
            waveUnitStatesByUnitId[clone.UniqueId] = cloneState;
            ApplyElement(clone, cloneState, false);

            gameApi.SetUnitFacing(clone, ArenaCenter);
            clone.AttackMove(ArenaCenter);
            playerState.SpawnedUnitsPerWave[waveNumber].Add(clone);
        }
    }

    private void HandleGameTimerTick()
    {
        gameSecondsElapsed++;

        if (gameSecondsElapsed >= nextFoodCapIncreaseSeconds)
            IncreaseFoodCap();
    }

    private void IncreaseFoodCap()
    {
        if (foodCap >= FoodCapIncreaseMinutes.Length)
            return;

        foodCap++;
        nextFoodCapIncreaseSeconds = GetNextDraftUnlockTimestamp(foodCap);

        foreach (int playerIndex in playerStates.Keys.ToList())
        {
            gameApi.SetPlayerMaxPopulation(playerIndex, foodCap);

            if (gameApi.IsPlayerComputer(playerIndex))
                PerformComputerDraft(playerIndex);
        }
    }

    private void GrantWoodPassive()
    {
        foreach (int playerIndex in playerStates.Keys)
            gameApi.AdjustPlayerWood(playerIndex, 1f);
    }

    private void GrantPassiveExperience()
    {
        foreach (PlayerState playerState in playerStates.Values)
        {
            if (playerState.Builder != null && !playerState.Builder.IsDead)
                playerState.Builder.Experience += 15f;
        }
    }

    private void ApplyNegativeHealthRegen()
    {
        foreach (PlayerState playerState in playerStates.Values)
        {
            for (int waveIndex = waveNumber - 1; waveIndex >= 1; waveIndex--)
            {
                if (!playerState.SpawnedUnitsPerWave.TryGetValue(waveIndex, out List<IUnit>? waveUnits))
                    continue;

                int ageOfWave = waveNumber - waveIndex;
                float baseDamageMultiplier = MathF.Pow(1.1f, ageOfWave) - 1f;

                for (int unitIndex = waveUnits.Count - 1; unitIndex >= 0; unitIndex--)
                {
                    IUnit unit = waveUnits[unitIndex];
                    if (unit.IsDead)
                    {
                        waveUnits.RemoveAt(unitIndex);
                        continue;
                    }

                    float damageMultiplier = IsDraftableUnit(unit.UnitId)
                        ? baseDamageMultiplier
                        : baseDamageMultiplier * 2f;

                    gameApi.DealDamage(unit, unit, unit.MaxHealth * damageMultiplier);
                    if (!unit.HasBuff(AntiSnowballPoisonBuffId))
                        unit.AddBuff(AntiSnowballPoisonBuffId, float.PositiveInfinity);

                    if (unit.IsDead)
                        waveUnits.RemoveAt(unitIndex);
                }
            }
        }
    }

    private void RunAiLoop()
    {
        aiCycle = (aiCycle + 1) % 4;

        foreach (int playerIndex in playerStates.Keys)
        {
            List<IUnit> proxyUnits = gameApi
                .GetUnitsOwnedByPlayer(playerIndex + ProxyPlayerOffset, unit => !unit.IsDead)
                .ToList();

            foreach (IUnit unit in proxyUnits)
                RunUnitAi(unit, playerIndex);
        }
    }

    private void RunUnitAi(IUnit unit, int realPlayerIndex)
    {
        if (aiCycle == 0)
        {
            unit.AttackMove(ArenaCenter);
            return;
        }

        if (!waveUnitStatesByUnitId.TryGetValue(unit.UniqueId, out TileState? state)
            || !TryGetUnitAbilities(unit.UnitId, out string[] abilities))
        {
            PlayerState playerState = playerStates[realPlayerIndex];
            if (playerState.SpawnedUnitsPerWave.TryGetValue(waveNumber, out List<IUnit>? currentWaveUnits)
                && !currentWaveUnits.Any(existing => existing.UniqueId == unit.UniqueId))
            {
                currentWaveUnits.Add(unit);
            }
            return;
        }

        int elementIndex = (int)state.Element;
        string activeAbilityId = elementIndex < abilities.Length ? abilities[elementIndex] : string.Empty;
        if (string.IsNullOrEmpty(activeAbilityId))
            return;

        int team = gameApi.GetPlayerTeam(unit.Player);
        IUnit? enemyTarget = FindEnemyInRange(unit, team, EnemyAcquisitionRange);
        if (enemyTarget != null)
        {
            gameApi.CastAbility(unit, activeAbilityId, enemyTarget.Position);
        }
        else
        {
            gameApi.CastAbility(unit, activeAbilityId, unit.Position);
        }
    }

    private IUnit? FindEnemyInRange(IUnit unit, int team, float range)
    {
        return gameApi.GetRandomUnitInRadius(
            unit.Position,
            range,
            candidate => IsProxyPlayerSlot(candidate.Player) && gameApi.GetPlayerTeam(candidate.Player) != team);
    }

    private void HandleSpellCast(IUnit? caster, string spellId, Vector3 targetPosition)
    {
        if (caster == null)
            return;

        int playerIndex = ResolveRealPlayerIndex(caster.Player);

        UpgradeDefinition? upgrade = DraftableUpgrades.FirstOrDefault(u => u.AbilityId.Equals(spellId, StringComparison.OrdinalIgnoreCase));
        if (upgrade != null)
        {
            gameApi.AddPlayerTechLevel(playerIndex, upgrade.TechId);
            gameApi.AddPlayerTechLevel(playerIndex + ProxyPlayerOffset, upgrade.TechId);
            gameApi.SendMessageToPlayer(playerIndex, $"{upgrade.Name} upgraded (Level {gameApi.GetPlayerTechLevel(playerIndex, upgrade.TechId)}).");
            return;
        }

        switch (spellId)
        {
            case SoulSiphonAbilityId:
                ApplySoulSiphon(caster, targetPosition);
                break;
            case HealingWaveAbilityId:
                ApplyHealingWave(caster, targetPosition);
                break;
            case RerollItemAbilityId:
                PerformItemReroll(caster);
                break;
            case RerollElementAbilityId when tileStatesByUnitId.TryGetValue(caster.UniqueId, out TileState? state):
                PerformElementReroll(caster, state);
                break;
            case RerollHeroAbilityId when tileStatesByUnitId.TryGetValue(caster.UniqueId, out TileState? state) && state.Kind == TileKind.Hero:
                PerformHeroReroll(caster, playerIndex);
                break;
            case RerollAllAbilityId when tileStatesByUnitId.TryGetValue(caster.UniqueId, out TileState? state):
                PerformAllReroll(caster, state, playerIndex);
                break;
            case SwapTileUnitsAbilityId:
                HandleSwapAbilityCast(caster, targetPosition, playerIndex);
                break;
            default:
                TryDraftFromAbilityCast(caster, spellId);
                break;
        }
    }

    private void HandleSwapAbilityCast(IUnit caster, Vector3 targetPosition, int playerIndex)
    {
        IUnit? targetTileUnit = gameApi.GetUnitsInRadius(targetPosition, 2f)
            .FirstOrDefault(u => u.Player == playerIndex && u.UniqueId != caster.UniqueId && tileStatesByUnitId.ContainsKey(u.UniqueId));

        if (targetTileUnit != null)
            SwapTileUnits(caster, targetTileUnit, playerIndex);
    }

    private void TryDraftFromAbilityCast(IUnit caster, string spellId)
    {
        int playerIndex = ResolveRealPlayerIndex(caster.Player);
        if (!playerStates.TryGetValue(playerIndex, out PlayerState? playerState))
            return;

        bool isCircle = IsCircleUnit(caster);
        bool isAltar = caster.UnitId.Equals(AltarBuildingTypeId, StringComparison.OrdinalIgnoreCase)
            || caster.UnitId.Equals("building/altar", StringComparison.OrdinalIgnoreCase)
            || caster.UnitId.Equals("altar", StringComparison.OrdinalIgnoreCase);

        IUnit? draftTargetCircle = null;

        if (isCircle)
        {
            draftTargetCircle = caster;
        }
        else if (isAltar)
        {
            draftTargetCircle = GetPlayerCircles(playerIndex).FirstOrDefault();
        }

        if (draftTargetCircle == null)
            return;

        string? matchedUnitTypeId = null;
        foreach ((string unitId, string[] abilities) in DraftableUnitAbilities)
        {
            if (abilities.Any(a => a.Equals(spellId, StringComparison.OrdinalIgnoreCase)
                || a.EndsWith($"/{spellId}", StringComparison.OrdinalIgnoreCase)
                || spellId.EndsWith($"/{a}", StringComparison.OrdinalIgnoreCase)))
            {
                matchedUnitTypeId = unitId;
                break;
            }
        }

        if (matchedUnitTypeId == null)
            return;

        if (!playerState.Offers.Contains(matchedUnitTypeId)
            && !playerState.Offers.Any(o => matchedUnitTypeId.EndsWith(o, StringComparison.OrdinalIgnoreCase)))
        {
            gameApi.SendMessageToPlayer(playerIndex, $"{GetUnitDisplayName(matchedUnitTypeId)} is not currently offered for draft.");
            return;
        }

        if (playerState.DraftedCount >= foodCap)
        {
            gameApi.SendMessageToPlayer(playerIndex, "Food Cap Exceeded");
            return;
        }

        if (gameSecondsElapsed < playerState.NextDraftSeconds)
        {
            gameApi.SendMessageToPlayer(playerIndex, $"Draft is on cooldown for {playerState.NextDraftSeconds - gameSecondsElapsed} more seconds.");
            return;
        }

        DraftUnit(playerIndex, matchedUnitTypeId, draftTargetCircle);
    }

    private void ApplySoulSiphon(IUnit caster, Vector3 targetPosition)
    {
        int casterTeam = gameApi.GetPlayerTeam(caster.Player);
        int rewardedPlayerIndex = ResolveRealPlayerIndex(caster.Player);

        foreach (IUnit target in gameApi.GetUnitsInRadius(targetPosition, SoulSiphonRadius).ToList())
        {
            if (target.IsDead || gameApi.GetPlayerTeam(target.Player) == casterTeam)
                continue;

            float healthPercent = 100f * target.Health / target.MaxHealth;
            float executeChance = 26f - healthPercent;
            if (executeChance < 0f)
                continue;

            if (gameApi.RandomInt(1, 100) <= executeChance)
            {
                gameApi.KillUnit(target);
                gameApi.AdjustPlayerWood(rewardedPlayerIndex, 1f);
            }
        }
    }

    private void ApplyHealingWave(IUnit caster, Vector3 targetPosition)
    {
        foreach (IUnit target in gameApi.GetUnitsInRadius(targetPosition, HealingWaveRadius).ToList())
        {
            if (target.IsDead || target.Player != caster.Player)
                continue;

            float healthPercent = 100f * target.Health / target.MaxHealth;
            float healAmount = (100f - healthPercent) * target.MaxHealth / 4f;
            gameApi.HealUnit(target, healAmount);
        }
    }

    private void HandleUnitDied(IUnit dyingUnit, IUnit? killer)
    {
        tileStatesByUnitId.Remove(dyingUnit.UniqueId);
        waveUnitStatesByUnitId.Remove(dyingUnit.UniqueId);

        if (killer == null)
            return;

        int killerPlayerIndex = ResolveRealPlayerIndex(killer.Player);
        if (!playerStates.ContainsKey(killerPlayerIndex))
            return;

        gameApi.SetPlayerKills(killerPlayerIndex, gameApi.GetPlayerKills(killerPlayerIndex) + 1);
        gameApi.AdjustPlayerGold(killerPlayerIndex, 1f);
    }

    private void HandleUnitDamaged(IUnit victim, IUnit attacker, float damage)
    {
        int damagingPlayerIndex = ResolveRealPlayerIndex(attacker.Player);
        if (!playerStates.TryGetValue(damagingPlayerIndex, out PlayerState? playerState))
            return;

        if (!playerState.DamageDealtPerUnitTypePerWave.TryGetValue(attacker.UnitId, out Dictionary<int, float>? damagePerWave))
        {
            damagePerWave = new Dictionary<int, float>();
            playerState.DamageDealtPerUnitTypePerWave[attacker.UnitId] = damagePerWave;
        }

        damagePerWave.TryGetValue(waveNumber, out float existingDamage);
        damagePerWave[waveNumber] = existingDamage + damage;
    }

    private void UpdateSummaryTable()
    {
        gameApi.ClearSummaryTable();

        var rankedPlayers = playerStates.Keys
            .Select(playerIndex => (PlayerIndex: playerIndex, Kills: gameApi.GetPlayerKills(playerIndex)))
            .OrderByDescending(entry => entry.Kills);

        foreach ((int playerIndex, int kills) in rankedPlayers)
        {
            string coloredPlayerName = gameApi.FormatColoredPlayerName(playerIndex);
            string topDamageBreakdown = GetTopDamageBreakdown(playerStates[playerIndex]);

            gameApi.SetSummaryTableRow(
                coloredPlayerName,
                kills.ToString(),
                topDamageBreakdown);
        }
    }

    private string GetTopDamageBreakdown(PlayerState playerState)
    {
        var summaries = new List<(string UnitName, float AvgDamage)>();

        foreach (KeyValuePair<string, Dictionary<int, float>> entry in playerState.DamageDealtPerUnitTypePerWave)
        {
            float totalDamage = 0f;
            int wavesCounted = 0;

            foreach (KeyValuePair<int, float> waveDamage in entry.Value)
            {
                if (waveDamage.Key == waveNumber)
                    continue;

                totalDamage += waveDamage.Value;
                wavesCounted++;
            }

            if (wavesCounted == 0)
                continue;

            float averageDamage = totalDamage / wavesCounted;
            string displayName = GetUnitDisplayName(entry.Key);

            summaries.Add((displayName, averageDamage));
        }

        if (summaries.Count == 0)
            return "-";

        var ordered = summaries.OrderByDescending(s => s.AvgDamage).Take(3);
        return string.Join(" | ", ordered.Select(s => $"{s.UnitName}: {(int)s.AvgDamage}"));
    }
}
