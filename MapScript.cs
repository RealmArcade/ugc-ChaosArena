namespace Realm.Maps;

using System.Numerics;
using Realm.MapAPI;

public enum Element
{
    Water,
    Earth,
    Fire
}

public enum PrimaryAttribute
{
    Strength,
    Agility,
    Intelligence
}

public enum AbilityTargeting
{
    Passive,
    AutoCast,
    NoTarget,
    Self,
    PlayerUnits,
    Enemy,
    GroundRandom,
    GroundPlayerUnits,
    GroundEnemy
}

public enum TileKind
{
    Circle,
    Hero
}

public record AbilityMetaData(string AbilityId, float Range, AbilityTargeting Targeting, float ManaCost);

public record BalancePointAllocations(
    float AttackRange,
    float AttackSpeed,
    float AttackDamage,
    float MaxLife,
    float Armor,
    float ManaRegen,
    float StrengthPerLevel,
    float AgilityPerLevel,
    float IntelligencePerLevel);

public record UnitBaseStats(
    PrimaryAttribute PrimaryAttribute,
    float StartingMana,
    float AttackRange,
    float AttackSpeed,
    float AttackDamage,
    float MaxLife,
    float Armor,
    float ManaRegen,
    float StrengthPerLevel,
    float AgilityPerLevel,
    float IntelligencePerLevel);

public record UnitMetaData(
    string Name,
    string UnitTypeId,
    AbilityMetaData? WaterAbility,
    AbilityMetaData? EarthAbility,
    AbilityMetaData? FireAbility,
    AbilityMetaData? UltimateAbility,
    UnitBaseStats BaseStats)
{
    public AbilityMetaData? GetAbility(Element element) => element switch
    {
        Element.Water => WaterAbility,
        Element.Earth => EarthAbility,
        _ => FireAbility
    };

    public IEnumerable<AbilityMetaData> GetAllAbilities()
    {
        if (WaterAbility != null) yield return WaterAbility;
        if (EarthAbility != null) yield return EarthAbility;
        if (FireAbility != null) yield return FireAbility;
        if (UltimateAbility != null) yield return UltimateAbility;
    }
}

public class TileState
{
    public TileKind Kind { get; init; }
    public Element Element { get; set; }
    public bool ElementAssigned { get; set; }
    public int CircleLevel { get; init; }
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

