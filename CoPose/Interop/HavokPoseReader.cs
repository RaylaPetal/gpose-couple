using System;
using System.Collections.Generic;
using System.Numerics;
using CoPose.Core.Sync;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace CoPose.Interop;

/// <summary>
/// Reads bone transforms straight from Havok. Framework thread only.
/// <para>
/// Ktisis gizmos edit <c>ModelPose</c> only, leaving <c>LocalPose</c> stale, so local transforms are derived
/// from model space with the same (scale-free) relation Ktisis's <c>HavokPosing.SyncModelSpace</c> uses to
/// rebuild model space after <c>ApplyAbsolutePoses</c>: <c>model = parent ∘ local</c>. Scale is model-space,
/// matching what <c>ApplyAbsolutePoses</c> writes. Only the first occurrence of each bone name (in partial order)
/// is returned, which is exactly what Ktisis can address by name.
/// </para>
/// </summary>
public sealed unsafe class HavokPoseReader(IObjectTable objects) : IPoseReader
{
    private readonly Dictionary<(uint Actor, int Partial, uint Resource), string[]> nameCache = [];
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);

    public bool TryRead(ActorHandle actor, PoseBuffer into)
    {
        var skeleton = GetSkeleton(actor);
        if (skeleton == null)
            return false;

        seen.Clear();
        var count = 0;
        var layout = new HashCode();

        for (var p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var resource = partial->SkeletonResourceHandle != null ? partial->SkeletonResourceHandle->Id : 0u;
            layout.Add(p);
            layout.Add(resource);

            var pose = partial->GetHavokPose(0);
            if (pose == null || pose->Skeleton == null || pose->ModelPose.Data == null)
                continue;

            var names = GetNames(actor.ObjectIndex, p, resource, pose);
            var parents = pose->Skeleton->ParentIndices;
            var model = pose->ModelPose.Data;
            var length = Math.Min(names.Length, pose->ModelPose.Length);

            into.EnsureCapacity(count + length);
            for (var i = 0; i < length; i++)
            {
                var name = names[i];
                if (!seen.Add(name))
                    continue;

                ref var qs = ref model[i];
                var position = new Vector3(qs.Translation.X, qs.Translation.Y, qs.Translation.Z);
                var rotation = new Quaternion(qs.Rotation.X, qs.Rotation.Y, qs.Rotation.Z, qs.Rotation.W);

                var parent = i < parents.Length ? parents[i] : (short)-1;
                if (parent >= 0 && parent < length)
                {
                    ref var pqs = ref model[parent];
                    var parentPosition = new Vector3(pqs.Translation.X, pqs.Translation.Y, pqs.Translation.Z);
                    var inverseParent = Quaternion.Inverse(new Quaternion(pqs.Rotation.X, pqs.Rotation.Y, pqs.Rotation.Z, pqs.Rotation.W));
                    position = Vector3.Transform(position - parentPosition, inverseParent);
                    rotation = Quaternion.Normalize(inverseParent * rotation);
                }

                into.Names[count] = name;
                into.Samples[count] = new BoneSample(position, rotation, new Vector3(qs.Scale.X, qs.Scale.Y, qs.Scale.Z));
                count++;
            }
        }

        into.Count = count;
        into.LayoutId = layout.ToHashCode();
        return count > 0;
    }

    private string[] GetNames(uint actor, int partial, uint resource, FFXIVClientStructs.Havok.Animation.Rig.hkaPose* pose)
    {
        var key = (actor, partial, resource);
        if (nameCache.TryGetValue(key, out var names))
            return names;

        var bones = pose->Skeleton->Bones;
        names = new string[bones.Length];
        for (var i = 0; i < bones.Length; i++)
            names[i] = bones[i].Name.String ?? string.Empty;
        nameCache[key] = names;
        return names;
    }

    private FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton* GetSkeleton(ActorHandle actor)
    {
        if (actor.ObjectIndex >= objects.Length)
            return null;
        var address = objects.GetObjectAddress((int)actor.ObjectIndex);
        if (address == nint.Zero)
            return null;

        var character = (Character*)address;
        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return null;

        return ((CharacterBase*)drawObject)->Skeleton;
    }

    /// <summary>Drops cached bone names, e.g. when leaving GPose (object indices get reused).</summary>
    public void ClearCache() => nameCache.Clear();
}
