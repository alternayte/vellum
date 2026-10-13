// src/Vellum.Web/src/components/docs/mdx-renderer.tsx
import { useMemo, useState, useEffect, type ReactNode } from 'react'
import { ElementLink } from './element-link'
import { LiveViewCard } from './live-view-card'
import { renderPage, type PageComponents } from './page-tree'

interface MdxRendererProps {
  content: string
  elements: { id: string; kind: string; name: string }[]
  views: { id: string; name: string }[]
}

export function MdxRenderer({ content, elements, views }: MdxRendererProps) {
  const [page, setPage] = useState<ReactNode>(null)
  const [rendered, setRendered] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const elementsKey = useMemo(() => elements.map(e => e.id).join(','), [elements])
  const viewsKey = useMemo(() => views.map(v => v.id).join(','), [views])

  const components = useMemo<PageComponents>(
    () => ({
      ElementLink: (props: { id: string }) => <ElementLink id={props.id} elements={elements} />,
      LiveView: (props: { id: string }) => <LiveViewCard id={props.id} views={views} />,
    }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [elementsKey, viewsKey],
  )

  useEffect(() => {
    let cancelled = false

    async function compile() {
      try {
        const next = await renderPage(content, components)
        if (!cancelled) {
          setPage(next)
          setRendered(true)
          setError(null)
        }
      } catch (e) {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : 'Failed to render MDX')
          setRendered(false)
        }
      }
    }

    compile()
    return () => { cancelled = true }
  }, [content, components])

  if (error) {
    return (
      <div role="alert" className="rounded border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive">
        <p className="font-medium">MDX render error</p>
        <pre className="mt-1 text-xs">{error}</pre>
      </div>
    )
  }

  if (!rendered) {
    return <div className="text-sm text-muted-foreground">Rendering...</div>
  }

  return page
}
