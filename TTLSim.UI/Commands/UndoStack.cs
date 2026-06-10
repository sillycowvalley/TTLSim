using System;
using System.Collections.Generic;
using TTLSim.UI.Model;

namespace TTLSim.UI.Commands;

/// <summary>
/// Two-stack undo/redo. Push executes a command and clears the redo stack.
/// Undo pops from undo, calls Undo, pushes onto redo; Redo is the mirror.
///
/// Composite recording: call BeginComposite to start grouping subsequent
/// pushes; EndComposite collapses them into a single CompositeCommand. Nested
/// composites are supported via a depth counter -- only the outermost
/// EndComposite actually closes and pushes.
/// </summary>
public sealed class UndoStack
{
    private readonly Schematic schematic;
    private readonly Stack<ICommand> undo = new();
    private readonly Stack<ICommand> redo = new();

    // Composite recording state
    private int compositeDepth;
    private string compositeDescription = "";
    private List<ICommand>? compositeBuffer;

    public UndoStack(Schematic schematic) => this.schematic = schematic;

    public event EventHandler? Changed;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public ICommand? UndoTop => undo.Count > 0 ? undo.Peek() : null;

    public string? UndoDescription => undo.Count > 0 ? undo.Peek().Description : null;
    public string? RedoDescription => redo.Count > 0 ? redo.Peek().Description : null;

    /// <summary>
    /// Execute a command and record it. If a composite recording is in
    /// progress the command is buffered rather than pushed directly.
    /// </summary>
    public void Do(ICommand command)
    {
        command.Execute(schematic);

        if (compositeBuffer != null)
        {
            compositeBuffer.Add(command);
            return;
        }

        undo.Push(command);
        redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Record an already-executed command without running Execute again.
    /// Useful for drag-to-move where the mutation has already happened
    /// interactively and we only want the final settle on the stack.
    /// </summary>
    public void Record(ICommand command)
    {
        if (compositeBuffer != null)
        {
            compositeBuffer.Add(command);
            return;
        }

        undo.Push(command);
        redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (compositeBuffer != null)
            throw new InvalidOperationException("Cannot undo while a composite is open.");
        if (undo.Count == 0) return;
        var c = undo.Pop();
        c.Undo(schematic);
        redo.Push(c);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (compositeBuffer != null)
            throw new InvalidOperationException("Cannot redo while a composite is open.");
        if (redo.Count == 0) return;
        var c = redo.Pop();
        c.Execute(schematic);
        undo.Push(c);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // -------------------------------------------------- composite recording

    public void BeginComposite(string description)
    {
        if (compositeDepth == 0)
        {
            compositeBuffer = new List<ICommand>();
            compositeDescription = description;
        }
        compositeDepth++;
    }

    public void EndComposite()
    {
        if (compositeDepth == 0)
            throw new InvalidOperationException("EndComposite without matching Begin.");
        compositeDepth--;
        if (compositeDepth > 0) return;

        var buffer = compositeBuffer!;
        compositeBuffer = null;

        if (buffer.Count == 0) return;  // nothing happened, don't pollute the stack

        ICommand toPush = buffer.Count == 1
            ? buffer[0]
            : new CompositeCommand(compositeDescription, buffer);

        undo.Push(toPush);
        redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Convenience: run an action with composite recording active. Use this
    /// for multi-step operations like "delete component and its wires".
    /// </summary>
    public void DoComposite(string description, Action body)
    {
        BeginComposite(description);
        try { body(); }
        finally { EndComposite(); }
    }
}