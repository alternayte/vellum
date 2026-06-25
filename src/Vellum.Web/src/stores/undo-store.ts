import { create } from 'zustand'

export interface Command {
  label: string
  execute: () => void
  undo: () => void
}

const MAX_STACK_SIZE = 50

interface UndoState {
  undoStack: Command[]
  redoStack: Command[]
  canUndo: boolean
  canRedo: boolean

  execute: (command: Command) => void
  undo: () => void
  redo: () => void
  reset: () => void
}

export const useUndoStore = create<UndoState>((set, get) => ({
  undoStack: [],
  redoStack: [],
  canUndo: false,
  canRedo: false,

  execute: (command) => {
    command.execute()
    set((state) => {
      const newStack = [...state.undoStack, command]
      if (newStack.length > MAX_STACK_SIZE) newStack.shift()
      return {
        undoStack: newStack,
        redoStack: [],
        canUndo: true,
        canRedo: false,
      }
    })
  },

  undo: () => {
    const { undoStack, redoStack } = get()
    if (undoStack.length === 0) return
    const command = undoStack[undoStack.length - 1]
    command.undo()
    const newUndo = undoStack.slice(0, -1)
    set({
      undoStack: newUndo,
      redoStack: [...redoStack, command],
      canUndo: newUndo.length > 0,
      canRedo: true,
    })
  },

  redo: () => {
    const { undoStack, redoStack } = get()
    if (redoStack.length === 0) return
    const command = redoStack[redoStack.length - 1]
    command.execute()
    const newRedo = redoStack.slice(0, -1)
    const newUndo = [...undoStack, command]
    set({
      undoStack: newUndo,
      redoStack: newRedo,
      canUndo: true,
      canRedo: newRedo.length > 0,
    })
  },

  reset: () =>
    set({
      undoStack: [],
      redoStack: [],
      canUndo: false,
      canRedo: false,
    }),
}))
