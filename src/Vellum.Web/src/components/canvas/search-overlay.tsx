import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Search, X } from 'lucide-react'
import { filterMatchingNodes, type SearchableNode } from '@/lib/canvas-search'

interface SearchOverlayProps {
  nodes: SearchableNode[]
  onMatchChange: (matchIds: string[], currentIndex: number) => void
  onClose: () => void
  onFocusNode: (nodeId: string) => void
}

export function SearchOverlay({ nodes, onMatchChange, onClose, onFocusNode }: SearchOverlayProps) {
  const [query, setQuery] = useState('')
  const [currentIndex, setCurrentIndex] = useState(0)
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    inputRef.current?.focus()
  }, [])

  const matchIds = useMemo(() => filterMatchingNodes(nodes, query), [nodes, query])

  useEffect(() => {
    onMatchChange(matchIds, currentIndex)
  }, [query, currentIndex, matchIds, onMatchChange])

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault()
        onClose()
      } else if (e.key === 'Enter') {
        e.preventDefault()
        if (matchIds.length === 0) return
        const nextIndex = (currentIndex + 1) % matchIds.length
        setCurrentIndex(nextIndex)
        onFocusNode(matchIds[nextIndex])
      }
    },
    [onClose, matchIds, currentIndex, onFocusNode],
  )

  return (
    <div role="search" className="absolute left-1/2 top-3 z-50 -translate-x-1/2">
      <div className="flex items-center gap-2 rounded-lg border border-border bg-card px-3 py-1.5 shadow-lg">
        <Search className="h-4 w-4 text-muted-foreground" />
        <input
          ref={inputRef}
          className="w-72 bg-transparent text-sm text-foreground outline-none placeholder:text-muted-foreground"
          placeholder="Search name, kind, technology, truth label..."
          aria-label="Search the canvas"
          value={query}
          onChange={(e) => { setQuery(e.target.value); setCurrentIndex(0) }}
          onKeyDown={handleKeyDown}
        />
        {matchIds.length > 0 && (
          <span aria-live="polite" className="text-xs text-muted-foreground whitespace-nowrap">
            {currentIndex + 1}/{matchIds.length}
          </span>
        )}
        {query && matchIds.length === 0 && (
          <span aria-live="polite" className="text-xs text-muted-foreground">No matches</span>
        )}
        <button
          type="button"
          onClick={onClose}
          aria-label="Close search"
          className="rounded text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary"
        >
          <X className="h-3.5 w-3.5" />
        </button>
      </div>
    </div>
  )
}
