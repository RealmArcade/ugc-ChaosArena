using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Realm.MapAPI;

namespace Realm.Maps;

public class CustomMap : IWasmModule
{
    public void Initialize(IGameAPI api)
    {
    }

    public void Update(IGameAPI api, float delta)
    {
        /*
        var units = api.GetAllUnits().ToArray();
        foreach (var unit in units)
        {
            api.KillUnit(unit);
        }
        */
    }
}
