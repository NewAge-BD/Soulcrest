namespace Soulcrest.App.Network;

/// <summary>A merchant envelope from the existing exact native-owned capture path.</summary>
public sealed record MerchantNetworkMessage(
    string Stream, long Generation, int? ServerId, DateTimeOffset At, byte[] Message);

public interface INetworkMerchantSource
{
    event Action<MerchantNetworkMessage>? MerchantMessageReceived;
    event Action? MerchantSessionReset;
}
