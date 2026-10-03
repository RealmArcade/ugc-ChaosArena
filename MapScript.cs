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
    UnitBaseStats BaseStats)
{
    public AbilityMetaData? GetAbility(Element element) => element switch
    {
        Element.Water => WaterAbility,
        Element.Earth => EarthAbility,
        _ => FireAbility
    };
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
    private const float EnemyAcquisitionRange = 1000f / WorldUnitsPerRealmUnit;
    private const float SoulSiphonRadius = 175f / WorldUnitsPerRealmUnit;
    private const float HealingWaveRadius = 350f / WorldUnitsPerRealmUnit;
    private const int WaveIntervalSeconds = 45;
    private const int WaveCountdownVisibleSeconds = 30;
    private const int SpawnSlotCount = 12;
    private const int TilesPerPlayer = 9;
    private const int CountdownFontSize = 216;
    private const float CountdownDistanceMultiplier = 3.75f;
    private const string BuilderUnitTypeId = "Builder";
    private const string CircleUnitTypeId = "CircleOfPower";
    private const string SoulSiphonAbilityId = "soul_siphon";
    private const string HealingWaveAbilityId = "healing_wave";
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
        "belt_of_giant_strength",
        "boots_of_quelthalas",
        "circlet_of_nobility",
        "claws_of_attack",
        "gloves_of_haste",
        "pendant_of_mana",
        "periapt_of_vitality",
        "ring_of_protection",
        "robe_of_the_magi"
    };

    private static readonly UnitMetaData[] DraftableUnits =
    {
        new(
            "Chad",
            "Chad",
            new AbilityMetaData("holylight", 12f, AbilityTargeting.GroundPlayerUnits, 0f),
            new AbilityMetaData(SoulSiphonAbilityId, 12f, AbilityTargeting.GroundEnemy, 0f),
            new AbilityMetaData("fireball", 12f, AbilityTargeting.GroundEnemy, 0f),
            BalanceBaseStatsByPoints(PrimaryAttribute.Agility, 0f, new BalancePointAllocations(7, 8, 9, 5, 3, 4, 2, 9, 3))),
        new(
            "Kevin",
            "Kevin",
            new AbilityMetaData(HealingWaveAbilityId, 12f, AbilityTargeting.GroundPlayerUnits, 0f),
            new AbilityMetaData("lightning", 12f, AbilityTargeting.GroundEnemy, 0f),
            null,
            BalanceBaseStatsByPoints(PrimaryAttribute.Strength, 0f, new BalancePointAllocations(2, 4, 6, 9, 6, 5, 8, 3, 3)))
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
            unitMetaDataByTypeId[metaData.UnitTypeId] = metaData;

        RegisterAbilities();
        InitializePlayers();

        gameApi.OnUnitDied += HandleUnitDied;
        gameApi.OnUnitDamaged += HandleUnitDamaged;
        gameApi.OnSpellCast += HandleSpellCast;
        gameApi.OnPlayerChatMessage += HandlePlayerChatMessage;

        gameApi.ShowSummaryTable("Chaos Arena", true);
        gameApi.SetSummaryTableHeaders("Player", "Kills", "Best Average Damage");
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

    private void RegisterAbilities()
    {
        gameApi.RegisterAbility(SoulSiphonAbilityId, "Soul Siphon", "Low health enemies in the area may be executed, granting wood.");
        gameApi.RegisterAbility(HealingWaveAbilityId, "Healing Wave", "Heals allied units in the area based on missing health.");

        foreach (UnitMetaData metaData in DraftableUnits)
        {
            foreach (Element element in Enum.GetValues<Element>())
            {
                AbilityMetaData? ability = metaData.GetAbility(element);
                if (ability != null)
                    gameApi.AddUnitTypeAbility(metaData.UnitTypeId, ability.AbilityId);
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

        IUnit builder = gameApi.SpawnUnitForPlayer(BuilderUnitTypeId, playerState.BasePosition, playerIndex);
        builder.Damage = 0f;
        builder.Invulnerable = true;
        playerState.Builder = builder;

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

        string offerList = string.Join(", ", playerState.Offers.Select((typeId, index) => $"{index + 1}: {typeId}"));
        gameApi.SendMessageToPlayer(playerIndex, $"Draft offers - {offerList}. Select a Circle of Power and type 'draft <number>'.");
    }

    private void UnlockPlayerTiles(int playerIndex, int count)
    {
        PlayerState playerState = playerStates[playerIndex];

        for (int tileCount = 0; tileCount < count && playerState.RemainingTilePositions.Count > 0; tileCount++)
        {
            int tileLevel = TilesPerPlayer - playerState.RemainingTilePositions.Count + 1;
            Vector3 position = playerState.RemainingTilePositions[0];
            playerState.RemainingTilePositions.RemoveAt(0);

            IUnit circle = gameApi.SpawnUnitForPlayer(CircleUnitTypeId, position, playerIndex);
            circle.Damage = 0f;
            circle.Speed = 0f;
            circle.Invulnerable = true;

            var state = new TileState { Kind = TileKind.Circle, CircleLevel = tileLevel };
            tileStatesByUnitId[circle.UniqueId] = state;

            PerformItemReroll(circle);
            PerformElementReroll(circle, state);
        }
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
            unit.AddItem(newItemId);
    }

    private void PerformElementReroll(IUnit unit, TileState state)
    {
        List<Element> candidateElements = Enum.GetValues<Element>().ToList();
        if (state.ElementAssigned)
            candidateElements.Remove(state.Element);

        state.Element = gameApi.PickRandom(candidateElements);
        state.ElementAssigned = true;
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
                gameApi.SetAbilityManaCost(unit, ability.AbilityId, ability.ManaCost);
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
    }

    private void ConfigureDraftedHero(IUnit hero, UnitMetaData metaData, int level, IEnumerable<string> itemIds, Element element)
    {
        ApplyHeroStats(hero, metaData, level);

        foreach (string itemId in itemIds)
            hero.AddItem(itemId);

        var state = new TileState { Kind = TileKind.Hero, Element = element, ElementAssigned = true };
        tileStatesByUnitId[hero.UniqueId] = state;

        ApplyElement(hero, state, true);
        LockTileUnit(hero);
    }

    private IEnumerable<IUnit> GetPlayerCircles(int playerIndex) =>
        gameApi.GetUnitsOwnedByPlayer(playerIndex, unit => unit.UnitId == CircleUnitTypeId && !unit.IsDead);

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

    private bool TryDraftFromOffer(int playerIndex, int offerNumber, IUnit? selectedUnit)
    {
        PlayerState playerState = playerStates[playerIndex];

        if (offerNumber < 1 || offerNumber > playerState.Offers.Count)
        {
            gameApi.SendMessageToPlayer(playerIndex, "Invalid draft offer.");
            return false;
        }

        if (playerState.DraftedCount >= foodCap)
        {
            gameApi.SendMessageToPlayer(playerIndex, "Food Cap Exceeded");
            return false;
        }

        if (gameSecondsElapsed < playerState.NextDraftSeconds)
        {
            gameApi.SendMessageToPlayer(playerIndex, $"Draft is on cooldown for {playerState.NextDraftSeconds - gameSecondsElapsed} more seconds.");
            return false;
        }

        IUnit? circle = selectedUnit != null && selectedUnit.UnitId == CircleUnitTypeId && selectedUnit.Player == playerIndex
            ? selectedUnit
            : null;

        if (circle == null)
        {
            gameApi.SendMessageToPlayer(playerIndex, "Must build on Circle of Power");
            return false;
        }

        DraftUnit(playerIndex, playerState.Offers[offerNumber - 1], circle);
        return true;
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
        playerState.DamageDealtPerUnitTypePerWave.Remove(unit.UnitId);

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
        PerformHeroReroll(unit, playerIndex);
    }

    private void SwapTileUnits(IUnit caster, IUnit target, int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];
        playerState.DamageDealtPerUnitTypePerWave.Remove(caster.UnitId);
        playerState.DamageDealtPerUnitTypePerWave.Remove(target.UnitId);

        gameApi.SwapUnitPositions(caster, target);
        gameApi.SelectUnit(caster);
    }

    private void HandlePlayerChatMessage(string message, IUnit? selectedUnit)
    {
        int playerIndex = selectedUnit != null && IsRealPlayerSlot(selectedUnit.Player)
            ? selectedUnit.Player
            : playerStates.Keys.OrderBy(index => index).FirstOrDefault(-1);

        if (playerIndex < 0 || !playerStates.ContainsKey(playerIndex))
            return;

        string[] tokens = message.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return;

        switch (tokens[0])
        {
            case "draft" when tokens.Length > 1 && int.TryParse(tokens[1], out int offerNumber):
                TryDraftFromOffer(playerIndex, offerNumber, selectedUnit);
                break;
            case "reroll" when tokens.Length > 1:
                HandleRerollCommand(playerIndex, tokens[1], selectedUnit);
                break;
            case "swap":
                HandleSwapCommand(playerIndex);
                break;
            case "upgrade" when tokens.Length > 1 && tokens[1] == "critical":
                gameApi.AddPlayerTechLevel(playerIndex, CriticalStrikeTechId);
                gameApi.AddPlayerTechLevel(playerIndex + ProxyPlayerOffset, CriticalStrikeTechId);
                gameApi.SendMessageToPlayer(playerIndex, "Critical Strike upgraded.");
                break;
            case "help":
                gameApi.SendMessageToPlayer(playerIndex, "Commands: draft <n>, reroll <item|element|hero|all>, swap (select two units), upgrade critical.");
                break;
        }
    }

    private void HandleRerollCommand(int playerIndex, string rerollKind, IUnit? selectedUnit)
    {
        if (selectedUnit == null
            || selectedUnit.Player != playerIndex
            || !tileStatesByUnitId.TryGetValue(selectedUnit.UniqueId, out TileState? state))
        {
            gameApi.SendMessageToPlayer(playerIndex, "Select one of your units or circles to reroll.");
            return;
        }

        bool isHero = state.Kind == TileKind.Hero;

        switch (rerollKind)
        {
            case "item":
                PerformItemReroll(selectedUnit);
                break;
            case "element":
                PerformElementReroll(selectedUnit, state);
                break;
            case "hero" when isHero:
                PerformHeroReroll(selectedUnit, playerIndex);
                break;
            case "all" when isHero:
                PerformAllReroll(selectedUnit, state, playerIndex);
                break;
            default:
                gameApi.SendMessageToPlayer(playerIndex, "Usage: reroll <item|element|hero|all> (hero and all require a drafted unit).");
                break;
        }
    }

    private void HandleSwapCommand(int playerIndex)
    {
        List<IUnit> selectedTileUnits = gameApi.GetSelectedUnits()
            .Where(unit => unit.Player == playerIndex && tileStatesByUnitId.ContainsKey(unit.UniqueId))
            .Take(2)
            .ToList();

        if (selectedTileUnits.Count < 2)
        {
            gameApi.SendMessageToPlayer(playerIndex, "Select two of your units or circles to swap.");
            return;
        }

        SwapTileUnits(selectedTileUnits[0], selectedTileUnits[1], playerIndex);
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

        foreach (IUnit draftedUnit in draftedUnits)
        {
            UnitMetaData metaData = unitMetaDataByTypeId[draftedUnit.UnitId];
            Vector3 spawnPosition = ToCoordinatePosition(draftedUnit.Position, playerState.SpawnOffset);

            IUnit clone = gameApi.SpawnUnitForPlayer(draftedUnit.UnitId, spawnPosition, proxyPlayerIndex);
            ApplyHeroStats(clone, metaData, draftedUnit.Level);

            foreach (string itemId in draftedUnit.GetItems())
                clone.AddItem(itemId);

            Element element = tileStatesByUnitId.TryGetValue(draftedUnit.UniqueId, out TileState? tileState)
                ? tileState.Element
                : Element.Water;

            var cloneState = new TileState { Kind = TileKind.Hero, Element = element, ElementAssigned = true };
            waveUnitStatesByUnitId[clone.UniqueId] = cloneState;
            ApplyElement(clone, cloneState, false);

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

        if (!waveUnitStatesByUnitId.TryGetValue(unit.UniqueId, out TileState? state))
            return;

        Element cycleElement = (Element)(aiCycle - 1);
        if (cycleElement != state.Element)
            return;

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

        if (spellId == SoulSiphonAbilityId)
            ApplySoulSiphon(caster, targetPosition);
        else if (spellId == HealingWaveAbilityId)
            ApplyHealingWave(caster, targetPosition);
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
            gameApi.SetSummaryTableRow(
                gameApi.GetPlayerName(playerIndex),
                kills.ToString(),
                GetBestAverageDamageDescription(playerStates[playerIndex]));
        }
    }

    private string GetBestAverageDamageDescription(PlayerState playerState)
    {
        string bestUnitTypeId = string.Empty;
        float bestAverageDamage = 0f;

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
            if (averageDamage > bestAverageDamage)
            {
                bestAverageDamage = averageDamage;
                bestUnitTypeId = entry.Key;
            }
        }

        return bestUnitTypeId.Length == 0 ? "-" : $"{bestUnitTypeId}: {(int)bestAverageDamage}";
    }
}
