namespace RoboExp.App.Flow;

/// <summary>
/// Undo/redo for a flow document. The original UndoManager recorded inverse steps per edit group; the port records a
/// serialized snapshot of the whole project (RcuWriter to memory) before each edit, which restores the same state
/// including project data edited through the property/variable panels. Stack depth follows
/// <c>ConfigInfo.FlowViewMaxUndoSteps</c> (default 10, clamped 0..20); the redo stack is cleared by every new edit;
/// edits that leave the serialized project unchanged record nothing (the original only pushed non-empty groups).
/// </summary>
public sealed class FlowUndoManager
{
    private readonly Func<byte[]> _capture;
    private readonly Action<byte[]> _restore;
    private readonly LinkedList<(string Description, byte[] State)> _undo = new();
    private readonly LinkedList<(string Description, byte[] State)> _redo = new();
    private int _maxCount = 10;

    public FlowUndoManager(Func<byte[]> capture, Action<byte[]> restore)
    {
        _capture = capture;
        _restore = restore;
    }

    /// <summary>Maximum stored steps per stack (ConfigInfo.FlowViewMaxUndoSteps: values outside 0..20 are clamped).</summary>
    public int MaxCount
    {
        get => _maxCount;
        set
        {
            _maxCount = Math.Clamp(value, 0, 20);
            Trim(_undo);
            Trim(_redo);
        }
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.Last?.Value.Description;
    public string? RedoDescription => _redo.Last?.Value.Description;

    public event EventHandler? StateChanged;

    /// <summary>Runs <paramref name="mutate"/>, recording the prior state when the project changed. Returns whether it changed.</summary>
    public bool Record(string description, Action mutate)
    {
        var before = _capture();
        mutate();
        var after = _capture();
        if (before.AsSpan().SequenceEqual(after)) return false;
        Push(_undo, (description, before));
        _redo.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Undo() => Step(_undo, _redo);

    public bool Redo() => Step(_redo, _undo);

    /// <summary>Original UndoManager.Reset (after open).</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool Step(LinkedList<(string, byte[])> from, LinkedList<(string, byte[])> to)
    {
        if (from.Last is not { } node) return false;
        from.RemoveLast();
        var current = _capture();
        Push(to, (node.Value.Item1, current));
        _restore(node.Value.Item2);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Push(LinkedList<(string, byte[])> stack, (string, byte[]) item)
    {
        stack.AddLast(item);
        Trim(stack);
    }

    /// <summary>UndoItemStack.MaxCount: the oldest entries fall off.</summary>
    private void Trim(LinkedList<(string, byte[])> stack)
    {
        while (stack.Count > _maxCount) stack.RemoveFirst();
    }
}
