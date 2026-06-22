import { describe, it, expect, beforeEach, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { LensBar } from '../../../src/components/shell/lens-bar'
import { useCanvasStore } from '../../../src/stores/canvas-store'

beforeEach(() => {
  useCanvasStore.getState().reset()
})

describe('LensBar', () => {
  it('renders Status and Metadata lens buttons', () => {
    render(<LensBar />)
    expect(screen.getByText('Status')).toBeTruthy()
    expect(screen.getByText('Metadata')).toBeTruthy()
  })

  it('renders Tidy and Fit action buttons', () => {
    render(<LensBar />)
    expect(screen.getByText('Tidy')).toBeTruthy()
    expect(screen.getByText('Fit')).toBeTruthy()
  })

  it('clicking Status toggles status lens on', () => {
    render(<LensBar />)
    fireEvent.click(screen.getByText('Status'))
    expect(useCanvasStore.getState().activeLens).toBe('status')
  })

  it('clicking Status twice toggles status lens off', () => {
    render(<LensBar />)
    fireEvent.click(screen.getByText('Status'))
    fireEvent.click(screen.getByText('Status'))
    expect(useCanvasStore.getState().activeLens).toBe('none')
  })

  it('clicking Metadata toggles metadata lens on', () => {
    render(<LensBar />)
    fireEvent.click(screen.getByText('Metadata'))
    expect(useCanvasStore.getState().activeLens).toBe('metadata')
  })

  it('switching from Status to Metadata updates activeLens', () => {
    render(<LensBar />)
    fireEvent.click(screen.getByText('Status'))
    fireEvent.click(screen.getByText('Metadata'))
    expect(useCanvasStore.getState().activeLens).toBe('metadata')
  })

  it('calls onTidy when Tidy button is clicked', () => {
    const onTidy = vi.fn()
    render(<LensBar onTidy={onTidy} />)
    fireEvent.click(screen.getByText('Tidy'))
    expect(onTidy).toHaveBeenCalledOnce()
  })

  it('calls onZoomToFit when Fit button is clicked', () => {
    const onZoomToFit = vi.fn()
    render(<LensBar onZoomToFit={onZoomToFit} />)
    fireEvent.click(screen.getByText('Fit'))
    expect(onZoomToFit).toHaveBeenCalledOnce()
  })
})
