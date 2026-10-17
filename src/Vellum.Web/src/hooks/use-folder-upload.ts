import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { shownInline } from '@/lib/mutation-errors'
import { formatBytes, sourcesApi } from '@/lib/import-api'
import { selectFolderFiles, zipFolder } from '@/lib/folder-archive'
import { useArchiveUpload } from '@/hooks/use-archive-upload'

export interface FolderUploadResult {
  sourceName: string
  /** The files in the archive. */
  files: number
  /** The files that .gitignore left out. */
  ignored: number
}

/**
 * Reads a folder that the person chose, packs the files a scan reads as a .zip and uploads it
 * to an archive Source. Without a Source ID the Source is the archive Source with the name of
 * the folder, made when the project has none. Each upload is a new scan; the folder is only read.
 */
export function useFolderUpload(projectId: string) {
  const { upload, sent } = useArchiveUpload(projectId)
  const [packing, setPacking] = useState<{ done: number; total: number } | null>(null)

  const run = useMutation({
    mutationFn: async ({ picked, sourceId }: { picked: File[]; sourceId?: string }): Promise<FolderUploadResult> => {
      setPacking({ done: 0, total: picked.length })
      try {
        const selection = await selectFolderFiles(picked, (file) => file.text())
        const name = selection.name || 'folder'
        if (selection.files.length === 0) {
          throw new Error(`${name} has no file that Vellum reads: C# and Go code, project files, JSON and YAML, Dockerfiles, Structurizr DSL and ADRs.`)
        }
        const { maxArchiveBytes } = await sourcesApi.kinds(projectId)
        if (selection.bytes > maxArchiveBytes) {
          throw new Error(`The files to read in ${name} hold ${formatBytes(selection.bytes)}. This workspace takes at most ${formatBytes(maxArchiveBytes)} for one upload. Choose a smaller folder.`)
        }
        const archive = await zipFolder(name, selection.files, (done, total) => setPacking({ done, total }))
        setPacking(null)
        let id = sourceId
        if (!id) {
          const existing = (await sourcesApi.list(projectId)).find((s) => s.kind === 'archive' && s.name === name)
          id = existing?.id ?? (await sourcesApi.create(projectId, { kind: 'archive', name })).id
        }
        await upload.mutateAsync({ sourceId: id, file: archive })
        return { sourceName: name, files: selection.files.length, ignored: selection.ignored }
      } finally {
        setPacking(null)
      }
    },
    onError: shownInline,
  })

  return { run, packing, sent, uploading: upload.isPending }
}
