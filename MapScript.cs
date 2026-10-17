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
    public int PlayerIndex { get; set; }
}

public class PlayerState
{
    public UnitId Builder { get; set; } = UnitId.Invalid;
    public UnitId Altar { get; set; } = UnitId.Invalid;
    public UnitId Blacksmith { get; set; } = UnitId.Invalid;
    public List<string> Deck { get; } = new();
    public List<string> Offers { get; } = new();
    public List<Vector3> TilePositions { get; } = new();
    public int DraftedCount { get; set; }
    public int NextDraftSeconds { get; set; }
    public Vector3 BasePosition { get; set; }
    public Vector3 SpawnPosition { get; set; }
    public int CountdownTextHandle { get; set; }
    public float SpawnAtSeconds { get; set; }
    public bool HasSpawnedThisRound { get; set; } = true;
    public bool IsCountdownVisible { get; set; }
    public Dictionary<int, List<UnitId>> SpawnedUnitsPerWave { get; } = new();
    public Dictionary<string, Dictionary<int, float>> DamageDealtPerUnitTypePerWave { get; } = new();
}

public class MapScript : IWasmModule
{
    private const int RealPlayerSlotCount = 12;
    private const int ProxyPlayerOffset = 12;
    private const float SpawnOffset = 30f;
    private const float TileOffset = 10f;
    private const float BuilderOffset = -5f;
    private const float BuildingOffset = -15f;
    private const float TileSpacing = 6.5f;
    private const float BuildingSpacing = 4f;
    private const float SoulSiphonRadius = 3f;
    private const float HealingWaveRadius = 6f;
    private const int WaveIntervalSeconds = 45;
    private const int WaveCountdownVisibleSeconds = 30;
    private const int SpawnSlotCount = 12;
    private const int TilesPerPlayer = 9;
    private const int CountdownFontSize = 216;

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

    private static readonly Element[] AllElements = Enum.GetValues<Element>();

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
    private readonly Dictionary<int, (int Player, string TemplateId)> _unitInfoCache = new();
    private int _tileUpdateTick;

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

        while (foodCap < FoodCapIncreaseMinutes.Length && gameSecondsElapsed >= nextFoodCapIncreaseSeconds)
            IncreaseFoodCap();

        _api.OnUnitDied += HandleUnitDied;
        _api.OnUnitDamaged += HandleUnitDamaged;
        _api.OnSpellCast += HandleSpellCast;
        _api.OnConstructionFinished += HandleConstructionFinished;
        _api.OnPlayerChatMessage += HandleChatMessage;
        _api.OnPlayerLeft += HandlePlayerLeft;

        _api.ShowSummaryTable("Chaos Arena - Combat Statistics", true);
        _api.SetSummaryTableHeaders("Player", "Kills", "Top Average Damage (Unit / Waves)");

        for (int slotIndex = 0; slotIndex < SpawnSlotCount; slotIndex++)
        {
            spawnOffsetSlots.Add(slotIndex * (float)WaveIntervalSeconds / SpawnSlotCount);
            spawnSlotOrder.Add(slotIndex);
        }