    private static readonly UnitMetaData[] DraftableUnits =
    {
        new(
            "Shadow Strider",
            "unit/shadow_strider",
            new AbilityMetaData("ability/searing_aura", 8f, AbilityTargeting.Self, 25f),
            new AbilityMetaData("ability/hardened_carapace", 0f, AbilityTargeting.Self, 45f),
            new AbilityMetaData("ability/challengers_roar", 8f, AbilityTargeting.GroundEnemy, 70f),
            new AbilityMetaData("ability/shadow_veil", 12f, AbilityTargeting.Self, 120f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Agility, 0f, new BalancePointAllocations(7, 8, 9, 5, 3, 4, 2, 9, 3))),
        new(
            "Vanguard Sentinel",
            "unit/vanguard_sentinel",
            new AbilityMetaData("ability/shield_bash", 2f, AbilityTargeting.Enemy, 30f),
            new AbilityMetaData("ability/fortress_stance", 0f, AbilityTargeting.Self, 45f),
            new AbilityMetaData("ability/banner_of_valor", 8f, AbilityTargeting.PlayerUnits, 65f),
            new AbilityMetaData("ability/earth_shatter", 10f, AbilityTargeting.GroundEnemy, 120f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Strength, 0f, new BalancePointAllocations(2, 5, 8, 9, 7, 3, 8, 4, 2))),
        new(
            "Flame Archon",
            "unit/flame_archon",
            new AbilityMetaData("ability/pyro_blast", 10f, AbilityTargeting.GroundEnemy, 35f),
            new AbilityMetaData("ability/blazing_shield", 0f, AbilityTargeting.Self, 50f),
            new AbilityMetaData("ability/infernal_surge", 0f, AbilityTargeting.Self, 70f),
            new AbilityMetaData("ability/supernova", 12f, AbilityTargeting.GroundEnemy, 150f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Intelligence, 100f, new BalancePointAllocations(8, 6, 7, 4, 2, 8, 2, 3, 9))),
        new(
            "Frost Warden",
            "unit/frost_warden",
            new AbilityMetaData("ability/ice_shard", 10f, AbilityTargeting.Enemy, 30f),
            new AbilityMetaData("ability/glacial_barrier", 8f, AbilityTargeting.PlayerUnits, 50f),
            new AbilityMetaData("ability/frost_nova", 8f, AbilityTargeting.GroundEnemy, 65f),
            new AbilityMetaData("ability/absolute_zero", 12f, AbilityTargeting.GroundEnemy, 140f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Intelligence, 100f, new BalancePointAllocations(7, 5, 6, 5, 4, 7, 3, 3, 8))),
        new(
            "Storm Caller",
            "unit/storm_caller",
            new AbilityMetaData("ability/chain_lightning", 10f, AbilityTargeting.Enemy, 40f),
            new AbilityMetaData("ability/wind_step", 8f, AbilityTargeting.GroundRandom, 45f),
            new AbilityMetaData("ability/static_field", 8f, AbilityTargeting.GroundEnemy, 75f),
            new AbilityMetaData("ability/tempest_cataclysm", 12f, AbilityTargeting.GroundEnemy, 150f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Intelligence, 100f, new BalancePointAllocations(8, 7, 7, 4, 3, 8, 2, 4, 8))),
        new(
            "Verdant Druid",
            "unit/verdant_druid",
            new AbilityMetaData("ability/tangle_vines", 8f, AbilityTargeting.Enemy, 35f),
            new AbilityMetaData("ability/soothing_bloom", 8f, AbilityTargeting.PlayerUnits, 45f),
            new AbilityMetaData("ability/thorn_armor", 0f, AbilityTargeting.Self, 60f),
            new AbilityMetaData("ability/wrath_of_nature", 12f, AbilityTargeting.GroundEnemy, 130f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Strength, 50f, new BalancePointAllocations(3, 4, 6, 8, 6, 6, 7, 3, 5))),
        new(
            "Iron Warlord",
            "unit/iron_warlord",
            new AbilityMetaData("ability/heavy_strike", 2f, AbilityTargeting.Enemy, 25f),
            new AbilityMetaData("ability/battle_cry", 8f, AbilityTargeting.PlayerUnits, 50f),
            new AbilityMetaData("ability/iron_will", 0f, AbilityTargeting.Self, 60f),
            new AbilityMetaData("ability/war_stomp", 6f, AbilityTargeting.NoTarget, 110f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Strength, 0f, new BalancePointAllocations(2, 6, 9, 8, 8, 3, 9, 3, 1))),
        new(
            "Arcane Scholar",
            "unit/arcane_scholar",
            new AbilityMetaData("ability/arcane_missiles", 10f, AbilityTargeting.Enemy, 30f),
            new AbilityMetaData("ability/spell_shield", 0f, AbilityTargeting.Self, 45f),
            new AbilityMetaData("ability/mana_drain", 8f, AbilityTargeting.Enemy, 20f),
            new AbilityMetaData("ability/time_dilation", 10f, AbilityTargeting.GroundEnemy, 140f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Intelligence, 120f, new BalancePointAllocations(8, 5, 8, 4, 2, 9, 2, 2, 10))),
        new(
            "Nether Assassin",
            "unit/nether_assassin",
            new AbilityMetaData("ability/poison_blade", 2f, AbilityTargeting.Enemy, 25f),
            new AbilityMetaData("ability/shadow_step", 8f, AbilityTargeting.Enemy, 40f),
            new AbilityMetaData("ability/smoke_screen", 8f, AbilityTargeting.GroundEnemy, 55f),
            new AbilityMetaData("ability/death_mark", 10f, AbilityTargeting.Enemy, 90f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Agility, 0f, new BalancePointAllocations(2, 9, 10, 4, 3, 4, 2, 10, 2))),
        new(
            "Sun Priest",
            "unit/sun_priest",
            new AbilityMetaData("ability/solar_flare", 10f, AbilityTargeting.GroundEnemy, 35f),
            new AbilityMetaData("ability/radiant_blessing", 8f, AbilityTargeting.PlayerUnits, 50f),
            new AbilityMetaData("ability/blinding_ray", 8f, AbilityTargeting.Enemy, 60f),
            new AbilityMetaData("ability/dawn_judgement", 12f, AbilityTargeting.GroundEnemy, 135f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Intelligence, 100f, new BalancePointAllocations(6, 5, 5, 6, 4, 8, 3, 2, 9))),
        new(
            "Blood Berserker",
            "unit/blood_berserker",
            new AbilityMetaData("ability/frenzy_slash", 2f, AbilityTargeting.Enemy, 20f),
            new AbilityMetaData("ability/blood_lust", 0f, AbilityTargeting.Self, 35f),
            new AbilityMetaData("ability/sanguine_leap", 8f, AbilityTargeting.GroundEnemy, 50f),
            new AbilityMetaData("ability/unending_rage", 0f, AbilityTargeting.Self, 100f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Strength, 0f, new BalancePointAllocations(1, 8, 10, 7, 4, 2, 8, 6, 1))),
        new(
            "Stone Guardian",
            "unit/stone_guardian",
            new AbilityMetaData("ability/boulder_toss", 8f, AbilityTargeting.Enemy, 35f),
            new AbilityMetaData("ability/hardened_crust", 0f, AbilityTargeting.Self, 40f),
            new AbilityMetaData("ability/seismic_pulse", 6f, AbilityTargeting.NoTarget, 60f),
            new AbilityMetaData("ability/granite_avatar", 0f, AbilityTargeting.Self, 120f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Strength, 0f, new BalancePointAllocations(1, 3, 7, 10, 9, 3, 10, 2, 1))),
        new(
            "Chrono Weaver",
            "unit/chrono_weaver",
            new AbilityMetaData("ability/temporal_strike", 8f, AbilityTargeting.Enemy, 40f),
            new AbilityMetaData("ability/haste_field", 8f, AbilityTargeting.PlayerUnits, 55f),
            new AbilityMetaData("ability/stasis_bubble", 8f, AbilityTargeting.Enemy, 70f),
            new AbilityMetaData("ability/time_reversal", 0f, AbilityTargeting.Self, 150f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Intelligence, 100f, new BalancePointAllocations(6, 6, 6, 5, 3, 8, 2, 4, 8))),
        new(
            "Dune Skirmisher",
            "unit/dune_skirmisher",
            new AbilityMetaData("ability/sand_bolt", 10f, AbilityTargeting.Enemy, 25f),
            new AbilityMetaData("ability/quick_roll", 6f, AbilityTargeting.GroundRandom, 30f),
            new AbilityMetaData("ability/sandstorm_veil", 8f, AbilityTargeting.GroundPlayerUnits, 60f),
            new AbilityMetaData("ability/quicksand_trap", 10f, AbilityTargeting.GroundEnemy, 110f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Agility, 0f, new BalancePointAllocations(8, 9, 8, 5, 3, 4, 3, 9, 2))),
        new(
            "Coral Vanguard",
            "unit/coral_vanguard",
            new AbilityMetaData("ability/wave_crash", 8f, AbilityTargeting.GroundEnemy, 35f),
            new AbilityMetaData("ability/coral_shield", 8f, AbilityTargeting.PlayerUnits, 45f),
            new AbilityMetaData("ability/tidal_pull", 8f, AbilityTargeting.Enemy, 55f),
            new AbilityMetaData("ability/whirlpool_surge", 10f, AbilityTargeting.GroundEnemy, 125f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Strength, 50f, new BalancePointAllocations(3, 6, 7, 8, 7, 4, 7, 5, 2))),
        new(
            "Crystal Weaver",
            "unit/crystal_weaver",
            new AbilityMetaData("ability/prism_beam", 10f, AbilityTargeting.GroundEnemy, 35f),
            new AbilityMetaData("ability/crystal_ward", 8f, AbilityTargeting.GroundRandom, 40f),
            new AbilityMetaData("ability/refractive_shield", 0f, AbilityTargeting.Self, 65f),
            new AbilityMetaData("ability/crystalline_explosion", 12f, AbilityTargeting.GroundEnemy, 140f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Intelligence, 100f, new BalancePointAllocations(7, 6, 7, 5, 4, 7, 2, 3, 8))),
        new(
            "Astral Paragon",
            "unit/astral_paragon",
            new AbilityMetaData("ability/star_fall", 10f, AbilityTargeting.GroundEnemy, 40f),
            new AbilityMetaData("ability/astral_grace", 8f, AbilityTargeting.PlayerUnits, 50f),
            new AbilityMetaData("ability/cosmic_binding", 8f, AbilityTargeting.Enemy, 70f),
            new AbilityMetaData("ability/supernova_burst", 12f, AbilityTargeting.GroundEnemy, 160f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Intelligence, 100f, new BalancePointAllocations(7, 7, 8, 5, 3, 7, 3, 4, 7))),
        new(
            "Lightbringer Monk",
            "unit/lightbringer_monk",
            new AbilityMetaData("ability/radiant_fist", 2f, AbilityTargeting.Enemy, 25f),
            new AbilityMetaData("ability/mantra_of_healing", 8f, AbilityTargeting.PlayerUnits, 45f),
            new AbilityMetaData("ability/sanctuary_aura", 0f, AbilityTargeting.Self, 50f),
            new AbilityMetaData("ability/divine_intervention", 8f, AbilityTargeting.PlayerUnits, 130f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Strength, 50f, new BalancePointAllocations(2, 7, 7, 8, 6, 5, 7, 6, 3))),
        new(
            "Shadow Weaver",
            "unit/shadow_weaver",
            new AbilityMetaData("ability/gloom_bolt", 8f, AbilityTargeting.Enemy, 30f),
            new AbilityMetaData("ability/veil_of_shadows", 0f, AbilityTargeting.Self, 40f),
            new AbilityMetaData("ability/soul_tether", 8f, AbilityTargeting.Enemy, 60f),
            new AbilityMetaData("ability/abyssal_realm", 10f, AbilityTargeting.GroundEnemy, 120f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Agility, 50f, new BalancePointAllocations(5, 8, 8, 5, 4, 5, 3, 8, 5))),
        new(
            "Tempest Chaser",
            "unit/tempest_chaser",
            new AbilityMetaData("ability/gale_strike", 8f, AbilityTargeting.GroundEnemy, 25f),
            new AbilityMetaData("ability/wind_barrier", 0f, AbilityTargeting.Self, 35f),
            new AbilityMetaData("ability/cyclone_kick", 4f, AbilityTargeting.NoTarget, 50f),
            new AbilityMetaData("ability/tornado_storm", 10f, AbilityTargeting.GroundEnemy, 115f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Agility, 0f, new BalancePointAllocations(6, 10, 8, 5, 3, 4, 3, 10, 2)))
    };

