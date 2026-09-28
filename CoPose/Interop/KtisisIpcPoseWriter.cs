using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using CoPose.Core.Sync;

namespace CoPose.Interop;

/// <summary>
/// Writes poses through Ktisis IPC. Framework thread only.
/// <para>
/// <c>Ktisis.ApplyAbsolutePoses</c> writes the given bones into <c>LocalPose</c> and then rebuilds model space
/// for <em>every</em> bone from <c>LocalPose</c>. Because Ktisis gizmos only edit model space, <c>LocalPose</c> is
/// stale for any bone posed locally, so sending just the changed bones would undo local posing. Each apply
/// therefore sends the actor's full current pose (as read by <see cref="HavokPoseReader"/>) with the received
/// bones overriding it.
/// </para>
/// <para>
/// Two things <c>ApplyAbsolutePoses</c> does not cover are written directly: the actor's world transform
/// (<see cref="PoseBuffer.WorldBone"/>) and the body's root bone, whose model space Ktisis never rebuilds.
/// </para>
/// </summary>
public sealed class KtisisIpcPoseWriter(KtisisIpc ktisis, HavokPoseReader reader) : IPoseWriter
{
    private readonly PoseBuffer current = new();

    public Task<bool> ApplyBonesAsync(ActorHandle actor, IReadOnlyList<BoneValue> bones)
    {
        if (!ktisis.Available || !reader.TryRead(actor, current))
            return Task.FromResult(false);

        var samples = new Dictionary<string, BoneSample>(current.Count, StringComparer.Ordinal);
        for (var i = 0; i < current.Count; i++)
            samples[current.Names[i]] = current.Samples[i];

        var posesBones = false;
        foreach (var bone in bones)
        {
            if (bone.Name == PoseBuffer.WorldBone)
            {
                if (!reader.WriteWorld(actor, bone.Value))
                    return Task.FromResult(false);
                continue;
            }
            samples[bone.Name] = bone.Value;
            posesBones = true;
        }

        if (!posesBones)
            return Task.FromResult(true);

        // Before the IPC call: it rebuilds the rest of the body from the root's model transform.
        reader.WriteRoot(actor, samples);

        samples.Remove(PoseBuffer.WorldBone);
        var matrices = new Dictionary<string, Matrix4x4>(samples.Count, StringComparer.Ordinal);
        foreach (var (name, sample) in samples)
            matrices[name] = Compose(sample);
        return ktisis.ApplyAbsolutePoses(actor.ObjectIndex, matrices);
    }

    /// <summary>Scale, then rotation, then translation, as Ktisis decomposes it.</summary>
    public static Matrix4x4 Compose(in BoneSample sample) =>
        Matrix4x4.CreateScale(ClampScale(sample.Scale))
        * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(sample.Rotation))
        * Matrix4x4.CreateTranslation(sample.Position);

    /// <summary>Zero scale (used by Customize+ to hide bones) cannot be decomposed; Ktisis clamps the same way.</summary>
    private static Vector3 ClampScale(Vector3 v) => new(Clamp(v.X), Clamp(v.Y), Clamp(v.Z));

    private static float Clamp(float f) => MathF.Abs(f) < 0.001f ? 0.001f : f;
}
