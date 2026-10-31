namespace Realm.Maps;

using Realm.MapAPI;

public partial class MapScript
{
    private const int ChaosArenaTeamCount = 12;

    public void LobbyInitialize(ILobbyAPI api)
    {
        api.SetTeamCount(ChaosArenaTeamCount);
    }

    public void LobbyUpdate(ILobbyAPI api, float deltaSeconds)
    {
    }
}