        _api.ScheduleRepeatingTimer(1f, HandleSpawnTimerTick);
        _api.ScheduleRepeatingTimer(1f, HandleGameTimerTick);
        _api.ScheduleRepeatingTimer(5f, GrantWoodPassive);
        _api.ScheduleRepeatingTimer(15f, ApplyNegativeHealthRegen);
        _api.ScheduleRepeatingTimer(60f, GrantPassiveExperience);
        _api.ScheduleRepeatingTimer(5f, UpdateSummaryTable);
        _api.ScheduleTimer(0f, UpdateSummaryTable);
        _api.ScheduleRepeatingTimer(5f, UpdateAliveUnitsLeaderboard);
        _api.ScheduleTimer(0f, UpdateAliveUnitsLeaderboard);
    }

    public void Update(IGameAPI api, float delta)
    {
        if (tileStatesByUnitId.Count == 0) return;

        int targetSlice = _tileUpdateTick++ % 30;
        int i = 0;
        foreach (KeyValuePair<int, TileState> entry in tileStatesByUnitId)
        {
            if (entry.Value.Kind != TileKind.Hero) continue;

            if ((i++ % 30) == targetSlice)
            {
                UnitId unit = (UnitId)entry.Key;
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
        playerIndex >= 1 && playerIndex <= RealPlayerSlotCount;

    private static bool IsProxyPlayerSlot(int playerIndex) =>
        playerIndex > ProxyPlayerOffset && playerIndex <= ProxyPlayerOffset + RealPlayerSlotCount;

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
        if (_api.GetPlayer(playerIndex).IsComputer)
            return;

        if (playerStates.TryGetValue(playerIndex, out PlayerState? playerState)
            && playerState.Builder.IsValid
            && !playerState.Builder.IsDead)
        {
            _api.GetPlayer(playerIndex).SelectUnit(playerState.Builder);
        }
    }

    private void SelectUnitForPlayer(int playerIndex, UnitId unit)
    {
        if (_api.GetPlayer(playerIndex).IsComputer)
            return;

        if (unit.IsValid && !unit.IsDead)
            _api.GetPlayer(playerIndex).SelectUnit(unit);
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
            }
        }
    }

    private void InitializeSinglePlayer(int playerIndex, bool updateAlliances = true)
    {
        if (playerStates.ContainsKey(playerIndex)) return;

        int proxyPlayerIndex = playerIndex + ProxyPlayerOffset;
        PlayerId proxyPlayer = _api.GetPlayer(proxyPlayerIndex);
        proxyPlayer.SetActive(true);
        proxyPlayer.IsComputer = true;

        var playerState = new PlayerState();
        playerStates[playerIndex] = playerState;
        playerState.BasePosition = GetPlayerBasePosition(playerIndex);
        playerState.SpawnPosition = GetPlayerOffset(playerState.BasePosition, SpawnOffset);
        playerState.CountdownTextHandle = _api.CreateStaticText(
            string.Empty,
            playerState.SpawnPosition,
            Vector3.One,
            CountdownFontSize);
        _api.SetStaticTextVisible(playerState.CountdownTextHandle, false);

        InitializePlayerTiles(playerIndex);
        InitializePlayerBase(playerIndex);
        InitializePlayerDeck(playerIndex);

        if (updateAlliances)
            InitializePlayerAlliances(playerStates.Keys.ToList());
    }

    private void InitializePlayers()
    {
        List<int> activePlayerIndices = _api.GetActivePlayerIndices().Where(IsRealPlayerSlot).ToList();

        foreach (int playerIndex in activePlayerIndices)
        {
            InitializeSinglePlayer(playerIndex, false);
        }

        InitializePlayerAlliances(playerStates.Keys.ToList());
    }

    private void HandleChatMessage(string message, UnitId senderUnit)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        string trimmed = message.Trim();

        if (trimmed.StartsWith("-kick", StringComparison.OrdinalIgnoreCase))
        {
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && int.TryParse(parts[1], out int targetSlot))
            {
                if (IsRealPlayerSlot(targetSlot))
                {
                    _api.KickPlayer(targetSlot, "Kicked by host");
                    CleanupPlayer(targetSlot);
                    _api.BroadcastMessage($"Player {targetSlot} was kicked.");
                }
                else
                {
                    _api.BroadcastMessage($"Invalid slot {targetSlot}. Must be 1-{RealPlayerSlotCount}.");
                }
            }
            else
            {
                _api.BroadcastMessage("Usage: -kick <slot>");
            }
        }
        else if (trimmed.StartsWith("-addai", StringComparison.OrdinalIgnoreCase))
        {
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int targetSlot = -1;
            if (parts.Length > 1 && int.TryParse(parts[1], out int parsedSlot))
            {
                targetSlot = parsedSlot;
            }
            else
            {
                for (int slot = 1; slot <= RealPlayerSlotCount; slot++)
                {
                    if (!playerStates.ContainsKey(slot) && !_api.IsPlayerActive(slot))
                    {
                        targetSlot = slot;
                        break;
                    }
                }
            }

            if (targetSlot >= 1 && targetSlot <= RealPlayerSlotCount)
            {
                if (_api.AddComputerPlayer(targetSlot, $"Bot {targetSlot}"))
                {
                    InitializeSinglePlayer(targetSlot);
                    _api.BroadcastMessage($"AI player added to slot {targetSlot}.");
                }
                else
                {
                    _api.BroadcastMessage($"Could not add AI to slot {targetSlot}.");
                }
            }
            else
            {
                _api.BroadcastMessage("No available slot found for AI.");
            }
        }
    }

    private void HandlePlayerLeft(int playerIndex)
    {
        int realSlot = ResolveRealPlayerIndex(playerIndex);
        if (playerStates.ContainsKey(realSlot))
        {
            CleanupPlayer(realSlot);
            _api.BroadcastMessage($"Player {realSlot} left the game.");
        }
    }

    private void CleanupPlayer(int playerIndex)
    {
        if (playerStates.TryGetValue(playerIndex, out PlayerState? playerState))
        {
            if (playerState.Builder.IsValid && !playerState.Builder.IsDead) _api.DestroyUnit(playerState.Builder);
            if (playerState.Altar.IsValid && !playerState.Altar.IsDead) _api.DestroyUnit(playerState.Altar);
            if (playerState.Blacksmith.IsValid && !playerState.Blacksmith.IsDead) _api.DestroyUnit(playerState.Blacksmith);
            if (playerState.CountdownTextHandle >= 0) _api.DestroyStaticText(playerState.CountdownTextHandle);

            foreach (var unit in _api.GetUnitsOwnedByPlayer(playerIndex))
            {
                _api.DestroyUnit(unit);
            }
            foreach (var unit in _api.GetUnitsOwnedByPlayer(playerIndex + ProxyPlayerOffset))
            {
                _api.DestroyUnit(unit);
            }

            playerStates.Remove(playerIndex);
        }
    }

    private Vector3 GetPlayerBasePosition(int playerIndex)
    {
        Vector3 startLocation = _api.GetPlayer(playerIndex).StartLocation;
        if (startLocation != Vector3.Zero)
            return startLocation;

        return _api.GetCoordinate($"PlayerStart{playerIndex}").Center;
    }

    private static Vector3 GetPlayerOffset(Vector3 basePosition, float delta)
    {
        return ToCoordinatePosition(basePosition, GetPlayerRelativeOffset(basePosition, delta));
    }

    private static Vector3 GetPlayerRelativeOffset(Vector3 basePosition, float delta)
    {
        float relativeX = basePosition.X - ArenaCenter.X;
        float relativeZ = basePosition.Z - ArenaCenter.Z;

        if (MathF.Abs(relativeX) >= MathF.Abs(relativeZ))
            return new Vector3(relativeX > 0f ? -delta : delta, 0f, 0f);

        return new Vector3(0f, 0f, relativeZ > 0f ? -delta : delta);
    }

    private void InitializePlayerAlliances(List<int> activePlayerIndices)
    {
        foreach (int playerIndex in activePlayerIndices)
        {
            PlayerId player = _api.GetPlayer(playerIndex);
            PlayerId proxyPlayer = _api.GetPlayer(playerIndex + ProxyPlayerOffset);
            proxyPlayer.SetActive(true);
            proxyPlayer.IsComputer = true;
            int team = player.Team;

            player.SetAllied(proxyPlayer, true);
            proxyPlayer.SetAllied(player, true);
            proxyPlayer.Team = team;

            foreach (int otherPlayerIndex in activePlayerIndices)
            {
                if (otherPlayerIndex == playerIndex || _api.GetPlayer(otherPlayerIndex).Team != team)
                    continue;

                PlayerId otherProxyPlayer = _api.GetPlayer(otherPlayerIndex + ProxyPlayerOffset);
                otherProxyPlayer.SetAllied(proxyPlayer, true);
                proxyPlayer.SetAllied(otherProxyPlayer, true);
            }
        }
    }

    private void InitializePlayerTiles(int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];
        Vector3 tileCenter = GetPlayerOffset(playerState.BasePosition, TileOffset);

        for (int columnIndex = -1; columnIndex <= 1; columnIndex++)
        {
            for (int rowIndex = -1; rowIndex <= 1; rowIndex++)
            {
                playerState.TilePositions.Add(new Vector3(
                    tileCenter.X + columnIndex * TileSpacing,
                    tileCenter.Y,
                    tileCenter.Z + rowIndex * TileSpacing));
            }
        }

        _api.Shuffle(playerState.TilePositions);
    }

    private void InitializePlayerBase(int playerIndex)
    {
        PlayerId player = _api.GetPlayer(playerIndex);
        PlayerState playerState = playerStates[playerIndex];
        Vector3 startLocation = playerState.BasePosition;
        Vector3 buildingOffset = GetPlayerOffset(startLocation, BuildingOffset);
        Vector3 builderOffset = GetPlayerOffset(startLocation, BuilderOffset);

        UnitId builder = player.SpawnUnit(UnitIds.Builder, builderOffset);
        builder.Damage = 0f;
        builder.Invulnerable = true;
        _api.SetUnitFacing(builder, ArenaCenter);
        playerState.Builder = builder;

        Vector3 altarOffset = MathF.Abs(buildingOffset.X) > MathF.Abs(buildingOffset.Z)
            ? new Vector3(0f, 0f, BuildingSpacing)
            : new Vector3(BuildingSpacing, 0f, 0f);

        Vector3 blacksmithOffset = MathF.Abs(buildingOffset.X) > MathF.Abs(buildingOffset.Z)
            ? new Vector3(0f, 0f, -BuildingSpacing)
            : new Vector3(-BuildingSpacing, 0f, 0f);

        UnitId altar = player.SpawnUnit(BuildingIds.Altar, ToCoordinatePosition(buildingOffset, altarOffset));
        altar.Damage = 0f;
        altar.Invulnerable = true;
        _api.SetUnitFacing(altar, ArenaCenter);
        playerState.Altar = altar;

        UnitId blacksmith = player.SpawnUnit(BuildingIds.Blacksmith, ToCoordinatePosition(buildingOffset, blacksmithOffset));
        blacksmith.Damage = 0f;
        blacksmith.Invulnerable = true;
        _api.SetUnitFacing(blacksmith, ArenaCenter);
        playerState.Blacksmith = blacksmith;

        UnlockPlayerTiles(playerIndex, 1);

        player.PanCameraTo(builder.Position, 0f);
        player.SelectUnit(builder);
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
            HashSet<string> placedHeroTypes = GetPlayerPlacedHeroes(playerIndex).Select(u => u.TemplateID).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

        if (!_api.GetPlayer(playerIndex).IsComputer)
        {
            string offerList = string.Join(", ", playerState.Offers.Select((typeId, index) =>
                $"{index + 1}: {_api.GetUnitName(typeId)}"));

            _api.GetPlayer(playerIndex).SendMessage($"Draft offers: {offerList}. Use the Builder [B] Build menu or select a Circle of Power / Altar to draft.");

            UpdateDraftOfferAbilities(playerIndex);
        }
    }

    private void UpdateDraftOfferAbilities(int playerIndex)
    {
        if (_api.GetPlayer(playerIndex).IsComputer)
            return;

        PlayerState playerState = playerStates[playerIndex];
        HashSet<string> offeredUnitTypeIds = new(playerState.Offers, StringComparer.OrdinalIgnoreCase);
        HashSet<string> offeredAbilityIds = new(StringComparer.OrdinalIgnoreCase);

        foreach (string offerTypeId in playerState.Offers)
        {
            foreach (string abilityId in _api.GetUnitTypeAbilities(offerTypeId))
                offeredAbilityIds.Add(abilityId);
        }

        if (playerState.Builder.IsValid && !playerState.Builder.IsDead)
        {
            foreach (string unitTypeId in DraftableUnits)
            {
                bool isOffered = offeredUnitTypeIds.Contains(unitTypeId);
                _api.SetAbilityState(playerState.Builder, unitTypeId, !isOffered, !isOffered);
            }
        }

        var unitsToConfigure = GetPlayerCircles(playerIndex).ToList();

        foreach (UnitId unit in unitsToConfigure)
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

        for (int tileCount = 0; tileCount < count && playerState.TilePositions.Count > 0; tileCount++)
        {
            int tileLevel = TilesPerPlayer - playerState.TilePositions.Count + 1;
            Vector3 position = playerState.TilePositions[0];
            playerState.TilePositions.RemoveAt(0);

            Element initialElement = AllElements[Random.Shared.Next(AllElements.Length)];
            string circleTypeId = GetCircleUnitTypeId(initialElement);
            UnitId circle = _api.GetPlayer(playerIndex).SpawnUnit(circleTypeId, position);
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
                AnchorPosition = position,
                PlayerIndex = playerIndex
            };
            tileStatesByUnitId[circle.UniqueId] = state;

            PerformItemReroll(circle);
            ApplyElement(circle, state, true);
        }

        UpdateDraftOfferAbilities(playerIndex);
    }

    private void PerformItemReroll(UnitId unit)
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
                : 1;
            unit.AddItem(newItemId, charges);
        }

        SelectUnitForPlayer(unit.Player, unit);
    }

    private UnitId PerformElementReroll(UnitId unit, TileState state)
    {
        List<Element> candidateElements = Enum.GetValues<Element>().ToList();
        if (state.ElementAssigned)
            candidateElements.Remove(state.Element);

        state.Element = _api.PickRandom(candidateElements);
        state.ElementAssigned = true;

        if (state.Kind == TileKind.Circle)
        {
            string newCircleTypeId = GetCircleUnitTypeId(state.Element);
            if (!unit.TemplateID.Equals(newCircleTypeId, StringComparison.OrdinalIgnoreCase))
            {
                int playerIndex = unit.Player;
                Vector3 position = state.AnchorPosition;
                var items = unit.GetItems().Select(id => (id, unit.GetItemCharges(id))).ToList();
                tileStatesByUnitId.Remove(unit.UniqueId);
                _api.DestroyUnit(unit, false, false);

                UnitId newCircle = _api.GetPlayer(playerIndex).SpawnUnit(newCircleTypeId, position);
                newCircle.Damage = 0f;
                newCircle.Speed = 0f;
                newCircle.Invulnerable = true;
                newCircle.HideHealthAndManaBars = true;
                _api.SetUnitFacing(newCircle, ArenaCenter);

                foreach (var (itemId, charges) in items)
                {
                    newCircle.AddItem(itemId, charges);
                }

                tileStatesByUnitId[newCircle.UniqueId] = state;
                unit = newCircle;
            }
        }

        ApplyElement(unit, state, true);
        SelectUnitForPlayer(unit.Player, unit);
        return unit;
    }

    private void ApplyElement(UnitId unit, TileState state, bool includeDisabledAbilities)
    {
        unit.Name = state.Kind == TileKind.Circle
            ? $"{state.Element} [Level {state.CircleLevel}]"
            : state.Element.ToString();

        if (state.Kind != TileKind.Hero)
            return;

        IReadOnlyList<string> abilities = _api.GetUnitTypeAbilities(unit.TemplateID);
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

    private static void ApplyHeroStats(UnitId unit, int level)
    {
        if (unit.IsHero)
            unit.Level = Math.Max(level, 1);
    }

    private void LockTileUnit(UnitId unit)
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

    private void ConfigureDraftedHero(UnitId hero, int level, IEnumerable<string> itemIds, Element element, Vector3 anchorPosition, int playerIndex)
    {
        ApplyHeroStats(hero, level);

        foreach (string itemId in itemIds)
        {
            int charges = Math.Max(level, 1);
            hero.AddItem(itemId, charges);
        }

        var state = new TileState
        {
            Kind = TileKind.Hero,
            Element = element,
            ElementAssigned = true,
            CircleLevel = level,
            AnchorPosition = anchorPosition,
            PlayerIndex = playerIndex
        };
        tileStatesByUnitId[hero.UniqueId] = state;

        ApplyElement(hero, state, true);
        LockTileUnit(hero);
    }

    private IEnumerable<UnitId> GetPlayerPlacedHeroes(int playerIndex)
    {
        foreach (KeyValuePair<int, TileState> entry in tileStatesByUnitId)
        {
            if (entry.Value.Kind == TileKind.Hero && entry.Value.PlayerIndex == playerIndex)
                yield return (UnitId)entry.Key;
        }
    }

    private bool IsCircleUnit(UnitId unit) =>
        (tileStatesByUnitId.TryGetValue(unit.UniqueId, out TileState? state) && state.Kind == TileKind.Circle)
        || unit.TemplateID.Contains("circle_of_power", StringComparison.OrdinalIgnoreCase)
        || unit.TemplateID.Contains("CircleOfPower", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<UnitId> GetPlayerCircles(int playerIndex)
    {
        foreach (KeyValuePair<int, TileState> entry in tileStatesByUnitId)
        {
            if (entry.Value.Kind == TileKind.Circle && entry.Value.PlayerIndex == playerIndex)
                yield return (UnitId)entry.Key;
        }
    }

    private HashSet<string> GetPlayerDraftedUnitTypeIds(int playerIndex) =>
        GetPlayerPlacedHeroes(playerIndex)
            .Select(unit => unit.TemplateID)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void DraftUnit(int playerIndex, string unitTypeId, UnitId circle)
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

        UnitId hero = _api.GetPlayer(playerIndex).SpawnUnit(unitTypeId, position);
        ConfigureDraftedHero(hero, circleLevel, itemIds, element, position, playerIndex);

        ResetPlayerSelection(playerIndex);

        DrawDraftOffers(playerIndex);
        UnlockPlayerTiles(playerIndex, 1);
    }

    private void PerformComputerDraft(int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];
        if (playerState.Offers.Count == 0)
            DrawDraftOffers(playerIndex);

        if (playerState.Offers.Count == 0)
            return;

        string chosenUnitTypeId = playerState.Offers[Random.Shared.Next(playerState.Offers.Count)];
        List<UnitId> circles = GetPlayerCircles(playerIndex).ToList();
        if (circles.Count == 0)
            return;

        UnitId chosenCircle = circles[Random.Shared.Next(circles.Count)];
        if (!chosenCircle.IsValid)
            return;

        DraftUnit(playerIndex, chosenUnitTypeId, chosenCircle);
    }

    private UnitId PerformHeroReroll(UnitId unit, int playerIndex)
    {
        PlayerId player = _api.GetPlayer(playerIndex);
        HashSet<string> draftedUnitTypeIds = GetPlayerDraftedUnitTypeIds(playerIndex);
        List<string> candidates = DraftableUnits.Where(id => !draftedUnitTypeIds.Contains(id)).ToList();
        string? replacement = _api.PickRandom(candidates);

        if (replacement == null)
        {
            player.SendMessage("No other heroes are available to reroll into.");
            return unit;
        }

        TileState state = tileStatesByUnitId[unit.UniqueId];
        Vector3 position = state.AnchorPosition;
        int level = state.CircleLevel;
        List<string> itemIds = unit.GetItems().ToList();

        PlayerState playerState = playerStates[playerIndex];
        if (playerState.DamageDealtPerUnitTypePerWave.TryGetValue(unit.TemplateID, out Dictionary<int, float>? damageRecords))
            damageRecords.Clear();

        tileStatesByUnitId.Remove(unit.UniqueId);
        _api.DestroyUnit(unit, false, false);

        UnitId newUnit = player.SpawnUnit(replacement, position);
        ConfigureDraftedHero(newUnit, level, itemIds, state.Element, position, playerIndex);
        SelectUnitForPlayer(playerIndex, newUnit);
        return newUnit;
    }

    private void PerformAllReroll(UnitId unit, TileState state, int playerIndex)
    {
        PerformItemReroll(unit);
        UnitId currentUnit = PerformElementReroll(unit, state);
        if (state.Kind == TileKind.Hero)
            currentUnit = PerformHeroReroll(currentUnit, playerIndex);
        SelectUnitForPlayer(playerIndex, currentUnit);
    }

    private void SwapTileUnits(UnitId caster, UnitId target, int playerIndex)
    {
        PlayerState playerState = playerStates[playerIndex];

        if (playerState.DamageDealtPerUnitTypePerWave.TryGetValue(caster.TemplateID, out Dictionary<int, float>? casterDamage))
            casterDamage.Clear();
        if (playerState.DamageDealtPerUnitTypePerWave.TryGetValue(target.TemplateID, out Dictionary<int, float>? targetDamage))
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

    private void ApplyTileLevelToUnit(UnitId unit, TileState state)
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
        }

        foreach (KeyValuePair<int, PlayerState> entry in playerStates)
        {
            PlayerState playerState = entry.Value;

            if (!playerState.HasSpawnedThisRound && roundClockSeconds >= playerState.SpawnAtSeconds)
            {
                playerState.HasSpawnedThisRound = true;
                if (_api.GetPlayer(entry.Key).IsActive)
                    SpawnWaveForPlayer(entry.Key);
            }

            float secondsUntilSpawn = playerState.SpawnAtSeconds - roundClockSeconds;
            bool isCountdownVisible = !playerState.HasSpawnedThisRound && secondsUntilSpawn <= WaveCountdownVisibleSeconds;
            if (isCountdownVisible)
                _api.SetStaticText(playerState.CountdownTextHandle, ((int)MathF.Ceiling(secondsUntilSpawn)).ToString());

            if (playerState.IsCountdownVisible != isCountdownVisible)
            {
                playerState.IsCountdownVisible = isCountdownVisible;
                _api.SetStaticTextVisible(playerState.CountdownTextHandle, isCountdownVisible);
            }
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
            playerState.SpawnedUnitsPerWave[waveNumber] = new List<UnitId>();

            if (!_api.GetPlayer(playerIndex).IsActive)
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
        PlayerId player = _api.GetPlayer(playerIndex);
        int proxyPlayerIndex = playerIndex + ProxyPlayerOffset;
        PlayerId proxyPlayer = _api.GetPlayer(proxyPlayerIndex);

        List<UnitId> draftedUnits = GetPlayerPlacedHeroes(playerIndex).ToList();
        Vector3 tileCenter = GetPlayerOffset(playerState.BasePosition, TileOffset);

        foreach (UpgradeDefinition upgrade in DraftableUpgrades)
        {
            int techLevel = player.GetTechLevel(upgrade.TechId);
            if (techLevel > 0)
                proxyPlayer.SetTechLevel(upgrade.TechId, techLevel);
        }

        foreach (UnitId draftedUnit in draftedUnits)
        {
            tileStatesByUnitId.TryGetValue(draftedUnit.UniqueId, out TileState? tileState);
            Vector3 anchorPos = tileState?.AnchorPosition ?? draftedUnit.Position;
            var unitOffset = anchorPos - tileCenter;
            Vector3 spawnPosition = ToCoordinatePosition(playerState.SpawnPosition, unitOffset);

            UnitId clone = proxyPlayer.SpawnUnit(draftedUnit.TemplateID, spawnPosition, executeSpawnShader: true, bypassPopulation: true);
            ApplyHeroStats(clone, tileState?.CircleLevel ?? draftedUnit.Level);
            _api.EnableUnitAutoCombat(clone, true);

            foreach (UpgradeDefinition upgrade in DraftableUpgrades)
            {
                int techLevel = player.GetTechLevel(upgrade.TechId);
                _api.SetAbilityState(clone, upgrade.AbilityId, techLevel <= 0, techLevel <= 0);
            }

            foreach (string itemId in draftedUnit.GetItems())
            {
                int charges = draftedUnit.GetItemCharges(itemId);
                clone.AddItem(itemId, charges);
            }

            Element element = tileState?.Element ?? Element.Water;

            var cloneState = new TileState { Kind = TileKind.Hero, Element = element, ElementAssigned = true, PlayerIndex = proxyPlayerIndex };
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

        while (foodCap < FoodCapIncreaseMinutes.Length && gameSecondsElapsed >= nextFoodCapIncreaseSeconds)
            IncreaseFoodCap();

        foreach (int playerIndex in playerStates.Keys)
        {
            if (_api.GetPlayer(playerIndex).IsComputer)
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
            PlayerId player = _api.GetPlayer(playerIndex);
            player.SetMaxPopulation(foodCap);

            if (player.IsComputer)
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
            _api.GetPlayer(playerIndex).AdjustWood(1f);
    }

    private void GrantPassiveExperience()
    {
        foreach (PlayerState playerState in playerStates.Values)
        {
            if (playerState.Builder.IsValid && !playerState.Builder.IsDead)
            {
                var builder = playerState.Builder;
                builder.Experience += 15f;
            }
        }
    }

    private void ApplyNegativeHealthRegen()
    {
        foreach (PlayerState playerState in playerStates.Values)
        {
            foreach (List<UnitId> waveUnits in playerState.SpawnedUnitsPerWave.Values)
            {
                for (int unitIndex = waveUnits.Count - 1; unitIndex >= 0; unitIndex--)
                {
                    UnitId unit = waveUnits[unitIndex];
                    if (unit.IsDead)
                    {
                        waveUnitSpawnTimes.Remove(unit.UniqueId);
                        waveUnits.RemoveAt(unitIndex);
                        continue;
                    }

                    if (!IsProxyPlayerSlot(unit.Player))
                        continue;

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

                    float damageMultiplier = IsDraftableUnit(unit.TemplateID)
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

    private void HandleSpellCast(UnitId caster, string spellId, Vector3 targetPosition)
    {
        if (!caster.IsValid)
            return;

        int playerIndex = ResolveRealPlayerIndex(caster.Player);
        PlayerId player = _api.GetPlayer(playerIndex);

        UpgradeDefinition? upgrade = DraftableUpgrades.FirstOrDefault(u => SlugsEqual(u.AbilityId, spellId));
        if (upgrade != null)
        {
            player.AddTechLevel(upgrade.TechId);
            _api.GetPlayer(playerIndex + ProxyPlayerOffset).AddTechLevel(upgrade.TechId);
            string upgradeName = _api.GetAbilityName(upgrade.AbilityId);
            player.SendMessage($"{upgradeName} upgraded (Level {player.GetTechLevel(upgrade.TechId)}).");
            return;
        }

        if (SlugsEqual(spellId, SoulSiphonAbilityId))
        {
            ApplySoulSiphon(caster, targetPosition);
        }
        else if (SlugsEqual(spellId, HealingWaveAbilityId))
        {
            ApplyHealingWave(caster, targetPosition);
        }
        else if (SlugsEqual(spellId, RerollItemAbilityId))
        {
            PerformItemReroll(caster);
        }
        else if (SlugsEqual(spellId, RerollElementAbilityId) && tileStatesByUnitId.TryGetValue(caster.UniqueId, out TileState? elementState))
        {
            PerformElementReroll(caster, elementState);
        }
        else if (SlugsEqual(spellId, RerollHeroAbilityId) && tileStatesByUnitId.TryGetValue(caster.UniqueId, out TileState? heroState) && heroState.Kind == TileKind.Hero)
        {
            PerformHeroReroll(caster, playerIndex);
        }
        else if (SlugsEqual(spellId, RerollAllAbilityId) && tileStatesByUnitId.TryGetValue(caster.UniqueId, out TileState? allState))
        {
            PerformAllReroll(caster, allState, playerIndex);
        }
        else if (SlugsEqual(spellId, SwapTileUnitsAbilityId))
        {
            HandleSwapAbilityCast(caster, targetPosition, playerIndex);
        }
        else
        {
            TryDraftFromAbilityCast(caster, spellId);
        }
    }

    private static string StripSlugPrefix(string slug)
    {
        string trimmed = slug.Trim();
        int separatorIndex = trimmed.LastIndexOf('/');
        return separatorIndex >= 0 ? trimmed[(separatorIndex + 1)..] : trimmed;
    }

    private static bool SlugsEqual(string? first, string? second)
    {
        if (first == null || second == null)
            return false;

        return string.Equals(
            StripSlugPrefix(first).Trim(),
            StripSlugPrefix(second).Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private void HandleSwapAbilityCast(UnitId caster, Vector3 targetPosition, int playerIndex)
    {
        UnitId targetTileUnit = UnitId.Invalid;
        foreach (UnitId u in _api.GetUnitsInRadius(targetPosition, 2f))
        {
            if (u.Player == playerIndex && u.UniqueId != caster.UniqueId && tileStatesByUnitId.ContainsKey(u.UniqueId))
            {
                targetTileUnit = u;
                break;
            }
        }

        if (targetTileUnit.IsValid)
            SwapTileUnits(caster, targetTileUnit, playerIndex);
    }

    private void TryDraftFromAbilityCast(UnitId caster, string spellId)
    {
        int playerIndex = ResolveRealPlayerIndex(caster.Player);
        if (!playerStates.TryGetValue(playerIndex, out PlayerState? playerState))
            return;

        PlayerId player = _api.GetPlayer(playerIndex);

        bool isCircle = IsCircleUnit(caster);
        bool isAltar = caster.TemplateID.Equals(BuildingIds.Altar, StringComparison.OrdinalIgnoreCase)
            || caster.TemplateID.Equals("altar", StringComparison.OrdinalIgnoreCase);

        UnitId draftTargetCircle = UnitId.Invalid;

        if (isCircle)
        {
            draftTargetCircle = caster;
        }
        else if (isAltar)
        {
            draftTargetCircle = GetPlayerCircles(playerIndex).FirstOrDefault();
        }

        if (!draftTargetCircle.IsValid)
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
            player.SendMessage($"{_api.GetUnitName(matchedUnitTypeId)} is not currently offered for draft.");
            return;
        }

        if (playerState.DraftedCount >= foodCap)
        {
            player.SendMessage("Food Cap Exceeded");
            return;
        }

        if (gameSecondsElapsed < playerState.NextDraftSeconds)
        {
            player.SendMessage($"Draft is on cooldown for {playerState.NextDraftSeconds - gameSecondsElapsed} more seconds.");
            return;
        }

        DraftUnit(playerIndex, matchedUnitTypeId, draftTargetCircle);
    }

    private void HandleConstructionFinished(UnitId constructedUnit)
    {
        if (!constructedUnit.IsValid || constructedUnit.IsDead)
            return;

        if (tileStatesByUnitId.ContainsKey(constructedUnit.UniqueId))
            return;

        string unitId = constructedUnit.TemplateID;
        if (!IsDraftableUnit(unitId))
            return;

        int playerIndex = ResolveRealPlayerIndex(constructedUnit.Player);
        if (!playerStates.TryGetValue(playerIndex, out PlayerState? playerState))
        {
            _api.DestroyUnit(constructedUnit, false, false);
            return;
        }

        PlayerId player = _api.GetPlayer(playerIndex);

        string matchedUnitTypeId = DraftableUnits.FirstOrDefault(u =>
            u.Equals(unitId, StringComparison.OrdinalIgnoreCase)
            || u.EndsWith($"/{unitId}", StringComparison.OrdinalIgnoreCase)
            || unitId.EndsWith($"/{u}", StringComparison.OrdinalIgnoreCase)) ?? unitId;

        Vector3 buildPos = constructedUnit.Position;
        _api.DestroyUnit(constructedUnit, false, false);

        if (!playerState.Offers.Contains(matchedUnitTypeId, StringComparer.OrdinalIgnoreCase))
        {
            player.SendMessage($"{_api.GetUnitName(matchedUnitTypeId)} is not currently offered for draft.");
            ResetPlayerSelection(playerIndex);
            return;
        }

        if (playerState.DraftedCount >= foodCap)
        {
            player.SendMessage("Food Cap Exceeded");
            ResetPlayerSelection(playerIndex);
            return;
        }

        if (gameSecondsElapsed < playerState.NextDraftSeconds)
        {
            player.SendMessage($"Draft is on cooldown for {playerState.NextDraftSeconds - gameSecondsElapsed} more seconds.");
            ResetPlayerSelection(playerIndex);
            return;
        }

        UnitId targetCircle = GetPlayerCircles(playerIndex)
            .OrderBy(c => Vector3.DistanceSquared(c.Position, buildPos))
            .FirstOrDefault();

        if (!targetCircle.IsValid)
        {
            player.SendMessage("No available Circle of Power to draft onto.");
            ResetPlayerSelection(playerIndex);
            return;
        }

        DraftUnit(playerIndex, matchedUnitTypeId, targetCircle);
    }

    private void ApplySoulSiphon(UnitId caster, Vector3 targetPosition)
    {
        int casterTeam = _api.GetPlayer(caster.Player).Team;
        int rewardedPlayerIndex = ResolveRealPlayerIndex(caster.Player);

        foreach (UnitId target in _api.GetUnitsInRadius(targetPosition, SoulSiphonRadius))
        {
            if (target.IsDead || _api.GetPlayer(target.Player).Team == casterTeam)
                continue;

            float healthPercent = 100f * target.Health / target.MaxHealth;
            float executeChance = 26f - healthPercent;
            if (executeChance < 0f)
                continue;

            if (_api.RandomInt(1, 100) <= executeChance)
            {
                _api.KillUnit(target);
                _api.GetPlayer(rewardedPlayerIndex).AdjustWood(1f);
            }
        }
    }

    private void ApplyHealingWave(UnitId caster, Vector3 targetPosition)
    {
        foreach (UnitId target in _api.GetUnitsInRadius(targetPosition, HealingWaveRadius))
        {
            if (target.IsDead || target.Player != caster.Player)
                continue;

            float healthPercent = 100f * target.Health / target.MaxHealth;
            float healAmount = (100f - healthPercent) * target.MaxHealth / 4f;
            _api.HealUnit(target, healAmount);
        }
    }

    private void HandleUnitDied(UnitId dyingUnit, UnitId killer)
    {
        tileStatesByUnitId.Remove(dyingUnit.UniqueId);
        waveUnitStatesByUnitId.Remove(dyingUnit.UniqueId);
        waveUnitSpawnTimes.Remove(dyingUnit.UniqueId);
        _unitInfoCache.Remove(dyingUnit.UniqueId);

        if (!killer.IsValid)
            return;

        int killerPlayerIndex = ResolveRealPlayerIndex(killer.Player);
        if (!playerStates.ContainsKey(killerPlayerIndex))
            return;

        PlayerId killerPlayer = _api.GetPlayer(killerPlayerIndex);
        killerPlayer.SetKills(killerPlayer.GetKills() + 1);
        killerPlayer.AdjustGold(1f);
    }

    private void HandleUnitDamaged(UnitId victim, UnitId attacker, float damage)
    {
        if (!attacker.IsValid)
            return;

        if (!_unitInfoCache.TryGetValue(attacker.UniqueId, out var info))
        {
            info = (attacker.Player, attacker.TemplateID);
            _unitInfoCache[attacker.UniqueId] = info;
        }

        int damagingPlayerIndex = ResolveRealPlayerIndex(info.Player);
        if (!playerStates.TryGetValue(damagingPlayerIndex, out PlayerState? playerState))
            return;

        if (!playerState.DamageDealtPerUnitTypePerWave.TryGetValue(info.TemplateId, out Dictionary<int, float>? damagePerWave))
        {
            damagePerWave = new Dictionary<int, float>();
            playerState.DamageDealtPerUnitTypePerWave[info.TemplateId] = damagePerWave;
        }

        damagePerWave.TryGetValue(waveNumber, out float existingDamage);
        damagePerWave[waveNumber] = existingDamage + damage;
    }

    private void UpdateSummaryTable()
    {
        _api.ClearSummaryTable();

        var rankedPlayers = playerStates.Keys
            .Select(playerIndex => (PlayerIndex: playerIndex, Kills: _api.GetPlayer(playerIndex).GetKills()))
            .OrderByDescending(entry => entry.Kills);

        foreach ((int playerIndex, int kills) in rankedPlayers)
        {
            string coloredPlayerName = _api.GetPlayer(playerIndex).ColoredName;
            string topDamageBreakdown = GetTopDamageBreakdown(playerStates[playerIndex]);

            _api.SetSummaryTableRow(
                coloredPlayerName,
                kills.ToString(),
                topDamageBreakdown);
        }
    }

    private void UpdateAliveUnitsLeaderboard()
    {
        _api.ClearLeaderboard();
        _api.SetLeaderboardVisible("Alive Units", true);

        var rankedPlayers = playerStates.Keys
            .Select(playerIndex =>
            {
                int proxyPlayerIndex = playerIndex + ProxyPlayerOffset;
                int aliveCount = _api.GetUnitsOwnedByPlayer(proxyPlayerIndex).Length;
                return (PlayerIndex: playerIndex, AliveCount: aliveCount);
            })
            .OrderByDescending(entry => entry.AliveCount);

        foreach ((int playerIndex, int aliveCount) in rankedPlayers)
        {
            PlayerId player = _api.GetPlayer(playerIndex);
            _api.AddLeaderboardRow(player.Name, aliveCount.ToString());
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
