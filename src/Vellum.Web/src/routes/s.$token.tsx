import { createFileRoute } from '@tanstack/react-router'
import { ShareViewer } from '@/components/share/share-viewer'

/** The page of a share link. It is outside the app shell and needs no account. */
export const Route = createFileRoute('/s/$token')({
  component: SharePage,
})

function SharePage() {
  const { token } = Route.useParams()
  return <ShareViewer token={token} />
}
