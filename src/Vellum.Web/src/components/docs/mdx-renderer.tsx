// src/Vellum.Web/src/components/docs/mdx-renderer.tsx
import { useMemo, useState, useEffect, Fragment } from 'react'
import { evaluate } from '@mdx-js/mdx'
import { jsx, jsxs } from 'react/jsx-runtime'
import { ElementLink } from './element-link'
import { LiveViewCard } from './live-view-card'

interface MdxRendererProps {
  content: string
  elements: { id: string; kind: string; name: string }[]
  views: { id: string; name: string }[]
}

export function MdxRenderer({ content, elements, views }: MdxRendererProps) {
  const [MdxContent, setMdxContent] = useState<React.ComponentType | null>(null)
  const [error, setError] = useState<string | null>(null)

  const components = useMemo(
    () => ({
      ElementLink: (props: { id: string }) => <ElementLink id={props.id} elements={elements} />,
      LiveView: (props: { id: string }) => <LiveViewCard id={props.id} views={views} />,
    }),
    [elements, views],
  )

  useEffect(() => {
    let cancelled = false

    async function compile() {
      try {
        const { default: Content } = await evaluate(content, {
          jsx,
          jsxs,
          Fragment,
          useMDXComponents: () => components,
        })
        if (!cancelled) {
          setMdxContent(() => Content as React.ComponentType)
          setError(null)
        }
      } catch (e) {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : 'Failed to render MDX')
          setMdxContent(null)
        }
      }
    }

    compile()
    return () => { cancelled = true }
  }, [content, components])

  if (error) {
    return (
      <div className="rounded border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive">
        <p className="font-medium">MDX render error</p>
        <pre className="mt-1 text-xs">{error}</pre>
      </div>
    )
  }

  if (!MdxContent) {
    return <div className="text-sm text-muted-foreground">Rendering...</div>
  }

  return (
    <div className="prose prose-sm prose-invert max-w-none">
      <MdxContent />
    </div>
  )
}
