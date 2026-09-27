using System.Numerics;

namespace CoPose.Core.Sync;

public static class PoseDiff
{
    public const float PositionEpsilon = 1e-4f;

    /// <summary>Threshold on <c>1 - |dot(a, b)|</c>; q and -q are the same rotation.</summary>
    public const float RotationEpsilon = 1e-5f;

    public const float ScaleEpsilon = 1e-4f;

    public static bool Changed(in BoneSample a, in BoneSample b) =>
        MaxAbs(a.Position - b.Position) > PositionEpsilon
        || 1f - MathF.Abs(Quaternion.Dot(a.Rotation, b.Rotation)) > RotationEpsilon
        || MaxAbs(a.Scale - b.Scale) > ScaleEpsilon;

    /// <summary>
    /// Appends to <paramref name="changed"/> the indices where <paramref name="live"/> differs from
    /// <paramref name="committed"/>, skipping indices with a non-zero <paramref name="skip"/> count.
    /// Does not allocate once <paramref name="changed"/> has grown to size.
    /// </summary>
    public static void FindChanged(ReadOnlySpan<BoneSample> committed, ReadOnlySpan<BoneSample> live, ReadOnlySpan<int> skip, List<int> changed)
    {
        for (var i = 0; i < live.Length; i++)
        {
            if (skip[i] != 0)
                continue;
            if (Changed(committed[i], live[i]))
                changed.Add(i);
        }
    }

    private static float MaxAbs(Vector3 v) => MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z)));
}
