namespace Realm.Maps;

public readonly struct Coordinate
{
    public readonly System.Numerics.Vector3 Min;
    public readonly System.Numerics.Vector3 Max;
    public readonly System.Numerics.Vector3 Center;

    public Coordinate(System.Numerics.Vector3 min, System.Numerics.Vector3 max)
    {
        Min = min;
        Max = max;
        Center = (min + max) / 2f;
    }
}

public static class Coordinates
{
}
