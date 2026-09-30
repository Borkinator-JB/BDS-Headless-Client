using Bds.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Bds.Host.Components;

/// <summary>Re-renders when the bridge reports a change.</summary>
public abstract class LiveComponent : ComponentBase, IDisposable
{
    [Inject] protected BridgeService Bridge { get; set; } = null!;

    protected override void OnInitialized()
    {
        Bridge.Changed += OnBridgeChanged;
        Bridge.Bot.Changed += OnBridgeChanged;
    }

    void OnBridgeChanged() => _ = InvokeAsync(StateHasChanged);

    public virtual void Dispose()
    {
        Bridge.Changed -= OnBridgeChanged;
        Bridge.Bot.Changed -= OnBridgeChanged;
    }
}
