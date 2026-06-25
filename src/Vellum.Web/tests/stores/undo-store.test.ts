import { describe, it, expect, beforeEach, vi } from 'vitest'
import { useUndoStore } from '../../src/stores/undo-store'

beforeEach(() => {
  useUndoStore.getState().reset()
})

describe('undo store', () => {
  it('starts with empty stacks', () => {
    const state = useUndoStore.getState()
    expect(state.undoStack).toEqual([])
    expect(state.redoStack).toEqual([])
    expect(state.canUndo).toBe(false)
    expect(state.canRedo).toBe(false)
  })

  it('execute runs the command and pushes to undo stack', () => {
    const executeFn = vi.fn()
    const undoFn = vi.fn()
    useUndoStore.getState().execute({ label: 'test', execute: executeFn, undo: undoFn })

    expect(executeFn).toHaveBeenCalledOnce()
    expect(undoFn).not.toHaveBeenCalled()
    const state = useUndoStore.getState()
    expect(state.undoStack).toHaveLength(1)
    expect(state.canUndo).toBe(true)
    expect(state.canRedo).toBe(false)
  })

  it('execute clears the redo stack', () => {
    const cmd1 = { label: 'a', execute: vi.fn(), undo: vi.fn() }
    const cmd2 = { label: 'b', execute: vi.fn(), undo: vi.fn() }
    useUndoStore.getState().execute(cmd1)
    useUndoStore.getState().undo()
    expect(useUndoStore.getState().canRedo).toBe(true)

    useUndoStore.getState().execute(cmd2)
    expect(useUndoStore.getState().redoStack).toEqual([])
    expect(useUndoStore.getState().canRedo).toBe(false)
  })

  it('undo calls command.undo and moves to redo stack', () => {
    const cmd = { label: 'test', execute: vi.fn(), undo: vi.fn() }
    useUndoStore.getState().execute(cmd)
    useUndoStore.getState().undo()

    expect(cmd.undo).toHaveBeenCalledOnce()
    const state = useUndoStore.getState()
    expect(state.undoStack).toHaveLength(0)
    expect(state.redoStack).toHaveLength(1)
    expect(state.canUndo).toBe(false)
    expect(state.canRedo).toBe(true)
  })

  it('redo calls command.execute and moves back to undo stack', () => {
    const cmd = { label: 'test', execute: vi.fn(), undo: vi.fn() }
    useUndoStore.getState().execute(cmd)
    useUndoStore.getState().undo()
    cmd.execute.mockClear()

    useUndoStore.getState().redo()
    expect(cmd.execute).toHaveBeenCalledOnce()
    const state = useUndoStore.getState()
    expect(state.undoStack).toHaveLength(1)
    expect(state.redoStack).toHaveLength(0)
    expect(state.canUndo).toBe(true)
    expect(state.canRedo).toBe(false)
  })

  it('undo on empty stack does nothing', () => {
    useUndoStore.getState().undo()
    expect(useUndoStore.getState().undoStack).toEqual([])
  })

  it('redo on empty stack does nothing', () => {
    useUndoStore.getState().redo()
    expect(useUndoStore.getState().redoStack).toEqual([])
  })

  it('caps undo stack at 50 commands', () => {
    for (let i = 0; i < 55; i++) {
      useUndoStore.getState().execute({
        label: `cmd-${i}`,
        execute: vi.fn(),
        undo: vi.fn(),
      })
    }
    expect(useUndoStore.getState().undoStack).toHaveLength(50)
    expect(useUndoStore.getState().undoStack[0].label).toBe('cmd-5')
  })

  it('supports multiple undo/redo in sequence', () => {
    const cmds = Array.from({ length: 3 }, (_, i) => ({
      label: `cmd-${i}`,
      execute: vi.fn(),
      undo: vi.fn(),
    }))
    cmds.forEach((c) => useUndoStore.getState().execute(c))

    useUndoStore.getState().undo()
    useUndoStore.getState().undo()
    expect(useUndoStore.getState().undoStack).toHaveLength(1)
    expect(useUndoStore.getState().redoStack).toHaveLength(2)

    useUndoStore.getState().redo()
    expect(useUndoStore.getState().undoStack).toHaveLength(2)
    expect(useUndoStore.getState().redoStack).toHaveLength(1)
  })

  it('reset clears all state', () => {
    useUndoStore.getState().execute({ label: 'a', execute: vi.fn(), undo: vi.fn() })
    useUndoStore.getState().reset()
    const state = useUndoStore.getState()
    expect(state.undoStack).toEqual([])
    expect(state.redoStack).toEqual([])
    expect(state.canUndo).toBe(false)
    expect(state.canRedo).toBe(false)
  })
})
