// src/Vellum.Web/src/components/docs/page-tree.tsx
import { createElement, type ReactNode } from 'react'
import { createProcessor } from '@mdx-js/mdx'
import remarkGfm from 'remark-gfm'
import rehypePrettyCode from 'rehype-pretty-code'

/**
 * A page of a project is text that any editor of the workspace wrote, or an import, or an
 * AI. It is rendered as a document and never run as a program: the MDX is parsed to a tree,
 * and the tree is turned into React elements here. No code is generated and none is
 * evaluated, so the page needs no `unsafe-eval` in the Content-Security-Policy, and an
 * expression in a page (`{fetch(...)}`, `onClick={...}`, `import`) has no effect.
 * The two components a page can use are <ElementLink id="..." /> and <LiveView id="..." />.
 */

interface TreeNode {
  type: string
  tagName?: string
  name?: string | null
  value?: unknown
  properties?: Record<string, unknown>
  attributes?: { type: string; name?: string; value?: unknown }[]
  children?: TreeNode[]
}

/** Elements that load or run something, or that change the page outside the document. */
const REFUSED_TAGS = new Set(['script', 'style', 'iframe', 'object', 'embed', 'link', 'meta', 'base', 'form', 'frame', 'frameset', 'template', 'svg', 'math'])
const URL_PROPERTIES = new Set(['href', 'src', 'cite', 'poster', 'action', 'formAction', 'xlinkHref', 'srcSet'])
const SAFE_URL = /^(?:https?:|mailto:|#|\/(?!\/)|\.{0,2}\/|[^:/?#]*(?:[/?#]|$))/i
const HTML_TAG = /^[a-z][a-z0-9]*$/

export interface PageComponents {
  ElementLink: (props: { id: string }) => ReactNode
  LiveView: (props: { id: string }) => ReactNode
}

function styleObject(css: string): Record<string, string> {
  const style: Record<string, string> = {}
  for (const declaration of css.split(';')) {
    const at = declaration.indexOf(':')
    if (at < 0) continue
    const name = declaration.slice(0, at).trim()
    const value = declaration.slice(at + 1).trim()
    // A style from the highlighter is a colour. A value that loads something is left out.
    if (!name || !value || /url\(|expression\(|@import/i.test(value)) continue
    style[name.startsWith('--') ? name : name.replace(/-([a-z])/g, (_, c: string) => c.toUpperCase())] = value
  }
  return style
}

function reactProps(properties: Record<string, unknown>): Record<string, unknown> {
  const props: Record<string, unknown> = {}
  for (const [name, raw] of Object.entries(properties)) {
    // No event handler comes from a page, whatever its value is.
    if (raw === null || raw === undefined || raw === false || /^on/i.test(name)) continue
    const value = Array.isArray(raw) ? raw.join(' ') : raw
    if (typeof value !== 'string' && typeof value !== 'number' && value !== true) continue
    if (URL_PROPERTIES.has(name) && (typeof value !== 'string' || !SAFE_URL.test(value.trim()))) continue
    if (name === 'style') {
      if (typeof value === 'string') props.style = styleObject(value)
    } else if (/^(data|aria)[A-Z]/.test(name)) {
      props[name.replace(/[A-Z]/g, (c) => `-${c.toLowerCase()}`)] = value
    } else if (name === 'class') {
      props.className = value
    } else {
      props[name] = value
    }
  }
  return props
}

/** The attributes of a JSX tag that are plain text. An attribute with an expression is left out. */
function literalAttributes(node: TreeNode): Record<string, unknown> {
  const properties: Record<string, unknown> = {}
  for (const attribute of node.attributes ?? []) {
    if (attribute.type !== 'mdxJsxAttribute' || !attribute.name) continue
    if (attribute.value === null || attribute.value === undefined) properties[attribute.name] = true
    else if (typeof attribute.value === 'string') properties[attribute.name] = attribute.value
  }
  return properties
}

/** Turns the parsed page into React elements. */
export function renderTree(node: TreeNode, components: PageComponents, key: number | string = 'root'): ReactNode {
  const children = () => (node.children ?? []).map((child, index) => renderTree(child, components, index))

  switch (node.type) {
    case 'root':
      return createElement('div', { key, className: 'prose prose-sm prose-invert max-w-none' }, ...children())
    case 'text':
      return typeof node.value === 'string' ? node.value : null
    case 'element': {
      if (!node.tagName || !HTML_TAG.test(node.tagName) || REFUSED_TAGS.has(node.tagName)) return null
      return createElement(node.tagName, { key, ...reactProps(node.properties ?? {}) }, ...children())
    }
    case 'mdxJsxFlowElement':
    case 'mdxJsxTextElement': {
      const attributes = literalAttributes(node)
      if (node.name === 'ElementLink' || node.name === 'LiveView') {
        const id = typeof attributes.id === 'string' ? attributes.id : ''
        const Component = components[node.name]
        return <Component key={key} id={id} />
      }
      // A fragment, or a plain HTML tag written as JSX. Any other component name is not known here.
      if (!node.name) return createElement('span', { key, style: { display: 'contents' } }, ...children())
      if (!HTML_TAG.test(node.name) || REFUSED_TAGS.has(node.name)) return null
      return createElement(node.name, { key, ...reactProps(attributes) }, ...children())
    }
    // Expressions, imports and exports, comments, doctypes, raw text of another kind: not part of a page.
    default:
      return null
  }
}

/** Parses a page and renders it with no evaluation. */
export async function renderPage(content: string, components: PageComponents): Promise<ReactNode> {
  let page: ReactNode = null
  const processor = createProcessor({
    remarkPlugins: [remarkGfm],
    rehypePlugins: [
      [rehypePrettyCode, { theme: 'one-dark-pro' }],
      // Last, so it sees the finished document tree. What the processor does after this
      // (it builds a program from the tree) is not used.
      () => (tree: TreeNode) => { page = renderTree(tree, components) },
    ],
  })
  // The types of the processor describe its last stage; run takes the parsed page.
  await processor.run(processor.parse(content) as unknown as Parameters<typeof processor.run>[0])
  return page
}
