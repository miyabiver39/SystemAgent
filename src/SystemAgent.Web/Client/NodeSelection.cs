using Microsoft.AspNetCore.Components;
using SystemAgent.Client;

namespace SystemAgent.Web.Client;

/// <summary>WebUI（回線）ごとの操作対象ノード。nullはログイン中のこのノード。</summary>
public sealed class NodeSelection
{
    public Guid? NodeId { get; private set; }

    public string? NodeName { get; private set; }

    public event Func<Task>? Changed;

    public async Task SelectAsync(Guid? nodeId, string? nodeName)
    {
        if (NodeId == nodeId) return;
        NodeId = nodeId;
        NodeName = nodeName;
        if (Changed is { } changed) await changed.Invoke();
    }
}

/// <summary>
/// 操作対象ノードを切り替えられる画面の基底クラス。Api は選択中のノードへ（必要なら他ノード経由で）要求する。
/// ノードが切り替わると LoadAsync が呼び直される。
/// </summary>
public abstract class NodeScopedPage : ComponentBase, IDisposable
{
    [Inject] protected ApiClient RootApi { get; set; } = default!;

    [Inject] protected NodeSelection Selection { get; set; } = default!;

    protected ApiClient Api => RootApi.ForNode(Selection.NodeId);

    protected override async Task OnInitializedAsync()
    {
        Selection.Changed += OnNodeChangedAsync;
        await LoadAsync();
    }

    protected abstract Task LoadAsync();

    private Task OnNodeChangedAsync() => InvokeAsync(async () =>
    {
        await LoadAsync();
        StateHasChanged();
    });

    public void Dispose() => Selection.Changed -= OnNodeChangedAsync;
}
