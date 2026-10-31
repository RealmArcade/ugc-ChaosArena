namespace Realm.Maps;

using System;
using System.Collections.Generic;
using System.Text;
using Realm.MapAPI;

public partial class MapScript
{
    private const int MatchWaveCutoffSeconds = 25 * 60;
    private const int MatchHardEndSeconds = 30 * 60;
    private const string PlacementLeaderboardId = "placement";

    private readonly List<int> _standingPlayers = new();
    private readonly List<int> _waveTargetRoster = new();
    private readonly List<int> _standingsOrder = new();

    private bool matchComplete;
    private bool wavesClosedAnnounced;

    private void EvaluateEliminations()
    {

        if (matchComplete || gameSecondsElapsed < MatchWaveCutoffSeconds)
            return;

        CollectStandingPlayers();
        if (_standingPlayers.Count <= 1)
            return;

        for (int standingIndex = 0; standingIndex < _standingPlayers.Count; standingIndex++)
        {
            int playerIndex = _standingPlayers[standingIndex];
            PlayerState state = playerStates[playerIndex];

            if (!state.HasFieldedWave || !state.HasSpawnedThisRound)
                continue;

            if (CountLivingUnits(playerIndex) > 0)
                continue;

            EliminatePlayer(playerIndex);
            if (matchComplete)
                return;
        }
    }

    private void EliminatePlayer(int playerIndex)
    {
        PlayerState state = playerStates[playerIndex];
        if (state.Eliminated)
            return;

        CollectStandingPlayers();
        int standingCount = _standingPlayers.Count;
        if (standingCount <= 1)
            return;

        state.Eliminated = true;
        state.Placement = standingCount;
        state.IsCountdownVisible = false;
        RefreshTileAbilityVisibility(playerIndex);
        if (state.CountdownTextHandle >= 0)
            _api.SetStaticTextVisible(state.CountdownTextHandle, false);

        PlayerId player = _api.GetPlayer(playerIndex);
        string placementText = FormatPlacement(standingCount);
        player.TriggerDefeat($"Eliminated — {placementText} of {playerStates.Count}.");

        _api.BroadcastMessage(
            $"{player.Name} has been eliminated ({placementText} of {playerStates.Count}). "
            + $"{standingCount - 1} remain.");

        RefreshPlacementDisplay();

        if (standingCount - 1 <= 1)
            EndMatch("last player standing");
    }

    private void EndMatch(string reason)
    {
        if (matchComplete)
            return;

        matchComplete = true;

        foreach (int playerIndex in playerStates.Keys)
            RefreshTileAbilityVisibility(playerIndex);

        CollectStandingPlayers();
        _standingsOrder.Clear();
        _standingsOrder.AddRange(_standingPlayers);
        _standingsOrder.Sort(CompareStandingPlayers);

        int placement = 1;
        foreach (int playerIndex in _standingsOrder)
            playerStates[playerIndex].Placement = placement++;

        int winnerIndex = _standingsOrder.Count > 0 ? _standingsOrder[0] : -1;

        foreach (KeyValuePair<int, PlayerState> entry in playerStates)
        {

            if (entry.Key == winnerIndex || entry.Value.Eliminated || entry.Value.Placement <= 0)
                continue;

            _api.GetPlayer(entry.Key).TriggerDefeat(
                $"Match over — {FormatPlacement(entry.Value.Placement)} of {playerStates.Count}.");
        }

        if (winnerIndex >= 0)
            _api.GetPlayer(winnerIndex).TriggerVictory();

        _api.BroadcastMessage($"Match over: {reason}. Final standings — {BuildStandingsText()}.");

        RefreshPlacementDisplay();
    }

    private int CompareStandingPlayers(int left, int right)
    {
        int livingCompare = CountLivingUnits(right).CompareTo(CountLivingUnits(left));
        if (livingCompare != 0)
            return livingCompare;

        int killCompare = _api.GetPlayer(right).GetKills().CompareTo(_api.GetPlayer(left).GetKills());
        return killCompare != 0 ? killCompare : left.CompareTo(right);
    }

    private int CountLivingUnits(int playerIndex)
    {
        int livingCount = 0;
        foreach (UnitId unit in _api.GetUnitsOwnedByPlayer(playerIndex + ProxyPlayerOffset))
        {
            if (unit.IsValid && !unit.IsDead)
                livingCount++;
        }

        return livingCount;
    }

