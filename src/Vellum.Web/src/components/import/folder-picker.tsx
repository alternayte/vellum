import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'
import { UploadProgress } from './archive-upload'

/**
 * A button that opens the folder picker of the browser (`webkitdirectory`, in every current
 * browser). The browser gives a read-only copy of the files; nothing is written to the folder.
 */
export function FolderPickerButton({ onPick, disabled, className, children }: {
  onPick: (files: File[]) => void
  disabled?: boolean
  className?: string
  children: ReactNode
}) {
  return (
    <label className={cn('cursor-pointer focus-within:outline-2 focus-within:outline-primary', disabled && 'pointer-events-none opacity-50', className)}>
      {children}
      <input
        type="file"
        className="sr-only"
        disabled={disabled}
        // React has no prop for these attributes.
        ref={(input) => { input?.setAttribute('webkitdirectory', ''); input?.setAttribute('directory', '') }}
        onChange={(e) => {
          const files = Array.from(e.target.files ?? [])
          e.target.value = ''
          if (files.length > 0) onPick(files)
        }}
      />
    </label>
  )
}

/** Packing, then uploading: one line for each step. */
export function FolderUploadProgress({ packing, sent, uploading }: {
  packing: { done: number; total: number } | null
  sent: { loaded: number; total: number } | null
  uploading: boolean
}) {
  if (packing) {
    return (
      <p role="status" className="text-[11px] text-muted-foreground">
        {packing.done === 0 ? 'Reading the folder and its .gitignore files…' : `Packing the files to read: ${packing.done} of ${packing.total}`}
      </p>
    )
  }
  return <UploadProgress sent={sent} pending={uploading} />
}
