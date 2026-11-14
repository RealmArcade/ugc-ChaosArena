namespace Realm.Maps;

using Realm.MapAPI;

public partial class MapScript
{
    private const int ChaosArenaTeamCount = 12;

    private readonly uint[] _syncedProxyColors = new uint[RealPlayerSlotCount + 1];

    public void LobbyInitialize(ILobbyAPI api)
    {
        // A Chaos Arena lobby seats the twelve players the arena is built for, and its board is played with everything in
        // view. This map has no draft and no building deck of its own, so it declares no match setting of its own: its host
        // is offered the three settings every map has and nothing else.
        api.SetOpenSlotCount(RealPlayerSlotCount);

        // Every player slot after the twelfth belongs to the proxy player this map drives for it, so the lobby reserves
        // them: nobody is admitted into one and the host cannot open one, whatever the rows on screen are clicked.
        api.SetPlayerSlotLimit(RealPlayerSlotCount);
        api.SetTeamCount(ChaosArenaTeamCount);
        api.SetShroudModeDefault(MatchShroudMode.None);

        // Every proxy player carries one seat's army, so each screen's allied-colour minimap draws its own army as
        // itself and every other army by that seat's standing on that screen.
        for (int playerIndex = 1; playerIndex <= RealPlayerSlotCount; playerIndex++)
        {
            api.SetPlayerProxyOwner(playerIndex + ProxyPlayerOffset, playerIndex);
        }
    }

    public void LobbyUpdate(ILobbyAPI api, float deltaSeconds)
    {
        SyncProxyPlayerColors(api);
    }

    /// <summary>
    ///     Keeps each proxy player in the colour of the seat whose army it carries, so a player watching their clones fight
    ///     reads them as their own, and the minimap that paints every player in its own colour draws both as one side.
    /// </summary>
    /// <param name="api">The lobby the map is filling.</param>
    private void SyncProxyPlayerColors(ILobbyAPI api)
    {
        for (int playerIndex = 1; playerIndex <= RealPlayerSlotCount; playerIndex++)
        {
            uint seatColor = api.GetPlayerColor(playerIndex);
            int proxyPlayerIndex = playerIndex + ProxyPlayerOffset;

            if (seatColor == 0)
            {
                if (_syncedProxyColors[playerIndex] == 0) continue;

                _syncedProxyColors[playerIndex] = 0;
                api.SetPlayerColor(proxyPlayerIndex, 0);
                continue;
            }

            if (_syncedProxyColors[playerIndex] == seatColor) continue;

            _syncedProxyColors[playerIndex] = seatColor;
            api.SetPlayerColor(proxyPlayerIndex, seatColor);
        }
    }

    public bool TryGetStartWarning(ILobbyAPI api, out string message)
    {
        message = string.Empty;

        int teamCount = api.TeamCount;
        if (teamCount <= 0) return false;

        Span<int> playersPerTeam = stackalloc int[teamCount];

        for (int slot = 1; slot <= api.SlotCount; slot++)
        {
            if (!api.IsOccupied(slot)) continue;

            int team = api.GetPlayerTeam(slot);
            if (team < 1 || team > teamCount) continue;

            playersPerTeam[team - 1]++;
        }

        int occupiedTeamCount = 0;
        int smallestTeamPlayerCount = int.MaxValue;
        int largestTeamPlayerCount = 0;

        foreach (int playersOnTeam in playersPerTeam)
        {
            if (playersOnTeam == 0) continue;

            occupiedTeamCount++;
            if (playersOnTeam < smallestTeamPlayerCount) smallestTeamPlayerCount = playersOnTeam;
            if (playersOnTeam > largestTeamPlayerCount) largestTeamPlayerCount = playersOnTeam;
        }

        if (occupiedTeamCount == 1)
        {
            message = "Without an opponent your drafted heroes will have no enemies to fight";
            return true;
        }

        if (occupiedTeamCount > 1 && smallestTeamPlayerCount != largestTeamPlayerCount)
        {
            message = "Teams have an uneven player count which may be unfair unless the smaller teams are highly skilled";
            return true;
        }

        return false;
    }
}
