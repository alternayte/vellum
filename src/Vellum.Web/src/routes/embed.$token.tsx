import { createFileRoute } from '@tanstack/react-router'
import { ShareViewer } from '@/components/share/share-viewer'

/**
 * A view link in the frame of another site: the diagram and its details, no app shell. Only
 * this page may be framed, and only by the origins the workspace allows.
 */
export const Route = createFileRoute('/embed/$token')({
  component: EmbedPage,
})

function EmbedPage() {
  const { token } = Route.useParams()
  return <ShareViewer token={token} embed />
}
