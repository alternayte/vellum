import { describe, it, expect, beforeEach } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { StrataBreadcrumb } from '../../../src/components/shell/strata-breadcrumb'
import { useCanvasStore } from '../../../src/stores/canvas-store'

beforeEach(() => {
  useCanvasStore.getState().reset()
})

describe('StrataBreadcrumb', () => {
  it('renders Context root link when drill path is empty', () => {
    render(<StrataBreadcrumb />)
    expect(screen.getByText('Context')).toBeTruthy()
  })

  it('applies active style to Context when at root', () => {
    render(<StrataBreadcrumb />)
    const btn = screen.getByText('Context')
    expect(btn.className).toContain('text-foreground')
    expect(btn.className).not.toContain('text-muted-foreground')
  })

  it('renders a segment for each entry in drillPath', () => {
    useCanvasStore.getState().drillInto({ elementId: 's1', name: 'Orders', kind: 'system' })
    render(<StrataBreadcrumb />)
    expect(screen.getByText('Orders')).toBeTruthy()
    expect(screen.getByText('›')).toBeTruthy()
  })

  it('renders container label for app kind segments', () => {
    useCanvasStore.getState().drillInto({ elementId: 'a1', name: 'API', kind: 'app' })
    render(<StrataBreadcrumb />)
    expect(screen.getByText('Container: API')).toBeTruthy()
  })

  it('Context becomes muted when drill path is non-empty', () => {
    useCanvasStore.getState().drillInto({ elementId: 's1', name: 'Orders', kind: 'system' })
    render(<StrataBreadcrumb />)
    const ctxBtn = screen.getByText('Context')
    expect(ctxBtn.className).toContain('text-muted-foreground')
  })

  it('clicking Context calls drillTo(-1)', () => {
    useCanvasStore.getState().drillInto({ elementId: 's1', name: 'Orders', kind: 'system' })
    render(<StrataBreadcrumb />)
    fireEvent.click(screen.getByText('Context'))
    expect(useCanvasStore.getState().drillPath).toHaveLength(0)
  })

  it('clicking a segment drills to that index', () => {
    useCanvasStore.getState().drillInto({ elementId: 's1', name: 'Orders', kind: 'system' })
    useCanvasStore.getState().drillInto({ elementId: 'a1', name: 'API', kind: 'app' })
    render(<StrataBreadcrumb />)
    fireEvent.click(screen.getByText('Orders'))
    expect(useCanvasStore.getState().drillPath).toHaveLength(1)
    expect(useCanvasStore.getState().currentRootId).toBe('s1')
  })
})
