import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Search, X } from 'lucide-react'

interface NodeLike {
  id: string
  data: { name?: string; description?: string | null }
}

function matchesQuery(text: string, q: string): boolean {
  // Match at word boundaries (word-start) for better precision
  const words = text.toLowerCase().split(/\W+/)
  return words.some((word) => word.startsWith(q))
}

export function filterMatchingNodes(nodes: NodeLike[], query: string): string[] {
  if (!query.trim()) return []
  const q = query.toLowerCase()
  return nodes
    .filter((n) => !n.id.startsWith('msg-'))
    .filter((n) => {
      const name = n.data.name ?? ''
      const desc = n.data.description ?? ''
      return matchesQuery(name, q) || matchesQuery(desc, q)
    })
    .map((n) => n.id)
}

interface SearchOverlayProps {
  nodes: NodeLike[]
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
    <div className="absolute left-1/2 top-3 z-50 -translate-x-1/2">
      <div className="flex items-center gap-2 rounded-lg border border-border bg-card px-3 py-1.5 shadow-lg">
        <Search className="h-4 w-4 text-muted-foreground" />
        <input
          ref={inputRef}
          className="w-64 bg-transparent text-sm text-foreground outline-none placeholder:text-muted-foreground"
          placeholder="Search nodes..."
          value={query}
          onChange={(e) => { setQuery(e.target.value); setCurrentIndex(0) }}
          onKeyDown={handleKeyDown}
        />
        {matchIds.length > 0 && (
          <span className="text-xs text-muted-foreground whitespace-nowrap">
            {currentIndex + 1}/{matchIds.length}
          </span>
        )}
        {query && matchIds.length === 0 && (
          <span className="text-xs text-muted-foreground">No matches</span>
        )}
        <button onClick={onClose} className="text-muted-foreground hover:text-foreground">
          <X className="h-3.5 w-3.5" />
        </button>
      </div>
    </div>
  )
}
