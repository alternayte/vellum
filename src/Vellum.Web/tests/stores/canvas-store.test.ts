import { describe, it, expect, beforeEach } from 'vitest'
import { useCanvasStore } from '../../src/stores/canvas-store'

beforeEach(() => {
  useCanvasStore.getState().reset()
})

describe('canvas store', () => {
  it('starts at top level with no drill path', () => {
    const state = useCanvasStore.getState()
    expect(state.drillPath).toEqual([])
    expect(state.currentRootId).toBeNull()
    expect(state.activeLens).toBe('none')
  })

  it('drillInto pushes onto path and updates root', () => {
    useCanvasStore.getState().drillInto({ elementId: 's1', name: 'Orders', kind: 'system' })
    const state = useCanvasStore.getState()
    expect(state.drillPath).toHaveLength(1)
    expect(state.currentRootId).toBe('s1')
  })

  it('drillInto stacks multiple levels', () => {
    useCanvasStore.getState().drillInto({ elementId: 's1', name: 'Orders', kind: 'system' })
    useCanvasStore.getState().drillInto({ elementId: 'a1', name: 'API', kind: 'app' })
    const state = useCanvasStore.getState()
    expect(state.drillPath).toHaveLength(2)
    expect(state.currentRootId).toBe('a1')
  })

  it('drillTo truncates path to given index', () => {
    useCanvasStore.getState().drillInto({ elementId: 's1', name: 'Orders', kind: 'system' })
    useCanvasStore.getState().drillInto({ elementId: 'a1', name: 'API', kind: 'app' })
    useCanvasStore.getState().drillTo(0)
    const state = useCanvasStore.getState()
    expect(state.drillPath).toHaveLength(1)
    expect(state.currentRootId).toBe('s1')
  })

  it('drillTo(-1) returns to top level', () => {
    useCanvasStore.getState().drillInto({ elementId: 's1', name: 'Orders', kind: 'system' })
    useCanvasStore.getState().drillTo(-1)
    const state = useCanvasStore.getState()
    expect(state.drillPath).toEqual([])
    expect(state.currentRootId).toBeNull()
  })

  it('toggleLens toggles between none and the given lens', () => {
    useCanvasStore.getState().toggleLens('status')
    expect(useCanvasStore.getState().activeLens).toBe('status')
    useCanvasStore.getState().toggleLens('status')
    expect(useCanvasStore.getState().activeLens).toBe('none')
  })

  it('toggleLens switches between lenses', () => {
    useCanvasStore.getState().toggleLens('status')
    useCanvasStore.getState().toggleLens('metadata')
    expect(useCanvasStore.getState().activeLens).toBe('metadata')
  })

  it('reset clears all state', () => {
    useCanvasStore.getState().drillInto({ elementId: 's1', name: 'Orders', kind: 'system' })
    useCanvasStore.getState().setLens('status')
    useCanvasStore.getState().setZoom(2)
    useCanvasStore.getState().reset()
    const state = useCanvasStore.getState()
    expect(state.drillPath).toEqual([])
    expect(state.currentRootId).toBeNull()
    expect(state.activeLens).toBe('none')
    expect(state.zoomLevel).toBe(1)
  })

  it('toggleExpand adds and removes node IDs', () => {
    useCanvasStore.getState().toggleExpand('s1')
    expect(useCanvasStore.getState().expandedNodeIds.has('s1')).toBe(true)
    useCanvasStore.getState().toggleExpand('s1')
    expect(useCanvasStore.getState().expandedNodeIds.has('s1')).toBe(false)
  })

  it('reset clears expanded nodes', () => {
    useCanvasStore.getState().toggleExpand('s1')
    useCanvasStore.getState().reset()
    expect(useCanvasStore.getState().expandedNodeIds.size).toBe(0)
  })
})
