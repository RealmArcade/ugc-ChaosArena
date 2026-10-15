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

public record UpgradeDefinition(string TechId, string AbilityId);

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
    private const float SpawnOffsetDistance = 1000f / WorldUnitsPerRealmUnit;
    private const float TileSpacing = 250f / WorldUnitsPerRealmUnit;
    private const float BuildingSpacing = 200f / WorldUnitsPerRealmUnit;
    private const float SoulSiphonRadius = 175f / WorldUnitsPerRealmUnit;
    private const float HealingWaveRadius = 350f / WorldUnitsPerRealmUnit;
    private const int WaveIntervalSeconds = 45;
    private const int WaveCountdownVisibleSeconds = 30;
    private const int SpawnSlotCount = 12;
    private const int TilesPerPlayer = 9;
    private const int CountdownFontSize = 216;
    private const float CountdownDistanceMultiplier = 3.75f;

    private const string RerollItemAbilityId = "ability/reroll_item";
    private const string RerollElementAbilityId = "ability/reroll_element";
    private const string RerollHeroAbilityId = "ability/reroll_hero";
    private const string RerollAllAbilityId = "ability/reroll_all";
    private const string SwapTileUnitsAbilityId = "ability/swap_tile_units";
    private const string UpgradeCriticalStrikeAbilityId = "ability/upgrade_critical_strike";

    private const string SoulSiphonAbilityId = "ability/soul_siphon";
    private const string HealingWaveAbilityId = "ability/healing_wave";
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
        ItemIds.TitanBelt,
        ItemIds.SwiftnessBoots,
        ItemIds.CowlOfTheWise,
        ItemIds.IronClaws,
        ItemIds.GauntletsOfPower,
        ItemIds.CrystalPendant,
        ItemIds.VitalityRing,
        ItemIds.RingOfProtection,
        ItemIds.TomeOfKnowledge
    };

    private static readonly string[] DraftableUnits =
    {
        UnitIds.ShadowStrider,
        UnitIds.VanguardSentinel,
        UnitIds.FlameArchon,
        UnitIds.FrostWarden,
        UnitIds.StormCaller,
        UnitIds.VerdantDruid,
        UnitIds.IronWarlord,
        UnitIds.ArcaneScholar,
        UnitIds.NetherAssassin,
        UnitIds.SunPriest,
        UnitIds.BloodBerserker,
        UnitIds.StoneGuardian,
        UnitIds.ChronoWeaver,
        UnitIds.DuneSkirmisher,
        UnitIds.CoralVanguard,
        UnitIds.CrystalWeaver,
        UnitIds.AstralParagon,
        UnitIds.LightbringerMonk,
        UnitIds.ShadowWeaver,
        UnitIds.TempestChaser
    };

    private static readonly HashSet<string> DraftableUnitSet = new(DraftableUnits, StringComparer.OrdinalIgnoreCase);

    private static readonly UpgradeDefinition[] DraftableUpgrades =
    {
        new(CriticalStrikeTechId, UpgradeCriticalStrikeAbilityId)
    };

    private readonly Dictionary<int, PlayerState> playerStates = new();
    private readonly Dictionary<int, TileState> tileStatesByUnitId = new();
    private readonly Dictionary<int, TileState> waveUnitStatesByUnitId = new();
    private readonly Dictionary<int, float> waveUnitSpawnTimes = new();

    private IGameAPI _api = null!;
    private int waveNumber;
    private float roundClockSeconds;
    private readonly List<float> spawnOffsetSlots = new();
    private readonly List<int> spawnSlotOrder = new();
    private int gameSecondsElapsed;
    private int foodCap;
    private int nextFoodCapIncreaseSeconds;

    public void Initialize(IGameAPI gameApi)
    {
        _api = gameApi;

        _api.DisableShroud();

        ConfigureDraftChoicesOnTiles();
        InitializePlayers();

        _api.OnUnitDied += HandleUnitDied;
        _api.OnUnitDamaged += HandleUnitDamaged;
        _api.OnSpellCast += HandleSpellCast;

        _api.ShowSummaryTable("Chaos Arena - Combat Statistics", true);
        _api.SetSummaryTableHeaders("Player", "Kills", "Top Average Damage (Unit / Waves)");

        for (int slotIndex = 0; slotIndex < SpawnSlotCount; slotIndex++)
        {
            spawnOffsetSlots.Add(slotIndex * (float)WaveIntervalSeconds / SpawnSlotCount);
            spawnSlotOrder.Add(slotIndex);
        }

        _api.StartCountdownTimer(WaveIntervalSeconds, "Next wave round");

        _api.ScheduleRepeatingTimer(1f, HandleSpawnTimerTick);
        _api.ScheduleRepeatingTimer(1f, HandleGameTimerTick);
        _api.ScheduleRepeatingTimer(5f, GrantWoodPassive);
        _api.ScheduleRepeatingTimer(15f, ApplyNegativeHealthRegen);
        _api.ScheduleRepeatingTimer(60f, GrantPassiveExperience);
        _api.ScheduleRepeatingTimer(5f, UpdateSummaryTable);
        _api.ScheduleTimer(0f, UpdateSummaryTable);
    }

    public void Update(IGameAPI api, float delta)
    {
        foreach (KeyValuePair<int, TileState> entry in tileStatesByUnitId)
        {
            IUnit? unit = _api.GetUnitById(entry.Key);
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

    private static bool IsDraftableUnit(string unitId) =>
        DraftableUnitSet.Contains(unitId)
        || (unitId.StartsWith("unit/", StringComparison.OrdinalIgnoreCase) && DraftableUnitSet.Contains(unitId))
        || (!unitId.StartsWith("unit/", StringComparison.OrdinalIgnoreCase) && DraftableUnitSet.Contains($"unit/{unitId}"));

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
        Element.Water => BuildingIds.WaterElement,
        Element.Fire => BuildingIds.FireElement,
        _ => BuildingIds.EarthElement
    };

    private void ResetPlayerSelection(int playerIndex)
    {
        if (playerStates.TryGetValue(playerIndex, out PlayerState? playerState)
            && playerState.Builder != null
            && !playerState.Builder.IsDead)
        {
            _api.SelectUnit(playerState.Builder);
        }
    }

    private void ConfigureDraftChoicesOnTiles()
    {
        foreach (string unitTypeId in DraftableUnits)
        {
            foreach (string abilityId in _api.GetUnitTypeAbilities(unitTypeId))
            {
                _api.AddUnitTypeAbility(BuildingIds.EarthElement, abilityId);
                _api.AddUnitTypeAbility(BuildingIds.FireElement, abilityId);
                _api.AddUnitTypeAbility(BuildingIds.WaterElement, abilityId);
                _api.AddUnitTypeAbility(BuildingIds.Altar, abilityId);
            }
        }
    }

    private void InitializePlayers()
    {
        List<int> activePlayerIndices = _api.GetActivePlayerIndices(RealPlayerSlotCount).ToList();

        foreach (int playerIndex in activePlayerIndices)
        {
            int proxyPlayerIndex = playerIndex + ProxyPlayerOffset;
            _api.SetPlayerComputerControlled(proxyPlayerIndex, true);

            var playerState = new PlayerState();
            playerStates[playerIndex] = playerState;
            playerState.BasePosition = GetPlayerBasePosition(playerIndex);
            playerState.SpawnOffset = GetSpawnOffset(playerState.BasePosition);
            playerState.CountdownTextHandle = _api.CreateStaticText(
                string.Empty,
                ToCoordinatePosition(playerState.BasePosition, playerState.SpawnOffset * CountdownDistanceMultiplier),
                Vector3.One,
                CountdownFontSize);
            _api.SetStaticTextVisible(playerState.CountdownTextHandle, false);
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
        Vector3 startLocation = _api.GetPlayerStartLocation(playerIndex);
        if (startLocation != Vector3.Zero)
            return startLocation;

        return _api.GetCoordinate($"PlayerStart{playerIndex + 1}").Center;
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
            int team = _api.GetPlayerTeam(playerIndex);

            _api.SetPlayersAllied(playerIndex, proxyPlayerIndex, true);
            _api.SetPlayersAllied(proxyPlayerIndex, playerIndex, true);
            _api.SetPlayerTeam(proxyPlayerIndex, team);

            foreach (int otherPlayerIndex in activePlayerIndices)
            {
                if (otherPlayerIndex == playerIndex || _api.GetPlayerTeam(otherPlayerIndex) != team)
                    continue;

                int otherProxyPlayerIndex = otherPlayerIndex + ProxyPlayerOffset;
                _api.SetPlayersAllied(otherProxyPlayerIndex, proxyPlayerIndex, true);
                _api.SetPlayersAllied(proxyPlayerIndex, otherProxyPlayerIndex, true);
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

        _api.Shuffle(playerState.RemainingTilePositions);
    }

    private void InitializePlayerBase(int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];
        Vector3 startLocation = playerState.BasePosition;
        Vector3 offset = playerState.SpawnOffset;

        IUnit builder = _api.SpawnUnitForPlayer(UnitIds.Builder, startLocation, playerIndex);
        builder.Damage = 0f;
        builder.Invulnerable = true;
        builder.Speed = 0f;
        _api.SetUnitFacing(builder, ArenaCenter);
        playerState.Builder = builder;

        Vector3 buildingStart = new(startLocation.X - offset.X * 0.5f, startLocation.Y, startLocation.Z - offset.Z * 0.5f);
        Vector3 altarOffset = MathF.Abs(offset.X) > MathF.Abs(offset.Z)
            ? new Vector3(0f, 0f, BuildingSpacing)
            : new Vector3(BuildingSpacing, 0f, 0f);

        Vector3 blacksmithOffset = MathF.Abs(offset.X) > MathF.Abs(offset.Z)
            ? new Vector3(0f, 0f, -BuildingSpacing)
            : new Vector3(-BuildingSpacing, 0f, 0f);

        IUnit altar = _api.SpawnUnitForPlayer(BuildingIds.Altar, buildingStart + altarOffset, playerIndex);
        altar.Damage = 0f;
        altar.Invulnerable = true;
        _api.SetUnitFacing(altar, ArenaCenter);
        playerState.Altar = altar;

        IUnit blacksmith = _api.SpawnUnitForPlayer(BuildingIds.Blacksmith, buildingStart + blacksmithOffset, playerIndex);
        blacksmith.Damage = 0f;
        blacksmith.Invulnerable = true;
        _api.SetUnitFacing(blacksmith, ArenaCenter);
        playerState.Blacksmith = blacksmith;

        UnlockPlayerTiles(playerIndex, 1);

        _api.PanCameraTo(builder.Position, 0f);
        _api.SelectUnit(builder);
    }

    private void InitializePlayerDeck(int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];
        playerState.Deck.AddRange(DraftableUnits);
        _api.Shuffle(playerState.Deck);
        DrawDraftOffers(playerIndex);
    }

    private void DrawDraftOffers(int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];
        playerState.Offers.Clear();

        if (playerState.Deck.Count < 3)
        {
            HashSet<string> placedHeroTypes = GetPlayerPlacedHeroes(playerIndex).Select(u => u.UnitId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<string> replenishments = DraftableUnits.Where(u => !placedHeroTypes.Contains(u) && !playerState.Deck.Contains(u)).ToList();
            _api.Shuffle(replenishments);
            playerState.Deck.AddRange(replenishments);
        }

        for (int drawIndex = 0; drawIndex < 3 && playerState.Deck.Count > 0; drawIndex++)
        {
            playerState.Offers.Add(playerState.Deck[0]);
            playerState.Deck.RemoveAt(0);
        }

        if (playerState.Offers.Count == 0)
            return;

        string offerList = string.Join(", ", playerState.Offers.Select((typeId, index) =>
            $"{index + 1}: {_api.GetUnitName(typeId)}"));

        _api.SendMessageToPlayer(playerIndex, $"Draft offers at Altar: {offerList}. Select a Circle of Power or Altar to draft.");

        UpdateDraftOfferAbilities(playerIndex);
    }

    private void UpdateDraftOfferAbilities(int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];
        HashSet<string> offeredAbilityIds = new(StringComparer.OrdinalIgnoreCase);

        foreach (string offerTypeId in playerState.Offers)
        {
            foreach (string abilityId in _api.GetUnitTypeAbilities(offerTypeId))
                offeredAbilityIds.Add(abilityId);
        }

        var unitsToConfigure = GetPlayerCircles(playerIndex).ToList();
        if (playerState.Altar != null && !playerState.Altar.IsDead)
            unitsToConfigure.Add(playerState.Altar);

        foreach (IUnit unit in unitsToConfigure)
        {
            foreach (string unitTypeId in DraftableUnits)
            {
                foreach (string abilityId in _api.GetUnitTypeAbilities(unitTypeId))
                {
                    bool isOffered = offeredAbilityIds.Contains(abilityId);
                    _api.SetAbilityState(unit, abilityId, !isOffered, !isOffered);
                    if (isOffered)
                        _api.SetAbilityManaCost(unit, abilityId, 0f);
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

            Element initialElement = _api.PickRandom(Enum.GetValues<Element>().ToList());
            string circleTypeId = GetCircleUnitTypeId(initialElement);
            IUnit circle = _api.SpawnUnitForPlayer(circleTypeId, position, playerIndex);
            circle.Damage = 0f;
            circle.Speed = 0f;
            circle.Invulnerable = true;
            circle.HideHealthAndManaBars = true;
            _api.SetUnitFacing(circle, ArenaCenter);

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
        string? newItemId = _api.PickRandom(availableItemIds) ?? previousItemId;
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

        state.Element = _api.PickRandom(candidateElements);
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
                _api.DestroyUnit(unit, false, false);

                IUnit newCircle = _api.SpawnUnitForPlayer(newCircleTypeId, position, playerIndex);
                newCircle.Damage = 0f;
                newCircle.Speed = 0f;
                newCircle.Invulnerable = true;
                newCircle.HideHealthAndManaBars = true;
                _api.SetUnitFacing(newCircle, ArenaCenter);

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

        if (state.Kind != TileKind.Hero)
            return;

        IReadOnlyList<string> abilities = _api.GetUnitTypeAbilities(unit.UnitId);
        // Ordered abilities: [0]=Water, [1]=Earth, [2]=Fire, [3]=Ultimate
        for (int i = 0; i < 3 && i < abilities.Count; i++)
        {
            Element element = (Element)i;
            string abilityId = abilities[i];
            if (string.IsNullOrEmpty(abilityId))
                continue;

            bool isActive = element == state.Element;
            if (!isActive && !includeDisabledAbilities)
                continue;

            _api.SetAbilityState(unit, abilityId, !isActive, false);
            string abilityName = _api.GetAbilityName(abilityId);
            if (isActive)
                _api.SetAbilityTooltip(abilityId, $"[{element}] {abilityName}");
            else
                _api.SetAbilityTooltip(abilityId, $"[{element}] {abilityName} (disabled)");
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
        _api.SetUnitFacing(unit, ArenaCenter);
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

    private IEnumerable<IUnit> GetPlayerPlacedHeroes(int playerIndex)
    {
        foreach (KeyValuePair<int, TileState> entry in tileStatesByUnitId)
        {
            if (entry.Value.Kind != TileKind.Hero)
                continue;

            IUnit? unit = _api.GetUnitById(entry.Key);
            if (unit != null && !unit.IsDead && unit.Player == playerIndex)
                yield return unit;
        }
    }

    private bool IsCircleUnit(IUnit unit) =>
        (tileStatesByUnitId.TryGetValue(unit.UniqueId, out TileState? state) && state.Kind == TileKind.Circle)
        || unit.UnitId.Contains("circle_of_power", StringComparison.OrdinalIgnoreCase)
        || unit.UnitId.Contains("CircleOfPower", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<IUnit> GetPlayerCircles(int playerIndex)
    {
        foreach (KeyValuePair<int, TileState> entry in tileStatesByUnitId)
        {
            if (entry.Value.Kind != TileKind.Circle)
                continue;

            IUnit? unit = _api.GetUnitById(entry.Key);
            if (unit != null && !unit.IsDead && unit.Player == playerIndex)
                yield return unit;
        }
    }

    private HashSet<string> GetPlayerDraftedUnitTypeIds(int playerIndex) =>
        GetPlayerPlacedHeroes(playerIndex)
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
        _api.DestroyUnit(circle, false, false);

        IUnit hero = _api.SpawnUnitForPlayer(unitTypeId, position, playerIndex);
        ConfigureDraftedHero(hero, circleLevel, itemIds, element, position);

        ResetPlayerSelection(playerIndex);

        DrawDraftOffers(playerIndex);
        UnlockPlayerTiles(playerIndex, 1);
    }

    private void PerformComputerDraft(int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];
        if (playerState.Offers.Count == 0)
            DrawDraftOffers(playerIndex);

        string? chosenUnitTypeId = _api.PickRandom(playerState.Offers);
        IUnit? chosenCircle = _api.PickRandom(GetPlayerCircles(playerIndex).ToList());

        if (chosenUnitTypeId == null || chosenCircle == null)
            return;

        DraftUnit(playerIndex, chosenUnitTypeId, chosenCircle);
    }

    private void PerformHeroReroll(IUnit unit, int playerIndex)
    {
        HashSet<string> draftedUnitTypeIds = GetPlayerDraftedUnitTypeIds(playerIndex);
        List<string> candidates = DraftableUnits.Where(id => !draftedUnitTypeIds.Contains(id)).ToList();
        string? replacement = _api.PickRandom(candidates);

        if (replacement == null)
        {
            _api.SendMessageToPlayer(playerIndex, "No other heroes are available to reroll into.");
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
        _api.DestroyUnit(unit, false, false);

        IUnit newUnit = _api.SpawnUnitForPlayer(replacement, position, playerIndex);
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

        _api.SwapUnitPositions(caster, target);
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
            _api.StartCountdownTimer(WaveIntervalSeconds, "Next wave round");
        }

        foreach (KeyValuePair<int, PlayerState> entry in playerStates)
        {
            PlayerState playerState = entry.Value;

            if (!playerState.HasSpawnedThisRound && roundClockSeconds >= playerState.SpawnAtSeconds)
            {
                playerState.HasSpawnedThisRound = true;
                if (_api.IsPlayerActive(entry.Key))
                    SpawnWaveForPlayer(entry.Key);
            }

            float secondsUntilSpawn = playerState.SpawnAtSeconds - roundClockSeconds;
            bool isCountdownVisible = !playerState.HasSpawnedThisRound && secondsUntilSpawn <= WaveCountdownVisibleSeconds;
            if (isCountdownVisible)
                _api.SetStaticText(playerState.CountdownTextHandle, ((int)MathF.Ceiling(secondsUntilSpawn)).ToString());
            _api.SetStaticTextVisible(playerState.CountdownTextHandle, isCountdownVisible);
        }
    }

    private void StartWaveRound()
    {
        waveNumber++;

        _api.Shuffle(spawnSlotOrder);

        int slotCursor = 0;
        foreach (int playerIndex in playerStates.Keys.OrderBy(index => index))
        {
            PlayerState playerState = playerStates[playerIndex];
            playerState.SpawnedUnitsPerWave[waveNumber] = new List<IUnit>();

            if (!_api.IsPlayerActive(playerIndex))
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

        List<IUnit> draftedUnits = GetPlayerPlacedHeroes(playerIndex).ToList();

        foreach (IUnit draftedUnit in draftedUnits)
        {
            Vector3 spawnPosition = ToCoordinatePosition(draftedUnit.Position, playerState.SpawnOffset);

            IUnit clone = _api.SpawnUnitForPlayer(draftedUnit.UnitId, spawnPosition, proxyPlayerIndex);
            ApplyHeroStats(clone, draftedUnit.Level);
            _api.EnableUnitAutoCombat(clone, true);

            foreach (UpgradeDefinition upgrade in DraftableUpgrades)
            {
                int techLevel = _api.GetPlayerTechLevel(playerIndex, upgrade.TechId);
                if (techLevel > 0)
                {
                    _api.SetPlayerTechLevel(proxyPlayerIndex, upgrade.TechId, techLevel);
                    _api.SetAbilityState(clone, upgrade.AbilityId, false, false);
                }
                else
                {
                    _api.SetAbilityState(clone, upgrade.AbilityId, true, true);
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

            _api.SetUnitFacing(clone, ArenaCenter);
            clone.AttackMove(ArenaCenter);
            playerState.SpawnedUnitsPerWave[waveNumber].Add(clone);
            waveUnitSpawnTimes[clone.UniqueId] = gameSecondsElapsed;
        }
    }

    private void HandleGameTimerTick()
    {
        gameSecondsElapsed++;

        if (gameSecondsElapsed >= nextFoodCapIncreaseSeconds)
            IncreaseFoodCap();

        foreach (int playerIndex in playerStates.Keys)
        {
            if (_api.IsPlayerComputer(playerIndex))
            {
                PlayerState playerState = playerStates[playerIndex];
                while (playerState.DraftedCount < foodCap && GetPlayerCircles(playerIndex).Any())
                {
                    PerformComputerDraft(playerIndex);
                }
            }
        }
    }

    private void IncreaseFoodCap()
    {
        if (foodCap >= FoodCapIncreaseMinutes.Length)
            return;

        foodCap++;
        nextFoodCapIncreaseSeconds = GetNextDraftUnlockTimestamp(foodCap);

        foreach (int playerIndex in playerStates.Keys.ToList())
        {
            _api.SetPlayerMaxPopulation(playerIndex, foodCap);

            if (_api.IsPlayerComputer(playerIndex))
            {
                PlayerState playerState = playerStates[playerIndex];
                while (playerState.DraftedCount < foodCap && GetPlayerCircles(playerIndex).Any())
                {
                    PerformComputerDraft(playerIndex);
                }
            }
        }
    }

    private void GrantWoodPassive()
    {
        foreach (int playerIndex in playerStates.Keys)
            _api.AdjustPlayerWood(playerIndex, 1f);
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
            foreach (List<IUnit> waveUnits in playerState.SpawnedUnitsPerWave.Values)
            {
                for (int unitIndex = waveUnits.Count - 1; unitIndex >= 0; unitIndex--)
                {
                    IUnit unit = waveUnits[unitIndex];
                    if (unit.IsDead)
                    {
                        waveUnitSpawnTimes.Remove(unit.UniqueId);
                        waveUnits.RemoveAt(unitIndex);
                        continue;
                    }

                    if (!waveUnitSpawnTimes.TryGetValue(unit.UniqueId, out float spawnTime))
                    {
                        spawnTime = gameSecondsElapsed;
                        waveUnitSpawnTimes[unit.UniqueId] = spawnTime;
                    }

                    float battleDuration = gameSecondsElapsed - spawnTime;
                    if (battleDuration <= WaveIntervalSeconds)
                        continue;

                    float overtimeSeconds = battleDuration - WaveIntervalSeconds;
                    float overtimeWaves = overtimeSeconds / WaveIntervalSeconds;
                    float baseDamageMultiplier = MathF.Pow(1.1f, MathF.Max(1f, overtimeWaves)) - 1f;

                    float damageMultiplier = IsDraftableUnit(unit.UnitId)
                        ? baseDamageMultiplier
                        : baseDamageMultiplier * 2f;

                    _api.DealDamage(unit, unit, unit.MaxHealth * damageMultiplier);
                    if (!unit.HasBuff(AntiSnowballPoisonBuffId))
                        unit.AddBuff(AntiSnowballPoisonBuffId, float.PositiveInfinity);

                    if (unit.IsDead)
                    {
                        waveUnitSpawnTimes.Remove(unit.UniqueId);
                        waveUnits.RemoveAt(unitIndex);
                    }
                }
            }
        }
    }

    private void HandleSpellCast(IUnit? caster, string spellId, Vector3 targetPosition)
    {
        if (caster == null)
            return;

        int playerIndex = ResolveRealPlayerIndex(caster.Player);

        UpgradeDefinition? upgrade = DraftableUpgrades.FirstOrDefault(u => u.AbilityId.Equals(spellId, StringComparison.OrdinalIgnoreCase));
        if (upgrade != null)
        {
            _api.AddPlayerTechLevel(playerIndex, upgrade.TechId);
            _api.AddPlayerTechLevel(playerIndex + ProxyPlayerOffset, upgrade.TechId);
            string upgradeName = _api.GetAbilityName(upgrade.AbilityId);
            _api.SendMessageToPlayer(playerIndex, $"{upgradeName} upgraded (Level {_api.GetPlayerTechLevel(playerIndex, upgrade.TechId)}).");
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
        IUnit? targetTileUnit = _api.GetUnitsInRadius(targetPosition, 2f)
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
        bool isAltar = caster.UnitId.Equals(BuildingIds.Altar, StringComparison.OrdinalIgnoreCase)
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
        foreach (string unitTypeId in DraftableUnits)
        {
            var abilities = _api.GetUnitTypeAbilities(unitTypeId);
            if (abilities.Any(a => a.Equals(spellId, StringComparison.OrdinalIgnoreCase)
                || a.EndsWith($"/{spellId}", StringComparison.OrdinalIgnoreCase)
                || spellId.EndsWith($"/{a}", StringComparison.OrdinalIgnoreCase)))
            {
                matchedUnitTypeId = unitTypeId;
                break;
            }
        }

        if (matchedUnitTypeId == null)
            return;

        if (!playerState.Offers.Contains(matchedUnitTypeId)
            && !playerState.Offers.Any(o => matchedUnitTypeId.EndsWith(o, StringComparison.OrdinalIgnoreCase)))
        {
            _api.SendMessageToPlayer(playerIndex, $"{_api.GetUnitName(matchedUnitTypeId)} is not currently offered for draft.");
            return;
        }

        if (playerState.DraftedCount >= foodCap)
        {
            _api.SendMessageToPlayer(playerIndex, "Food Cap Exceeded");
            return;
        }

        if (gameSecondsElapsed < playerState.NextDraftSeconds)
        {
            _api.SendMessageToPlayer(playerIndex, $"Draft is on cooldown for {playerState.NextDraftSeconds - gameSecondsElapsed} more seconds.");
            return;
        }

        DraftUnit(playerIndex, matchedUnitTypeId, draftTargetCircle);
    }

    private void ApplySoulSiphon(IUnit caster, Vector3 targetPosition)
    {
        int casterTeam = _api.GetPlayerTeam(caster.Player);
        int rewardedPlayerIndex = ResolveRealPlayerIndex(caster.Player);

        foreach (IUnit target in _api.GetUnitsInRadius(targetPosition, SoulSiphonRadius).ToList())
        {
            if (target.IsDead || _api.GetPlayerTeam(target.Player) == casterTeam)
                continue;

            float healthPercent = 100f * target.Health / target.MaxHealth;
            float executeChance = 26f - healthPercent;
            if (executeChance < 0f)
                continue;

            if (_api.RandomInt(1, 100) <= executeChance)
            {
                _api.KillUnit(target);
                _api.AdjustPlayerWood(rewardedPlayerIndex, 1f);
            }
        }
    }

    private void ApplyHealingWave(IUnit caster, Vector3 targetPosition)
    {
        foreach (IUnit target in _api.GetUnitsInRadius(targetPosition, HealingWaveRadius).ToList())
        {
            if (target.IsDead || target.Player != caster.Player)
                continue;

            float healthPercent = 100f * target.Health / target.MaxHealth;
            float healAmount = (100f - healthPercent) * target.MaxHealth / 4f;
            _api.HealUnit(target, healAmount);
        }
    }

    private void HandleUnitDied(IUnit dyingUnit, IUnit? killer)
    {
        tileStatesByUnitId.Remove(dyingUnit.UniqueId);
        waveUnitStatesByUnitId.Remove(dyingUnit.UniqueId);
        waveUnitSpawnTimes.Remove(dyingUnit.UniqueId);

        if (killer == null)
            return;

        int killerPlayerIndex = ResolveRealPlayerIndex(killer.Player);
        if (!playerStates.ContainsKey(killerPlayerIndex))
            return;

        _api.SetPlayerKills(killerPlayerIndex, _api.GetPlayerKills(killerPlayerIndex) + 1);
        _api.AdjustPlayerGold(killerPlayerIndex, 1f);
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
        _api.ClearSummaryTable();

        var rankedPlayers = playerStates.Keys
            .Select(playerIndex => (PlayerIndex: playerIndex, Kills: _api.GetPlayerKills(playerIndex)))
            .OrderByDescending(entry => entry.Kills);

        foreach ((int playerIndex, int kills) in rankedPlayers)
        {
            string coloredPlayerName = _api.FormatColoredPlayerName(playerIndex);
            string topDamageBreakdown = GetTopDamageBreakdown(playerStates[playerIndex]);

            _api.SetSummaryTableRow(
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
            string displayName = _api.GetUnitName(entry.Key);

            summaries.Add((displayName, averageDamage));
        }

        if (summaries.Count == 0)
            return "-";

        var ordered = summaries.OrderByDescending(s => s.AvgDamage).Take(3);
        return string.Join(" | ", ordered.Select(s => $"{s.UnitName}: {(int)s.AvgDamage}"));
    }
}
