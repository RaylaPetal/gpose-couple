using CoPose.Protocol;

namespace CoPose.Core.Tags;

/// <summary>A tag value seen on another player's character. <see cref="Value"/> is null when the tag was removed or the player left.</summary>
public readonly record struct RemoteTag(ActorKey Owner, string? Value);

/// <summary>
/// Where CoPose tags come and go: in the plugin, a SimpleHeels tag on the local player's character that the
/// players' sync service carries to paired players.
/// </summary>
public interface ITagChannel
{
    /// <summary>Sets the local player's CoPose tag, or removes it when <paramref name="value"/> is null.</summary>
    void Publish(string? value);

    /// <summary>Returns the next tag change seen on another player, if any. Called from the tick thread.</summary>
    bool TryReceive(out RemoteTag tag);
}
