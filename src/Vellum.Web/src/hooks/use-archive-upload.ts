import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { shownInline } from '@/lib/mutation-errors'
import { sourcesApi } from '@/lib/import-api'
import { sourcesKey } from '@/hooks/use-sources'

/** Uploads one archive to an archive Source, with the bytes sent so far. Each upload is a new scan. */
export function useArchiveUpload(projectId: string) {
  const queryClient = useQueryClient()
  const [sent, setSent] = useState<{ loaded: number; total: number } | null>(null)
  const upload = useMutation({
    mutationFn: ({ sourceId, file }: { sourceId: string; file: File }) => {
      setSent({ loaded: 0, total: file.size })
      return sourcesApi.uploadArchive(projectId, sourceId, file, (loaded, total) => setSent({ loaded, total }))
    },
    onSettled: () => Promise.all([
      queryClient.invalidateQueries({ queryKey: sourcesKey(projectId) }),
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'imports'] }),
    ]),
    onError: shownInline,
  })
  return { upload, sent }
}
