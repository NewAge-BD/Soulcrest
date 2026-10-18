namespace Soulcrest.App.Network;

/// <summary>Pause progress application without stopping another consumer's shared capture.</summary>
public interface INetworkProgressControl
{
    void SetProgressProcessingEnabled(bool enabled);
}
