namespace Realm.Maps;

using Realm.MapAPI;

public partial class MapScript
{
    private const int ChaosArenaTeamCount = 12;

    public void LobbyInitialize(ILobbyAPI api)
    {
        // A Chaos Arena lobby seats the twelve players the arena is built for, and its board is played with everything in
        // view. Every other match setting is left to the core game's defaults, so this map states nothing about them.
        api.SetMaximumPlayerCount(RealPlayerSlotCount);
        api.SetTeamCount(ChaosArenaTeamCount);
        api.SetShroudModeDefault(MatchShroudMode.Revealed);
    }

    public void LobbyUpdate(ILobbyAPI api, float deltaSeconds)
    {
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
