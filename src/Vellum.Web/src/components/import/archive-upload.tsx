import { formatBytes } from '@/lib/import-api'

/** How much of the file went to the server. The server checks the archive after the last byte. */
export function UploadProgress({ sent, pending }: { sent: { loaded: number; total: number } | null; pending: boolean }) {
  if (!sent || !pending) return null
  const percent = sent.total > 0 ? Math.min(100, Math.round((sent.loaded / sent.total) * 100)) : 0
  const checking = sent.loaded >= sent.total
  return (
    <div role="status" className="space-y-1">
      <div
        className="h-1.5 overflow-hidden rounded-full bg-muted"
        role="progressbar" aria-label="Upload" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}
      >
        <div className="h-full rounded-full bg-primary transition-[width]" style={{ width: `${percent}%` }} />
      </div>
      <p className="text-[11px] text-muted-foreground">
        {checking
          ? 'Uploaded. The server checks every entry of the archive…'
          : `Uploading: ${formatBytes(sent.loaded)} of ${formatBytes(sent.total)} (${percent}%)`}
      </p>
    </div>
  )
}
