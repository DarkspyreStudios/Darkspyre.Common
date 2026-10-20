namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>What a <see cref="StatusLight"/> shows. Each value has its own style class (<see cref="StatusLight.ClassOf"/>).</summary>
public enum StatusLightState
{
    /// <summary>Running as chosen. The light glows at its own pace.</summary>
    Active,

    /// <summary>Switched off. The light does not pulse.</summary>
    Off,

    /// <summary>Failed. The light glows at its own pace.</summary>
    Failed,

    /// <summary>Running an earlier choice because the chosen one failed. The light blinks.</summary>
    Fallback,

    /// <summary>A change is in progress. The light blinks fast.</summary>
    Busy,
}