    private void CollectStandingPlayers()
    {
        _standingPlayers.Clear();
        for (int playerIndex = 1; playerIndex <= RealPlayerSlotCount; playerIndex++)
        {
            if (!playerStates.TryGetValue(playerIndex, out PlayerState? state) || state.Eliminated)
                continue;

            _standingPlayers.Add(playerIndex);
        }
    }

    private void ResolveRoundWaveTargets()
    {
        _waveTargetRoster.Clear();
        for (int playerIndex = 1; playerIndex <= RealPlayerSlotCount; playerIndex++)
        {
            if (!playerStates.TryGetValue(playerIndex, out PlayerState? state) || state.Eliminated)
                continue;

            _waveTargetRoster.Add(playerIndex);
        }

        _api.Shuffle(_waveTargetRoster);

        for (int rosterIndex = 0; rosterIndex < _waveTargetRoster.Count; rosterIndex++)
        {
            int playerIndex = _waveTargetRoster[rosterIndex];
            playerStates[playerIndex].WaveTargetPlayerIndex = _waveTargetRoster.Count > 1
                ? _waveTargetRoster[(rosterIndex + 1) % _waveTargetRoster.Count]
                : playerIndex;
        }
    }

    private void RefreshPlacementDisplay()
    {
        UpdatePlacementLeaderboard();
        UpdateSummaryTable();
    }

    private void UpdatePlacementLeaderboard()
    {
        _api.SetLeaderboardBoard(PlacementLeaderboardId, "Standings", true);
        _api.ClearLeaderboardBoard(PlacementLeaderboardId);

        _standingsOrder.Clear();
        for (int playerIndex = 1; playerIndex <= RealPlayerSlotCount; playerIndex++)
        {
            if (playerStates.ContainsKey(playerIndex))
                _standingsOrder.Add(playerIndex);
        }

        _standingsOrder.Sort(CompareStandingsForDisplay);

        foreach (int playerIndex in _standingsOrder)
        {
            PlayerState state = playerStates[playerIndex];
            string value = state.Placement > 0
                ? FormatPlacement(state.Placement)
                : $"{CountLivingUnits(playerIndex)} alive";

            _api.AddLeaderboardBoardRow(
                PlacementLeaderboardId,
                -1,
                string.Empty,
                _api.GetPlayer(playerIndex).Name,
                value);
        }
    }

    private int CompareStandingsForDisplay(int left, int right)
    {
        int leftPlacement = playerStates[left].Placement;
        int rightPlacement = playerStates[right].Placement;

        if (leftPlacement != rightPlacement)
        {
            int leftKey = leftPlacement > 0 ? leftPlacement : int.MaxValue;
            int rightKey = rightPlacement > 0 ? rightPlacement : int.MaxValue;
            return leftKey.CompareTo(rightKey);
        }

        int livingCompare = CountLivingUnits(right).CompareTo(CountLivingUnits(left));
        return livingCompare != 0 ? livingCompare : left.CompareTo(right);
    }

    private string BuildStandingsText()
    {
        _standingsOrder.Clear();
        for (int playerIndex = 1; playerIndex <= RealPlayerSlotCount; playerIndex++)
        {
            if (playerStates.ContainsKey(playerIndex) && playerStates[playerIndex].Placement > 0)
                _standingsOrder.Add(playerIndex);
        }

        _standingsOrder.Sort((left, right) => playerStates[left].Placement.CompareTo(playerStates[right].Placement));

        var standings = new StringBuilder();
        foreach (int playerIndex in _standingsOrder)
        {
            if (standings.Length > 0)
                standings.Append(", ");

            standings.Append(FormatPlacement(playerStates[playerIndex].Placement));
            standings.Append(' ');
            standings.Append(_api.GetPlayer(playerIndex).Name);
        }

        return standings.ToString();
    }

    private static string FormatPlacement(int placement)
    {
        int lastTwoDigits = placement % 100;
        int lastDigit = placement % 10;

        string suffix = lastTwoDigits is >= 11 and <= 13
            ? "th"
            : lastDigit switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th"
            };

        return $"{placement}{suffix}";
    }
}
