namespace Realm.Maps;

using System;
using System.Collections.Generic;
using System.Text;
using Realm.MapAPI;

/// <summary>
/// Match length and placement. Waves stop being dealt at 25:00; a player who loses their last
/// unit is out, and placement is reverse elimination order — the first player out takes last
/// place, the last player with units on the field takes first.
/// </summary>
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

    /// <summary>
    /// Runs once a second, not from the death callback: a unit that just died may still be
    /// listed as alive while the death pipeline is inside the callback, and settling on a tick
    /// keeps the survivor count honest without re-entering combat.
    /// </summary>
    private void EvaluateEliminations()
    {
        if (matchComplete)
            return;

        CollectStandingPlayers();
        if (_standingPlayers.Count <= 1)
            return;

        // Only players who have actually fielded a wave can be knocked out; someone who has not
        // drafted yet simply has nothing on the field.
        for (int standingIndex = 0; standingIndex < _standingPlayers.Count; standingIndex++)
        {
            int playerIndex = _standingPlayers[standingIndex];
            PlayerState state = playerStates[playerIndex];

            if (!state.HasFieldedWave || CountLivingUnits(playerIndex) > 0)
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

    /// <summary>
    /// Ranks everyone still standing once the match is over: the player with the most units
    /// left wins, kills break a tie, and slot order breaks that.
    /// </summary>
    private void EndMatch(string reason)
    {
        if (matchComplete)
            return;

        matchComplete = true;

        CollectStandingPlayers();
        _standingsOrder.Clear();
        _standingsOrder.AddRange(_standingPlayers);
        _standingsOrder.Sort(CompareStandingPlayers);

        int placement = _standingsOrder.Count;
        foreach (int playerIndex in _standingsOrder)
            playerStates[playerIndex].Placement = placement--;

        int winnerIndex = _standingsOrder.Count > 0 ? _standingsOrder[0] : -1;

        foreach (KeyValuePair<int, PlayerState> entry in playerStates)
        {
            // The winner gets the victory screen; everyone eliminated already saw their placement
            // the moment they went out.
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

    /// <summary>
    /// Living wave clones only. Board heroes sit on the real player and are never reachable
    /// from the arena, so counting them would keep a wiped player in the match forever.
    /// </summary>
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

    /// <summary>
    /// Deals each standing player a wave opponent for the round. Rebuilt every round so an
    /// eliminated player stops being a target and the remaining players keep pairing off
    /// instead of half of the arena walking to an empty spawn.
    /// </summary>
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