    private readonly Dictionary<string, UnitMetaData> unitMetaDataByTypeId = new();
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

        foreach (UnitMetaData metaData in DraftableUnits)
        {
            unitMetaDataByTypeId[metaData.UnitTypeId] = metaData;
            if (metaData.UnitTypeId.StartsWith("unit/", StringComparison.OrdinalIgnoreCase))
                unitMetaDataByTypeId[metaData.UnitTypeId.Substring(5)] = metaData;
            else
                unitMetaDataByTypeId[$"unit/{metaData.UnitTypeId}"] = metaData;
        }

        RegisterAbilities();
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
    }

    private static UnitBaseStats BalanceBaseStatsByPoints(
        PrimaryAttribute primaryAttribute,
        float startingMana,
        BalancePointAllocations points)
    {
        const float attackRangeScalingPerPoint = 100f;
        const float attackSpeedScalingPerPoint = 0.1f;
        const float attackDamageScalingPerPoint = 10f;
        const float maxLifeScalingPerPoint = 100f;
        const float armorScalingPerPoint = 1f;
        const float manaRegenScalingPerPoint = 0.01f;
        const float attributePerLevelScalingPerPoint = 0.25f;
        const float attackRangeBaseValue = 100f;
        const float attackSpeedBaseValue = 0.1f;
        const float attackDamageBaseValue = 1f;
        const float maxLifeBaseValue = 100f;
        const float armorBaseValue = 0f;
        const float manaRegenBaseValue = 0.01f;
        const float attributeBaseValue = 1f;

        float totalPoints = points.AttackRange + points.AttackSpeed + points.AttackDamage + points.MaxLife + points.Armor
            + points.ManaRegen + points.StrengthPerLevel + points.AgilityPerLevel + points.IntelligencePerLevel;
        float normalizedScaling = 10f / totalPoints;

        return new UnitBaseStats(
            primaryAttribute,
            startingMana,
            MathF.Ceiling(normalizedScaling * points.AttackRange * attackRangeScalingPerPoint + attackRangeBaseValue) / WorldUnitsPerRealmUnit,
            MathF.Ceiling(normalizedScaling * points.AttackSpeed * attackSpeedScalingPerPoint + attackSpeedBaseValue),
            MathF.Ceiling(normalizedScaling * points.AttackDamage * attackDamageScalingPerPoint + attackDamageBaseValue),
            MathF.Ceiling(normalizedScaling * points.MaxLife * maxLifeScalingPerPoint + maxLifeBaseValue),
            MathF.Ceiling(normalizedScaling * points.Armor * armorScalingPerPoint + armorBaseValue),
            normalizedScaling * points.ManaRegen * manaRegenScalingPerPoint + manaRegenBaseValue,
            normalizedScaling * points.StrengthPerLevel * attributePerLevelScalingPerPoint + attributeBaseValue,
            normalizedScaling * points.AgilityPerLevel * attributePerLevelScalingPerPoint + attributeBaseValue,
            normalizedScaling * points.IntelligencePerLevel * attributePerLevelScalingPerPoint + attributeBaseValue);
    }

    private static bool IsRealPlayerSlot(int playerIndex) =>
        playerIndex >= 0 && playerIndex < RealPlayerSlotCount;

    private static bool IsProxyPlayerSlot(int playerIndex) =>
        playerIndex >= ProxyPlayerOffset && playerIndex < ProxyPlayerOffset + RealPlayerSlotCount;

    private static int ResolveRealPlayerIndex(int playerIndex) =>
        IsProxyPlayerSlot(playerIndex) ? playerIndex - ProxyPlayerOffset : playerIndex;

    private static int GetDraftCooldownMinutes(int foodUsed) =>
        FoodCapIncreaseMinutes[Math.Min(foodUsed, FoodCapIncreaseMinutes.Length - 1)];

    private static Vector3 ToCoordinatePosition(Vector3 position, Vector3 offset) =>
        new(position.X + offset.X, position.Y, position.Z + offset.Z);

    private static string GetCircleUnitTypeId(Element element) => element switch
    {
        Element.Water => CircleWaterUnitTypeId,
        Element.Fire => CircleFireUnitTypeId,
        _ => CircleEarthUnitTypeId
    };

    private void RegisterAbilities()
    {
        gameApi.RegisterAbility(SoulSiphonAbilityId, "Soul Siphon", "Low health enemies in the area may be executed, granting wood.");
        gameApi.RegisterAbility(HealingWaveAbilityId, "Healing Wave", "Heals allied units in the area based on missing health.");
        gameApi.RegisterAbility(AncestralSpiritAbilityId, "Ancestral Spirit", "Invokes a spirit of ancient ancestors to mend and protect allied ground forces in the targeted area.");

        gameApi.RegisterAbility(RerollItemAbilityId, "Reroll Item", "Rerolls the item on this tile into a different random item.", isInstant: true);
        gameApi.RegisterAbility(RerollElementAbilityId, "Reroll Element", "Rerolls to a different element affinity (guaranteed different).", isInstant: true);
        gameApi.RegisterAbility(RerollHeroAbilityId, "Reroll Hero", "Rerolls into a different available hero candidate, preserving level, item, and element.", isInstant: true);
        gameApi.RegisterAbility(RerollAllAbilityId, "Reroll All", "Rerolls item, element, and hero type for this tile.", isInstant: true);
        gameApi.RegisterAbility(SwapTileUnitsAbilityId, "Swap Units", "Select another player-owned tile unit to swap positions.");
        gameApi.RegisterAbility(UpgradeCriticalStrikeAbilityId, "Upgrade Critical Strike", "Researches Critical Strike for all spawned units.", isInstant: true);

        foreach (UnitMetaData metaData in DraftableUnits)
        {
            if (metaData.WaterAbility != null)
            {
                gameApi.SetAbilityGridPosition(metaData.WaterAbility.AbilityId, 0, 0);
                gameApi.SetAbilityHotkey(metaData.WaterAbility.AbilityId, "W");
            }
            if (metaData.EarthAbility != null)
            {
                gameApi.SetAbilityGridPosition(metaData.EarthAbility.AbilityId, 1, 0);
                gameApi.SetAbilityHotkey(metaData.EarthAbility.AbilityId, "E");
            }
            if (metaData.FireAbility != null)
            {
                gameApi.SetAbilityGridPosition(metaData.FireAbility.AbilityId, 2, 0);
                gameApi.SetAbilityHotkey(metaData.FireAbility.AbilityId, "R");
            }

            foreach (AbilityMetaData ability in metaData.GetAllAbilities())
            {
                gameApi.AddUnitTypeAbility(CircleEarthUnitTypeId, ability.AbilityId);
                gameApi.AddUnitTypeAbility(CircleFireUnitTypeId, ability.AbilityId);
                gameApi.AddUnitTypeAbility(CircleWaterUnitTypeId, ability.AbilityId);
                gameApi.AddUnitTypeAbility(AltarBuildingTypeId, ability.AbilityId);
            }

            gameApi.AddUnitTypeAbility(metaData.UnitTypeId, RerollItemAbilityId);
            gameApi.AddUnitTypeAbility(metaData.UnitTypeId, RerollElementAbilityId);
            gameApi.AddUnitTypeAbility(metaData.UnitTypeId, RerollHeroAbilityId);
            gameApi.AddUnitTypeAbility(metaData.UnitTypeId, RerollAllAbilityId);
            gameApi.AddUnitTypeAbility(metaData.UnitTypeId, SwapTileUnitsAbilityId);
        }

        string[] allCircleUnitTypes = { CircleEarthUnitTypeId, CircleFireUnitTypeId, CircleWaterUnitTypeId };
        foreach (string circleType in allCircleUnitTypes)
        {
            gameApi.AddUnitTypeAbility(circleType, RerollItemAbilityId);
            gameApi.AddUnitTypeAbility(circleType, RerollElementAbilityId);
            gameApi.AddUnitTypeAbility(circleType, SwapTileUnitsAbilityId);
        }

        gameApi.AddUnitTypeAbility(AltarBuildingTypeId, RerollAllAbilityId);
        gameApi.AddUnitTypeAbility(BlacksmithBuildingTypeId, UpgradeCriticalStrikeAbilityId);
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
        if (startLocation.LengthSquared() > 0.0001f)
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
        playerState.Deck.AddRange(DraftableUnits.Select(metaData => metaData.UnitTypeId));
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
        {
            string unitName = unitMetaDataByTypeId.TryGetValue(typeId, out UnitMetaData? m) ? m.Name : typeId;
            return $"{index + 1}: {unitName}";
        }));

        gameApi.SendMessageToPlayer(playerIndex, $"Draft offers at Altar: {offerList}. Select a Circle of Power or Altar to draft.");

        UpdateDraftOfferAbilities(playerIndex);
    }

    private void UpdateDraftOfferAbilities(int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];
        HashSet<string> offeredAbilityIds = new();

        foreach (string offerTypeId in playerState.Offers)
        {
            if (unitMetaDataByTypeId.TryGetValue(offerTypeId, out UnitMetaData? metaData))
            {
                foreach (AbilityMetaData ability in metaData.GetAllAbilities())
                    offeredAbilityIds.Add(ability.AbilityId);
            }
        }

        var unitsToConfigure = GetPlayerCircles(playerIndex).ToList();
        if (playerState.Altar != null && !playerState.Altar.IsDead)
            unitsToConfigure.Add(playerState.Altar);

        foreach (IUnit unit in unitsToConfigure)
        {
            foreach (UnitMetaData metaData in DraftableUnits)
            {
                foreach (AbilityMetaData ability in metaData.GetAllAbilities())
                {
                    bool isOffered = offeredAbilityIds.Contains(ability.AbilityId);
                    gameApi.SetAbilityState(unit, ability.AbilityId, !isOffered, !isOffered);
                    if (isOffered)
                        gameApi.SetAbilityManaCost(unit, ability.AbilityId, 0f);
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
                ElementAssigned = true
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
            int charges = Math.Max(unit.Level, 1);
            unit.AddItem(newItemId, charges);
            unit.SetItemCharges(newItemId, charges);
        }
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
                Vector3 position = unit.Position;
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
    }

    private void ApplyElement(IUnit unit, TileState state, bool includeDisabledAbilities)
    {
        unit.Name = state.Kind == TileKind.Circle
            ? $"{state.Element} [Level {state.CircleLevel}]"
            : state.Element.ToString();

        gameApi.SetUnitColor(unit, ElementColors[state.Element]);

        if (!unitMetaDataByTypeId.TryGetValue(unit.UnitId, out UnitMetaData? metaData))
            return;

        foreach (Element element in Enum.GetValues<Element>())
        {
            AbilityMetaData? ability = metaData.GetAbility(element);
            if (ability == null)
                continue;

            bool isActive = element == state.Element;
            if (!isActive && !includeDisabledAbilities)
                continue;

            gameApi.SetAbilityState(unit, ability.AbilityId, !isActive, false);
            if (isActive)
            {
                gameApi.SetAbilityManaCost(unit, ability.AbilityId, ability.ManaCost);
                gameApi.SetAbilityTooltip(ability.AbilityId, $"[{element}] {ability.AbilityId}");
            }
            else
            {
                gameApi.SetAbilityTooltip(ability.AbilityId, $"[{element}] {ability.AbilityId} (disabled)");
            }
        }
    }

    private void ApplyHeroStats(IUnit unit, UnitMetaData metaData, int level)
    {
        const float healthPerStrength = 25f;
        const float armorPerAgility = 0.3f;
        const float manaPerIntelligence = 15f;

        UnitBaseStats stats = metaData.BaseStats;
        int levelsGained = Math.Max(level, 1) - 1;

        float primaryAttributeGain = stats.PrimaryAttribute switch
        {
            PrimaryAttribute.Strength => stats.StrengthPerLevel,
            PrimaryAttribute.Agility => stats.AgilityPerLevel,
            _ => stats.IntelligencePerLevel
        };

        unit.MaxHealth = stats.MaxLife + levelsGained * stats.StrengthPerLevel * healthPerStrength;
        unit.Health = unit.MaxHealth;
        unit.MaxMana = stats.StartingMana + levelsGained * stats.IntelligencePerLevel * manaPerIntelligence;
        unit.Mana = unit.MaxMana;
        unit.Armor = stats.Armor + levelsGained * stats.AgilityPerLevel * armorPerAgility;
        unit.Damage = stats.AttackDamage + levelsGained * primaryAttributeGain;
        unit.Range = stats.AttackRange;
        unit.AttackSpeed = stats.AttackSpeed;
        unit.ManaRegen = stats.ManaRegen;

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
    }

    private void ConfigureDraftedHero(IUnit hero, UnitMetaData metaData, int level, IEnumerable<string> itemIds, Element element)
    {
        ApplyHeroStats(hero, metaData, level);

        foreach (string itemId in itemIds)
        {
            int charges = Math.Max(level, 1);
            hero.AddItem(itemId, charges);
            hero.SetItemCharges(itemId, charges);
        }

        var state = new TileState { Kind = TileKind.Hero, Element = element, ElementAssigned = true };
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

    private HashSet<string> GetPlayerDraftedUnitTypeIds(int playerIndex)
    {
        return gameApi.GetUnitsOwnedByPlayer(playerIndex, unit => unitMetaDataByTypeId.ContainsKey(unit.UnitId))
            .Select(unit => unit.UnitId)
            .ToHashSet();
    }

    private void DraftUnit(int playerIndex, string unitTypeId, IUnit circle)
    {
        PlayerState playerState = playerStates[playerIndex];
        UnitMetaData metaData = unitMetaDataByTypeId[unitTypeId];

        playerState.Offers.Clear();
        playerState.DraftedCount++;
        playerState.NextDraftSeconds = GetDraftCooldownMinutes(playerState.DraftedCount) * 60;

        Vector3 position = circle.Position;
        List<string> itemIds = circle.GetItems().ToList();
        Element element = tileStatesByUnitId.TryGetValue(circle.UniqueId, out TileState? circleState)
            ? circleState.Element
            : Element.Water;

        tileStatesByUnitId.Remove(circle.UniqueId);
        gameApi.DestroyUnit(circle, false, false);

        IUnit hero = gameApi.SpawnUnitForPlayer(unitTypeId, position, playerIndex);
        ConfigureDraftedHero(hero, metaData, Math.Max(playerState.DraftedCount, 1), itemIds, element);

        if (playerState.Builder != null)
            gameApi.SelectUnit(playerState.Builder);

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
        List<UnitMetaData> candidates = DraftableUnits.Where(metaData => !draftedUnitTypeIds.Contains(metaData.UnitTypeId)).ToList();
        UnitMetaData? replacement = gameApi.PickRandom(candidates);

        if (replacement == null)
        {
            gameApi.SendMessageToPlayer(playerIndex, "No other heroes are available to reroll into.");
            return;
        }

        TileState state = tileStatesByUnitId[unit.UniqueId];
        Vector3 position = unit.Position;
        int level = unit.Level;
        List<string> itemIds = unit.GetItems().ToList();

        PlayerState playerState = playerStates[playerIndex];
        if (playerState.DamageDealtPerUnitTypePerWave.TryGetValue(unit.UnitId, out Dictionary<int, float>? damageRecords))
            damageRecords.Clear();

        tileStatesByUnitId.Remove(unit.UniqueId);
        gameApi.DestroyUnit(unit, false, false);

        IUnit newUnit = gameApi.SpawnUnitForPlayer(replacement.UnitTypeId, position, playerIndex);
        ConfigureDraftedHero(newUnit, replacement, level, itemIds, state.Element);
        gameApi.SelectUnit(newUnit);
    }

    private void PerformAllReroll(IUnit unit, TileState state, int playerIndex)
    {
        PerformItemReroll(unit);
        PerformElementReroll(unit, state);
        if (state.Kind == TileKind.Hero)
            PerformHeroReroll(unit, playerIndex);
    }

    private void SwapTileUnits(IUnit caster, IUnit target, int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];

        if (playerState.DamageDealtPerUnitTypePerWave.TryGetValue(caster.UnitId, out Dictionary<int, float>? casterDamage))
            casterDamage.Clear();
        if (playerState.DamageDealtPerUnitTypePerWave.TryGetValue(target.UnitId, out Dictionary<int, float>? targetDamage))
            targetDamage.Clear();

        gameApi.SwapUnitPositions(caster, target);
        gameApi.SelectUnit(caster);
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
            .GetUnitsOwnedByPlayer(playerIndex, unit => !unit.IsDead && unitMetaDataByTypeId.ContainsKey(unit.UnitId))
            .ToList();

        int techLevel = gameApi.GetPlayerTechLevel(playerIndex, CriticalStrikeTechId);

        foreach (IUnit draftedUnit in draftedUnits)
        {
            UnitMetaData metaData = unitMetaDataByTypeId[draftedUnit.UnitId];
            Vector3 spawnPosition = ToCoordinatePosition(draftedUnit.Position, playerState.SpawnOffset);

            IUnit clone = gameApi.SpawnUnitForPlayer(draftedUnit.UnitId, spawnPosition, proxyPlayerIndex);
            ApplyHeroStats(clone, metaData, draftedUnit.Level);

            if (techLevel > 0)
                gameApi.SetPlayerTechLevel(proxyPlayerIndex, CriticalStrikeTechId, techLevel);

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
        nextFoodCapIncreaseSeconds = GetDraftCooldownMinutes(foodCap) * 60;

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

                    float damageMultiplier = unitMetaDataByTypeId.ContainsKey(unit.UnitId)
                        ? baseDamageMultiplier
                        : baseDamageMultiplier * 2f;

                    gameApi.DealDamage(unit, unit, unit.MaxHealth * damageMultiplier);
                    unit.AddBuff(AntiSnowballPoisonBuffId, 15f);

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

        if (!unitMetaDataByTypeId.TryGetValue(unit.UnitId, out UnitMetaData? metaData))
        {
            PlayerState playerState = playerStates[realPlayerIndex];
            if (playerState.SpawnedUnitsPerWave.TryGetValue(waveNumber, out List<IUnit>? currentWaveUnits)
                && !currentWaveUnits.Any(existing => existing.UniqueId == unit.UniqueId))
            {
                currentWaveUnits.Add(unit);
            }
            return;
        }

        Element cycleElement = (Element)(aiCycle - 1);
        AbilityMetaData? ability = metaData.GetAbility(cycleElement);
        if (ability == null || ability.Targeting == AbilityTargeting.Passive)
            return;

        if (ability.Targeting == AbilityTargeting.AutoCast)
        {
            gameApi.SetAbilityAutoCast(unit, ability.AbilityId, true);
            return;
        }

        int team = gameApi.GetPlayerTeam(unit.Player);

        if (ability.Range > 0f && (ability.Targeting == AbilityTargeting.Enemy || ability.Targeting == AbilityTargeting.GroundEnemy))
        {
            IUnit? enemyTarget = FindEnemyInRange(unit, team, ability.Range);
            if (enemyTarget != null)
            {
                CastAtTarget(unit, ability, enemyTarget, ability.Targeting == AbilityTargeting.GroundEnemy);
                return;
            }
        }

        if (FindEnemyInRange(unit, team, EnemyAcquisitionRange) == null)
            return;

        if (ability.Range > 0f && (ability.Targeting == AbilityTargeting.PlayerUnits || ability.Targeting == AbilityTargeting.GroundPlayerUnits))
        {
            IUnit? allyTarget = gameApi.GetRandomUnitInRadius(
                unit.Position,
                ability.Range,
                candidate => candidate.UniqueId != unit.UniqueId && candidate.Player == unit.Player);

            if (allyTarget != null)
            {
                CastAtTarget(unit, ability, allyTarget, ability.Targeting == AbilityTargeting.GroundPlayerUnits);
                return;
            }
        }

        if (ability.Targeting == AbilityTargeting.PlayerUnits || ability.Targeting == AbilityTargeting.Self)
        {
            gameApi.IssueCastOrder(unit, ability.AbilityId, unit);
            return;
        }

        if (ability.Range > 0f && ability.Targeting == AbilityTargeting.GroundRandom)
        {
            Vector3 position = unit.Position;
            var randomTarget = new Vector3(
                gameApi.RandomFloat(position.X - ability.Range, position.X + ability.Range),
                position.Y,
                gameApi.RandomFloat(position.Z - ability.Range, position.Z + ability.Range));
            gameApi.IssueCastOrderAt(unit, ability.AbilityId, randomTarget);
            return;
        }

        if (ability.Targeting == AbilityTargeting.NoTarget)
            gameApi.CastAbility(unit, ability.AbilityId, unit.Position);
    }

    private void CastAtTarget(IUnit caster, AbilityMetaData ability, IUnit target, bool isGroundTargeted)
    {
        if (isGroundTargeted)
            gameApi.IssueCastOrderAt(caster, ability.AbilityId, target.Position);
        else
            gameApi.IssueCastOrder(caster, ability.AbilityId, target);
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
            case UpgradeCriticalStrikeAbilityId:
                gameApi.AddPlayerTechLevel(playerIndex, CriticalStrikeTechId);
                gameApi.AddPlayerTechLevel(playerIndex + ProxyPlayerOffset, CriticalStrikeTechId);
                gameApi.SendMessageToPlayer(playerIndex, $"Critical Strike upgraded (Level {gameApi.GetPlayerTechLevel(playerIndex, CriticalStrikeTechId)}).");
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

        UnitMetaData? matchedUnitMeta = null;
        foreach (UnitMetaData metaData in DraftableUnits)
        {
            if (metaData.GetAllAbilities().Any(a => a.AbilityId.Equals(spellId, StringComparison.OrdinalIgnoreCase)
                || a.AbilityId.EndsWith($"/{spellId}", StringComparison.OrdinalIgnoreCase)
                || spellId.EndsWith($"/{a.AbilityId}", StringComparison.OrdinalIgnoreCase)))
            {
                matchedUnitMeta = metaData;
                break;
            }
        }

        if (matchedUnitMeta == null)
            return;

        if (!playerState.Offers.Contains(matchedUnitMeta.UnitTypeId)
            && !playerState.Offers.Any(o => matchedUnitMeta.UnitTypeId.EndsWith(o, StringComparison.OrdinalIgnoreCase)))
        {
            gameApi.SendMessageToPlayer(playerIndex, $"{matchedUnitMeta.Name} is not currently offered for draft.");
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

        DraftUnit(playerIndex, matchedUnitMeta.UnitTypeId, draftTargetCircle);
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
            string displayName = unitMetaDataByTypeId.TryGetValue(entry.Key, out UnitMetaData? meta)
                ? meta.Name
                : entry.Key;

            summaries.Add((displayName, averageDamage));
        }

        if (summaries.Count == 0)
            return "-";

        var ordered = summaries.OrderByDescending(s => s.AvgDamage).Take(3);
        return string.Join(" | ", ordered.Select(s => $"{s.UnitName}: {(int)s.AvgDamage}"));
    }
}
