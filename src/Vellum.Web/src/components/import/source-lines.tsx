import { useQuery } from '@tanstack/react-query'
import { ExternalLink } from 'lucide-react'
import { CONTENT_NOT_HELD, importApi, type ProposalEvidence } from '@/lib/import-api'

/** The cited lines of one file at the commit of a scan, as text. A pushed Snapshot holds no lines: it says so and asks for none. */
export function SourceLines({ projectId, scanId, contentHeld, evidence }: { projectId: string; scanId: string; contentHeld: boolean; evidence: ProposalEvidence }) {
  const source = useQuery({
    queryKey: ['projects', projectId, 'imports', scanId, 'source', evidence.path, evidence.startLine, evidence.endLine],
    queryFn: () => importApi.source(projectId, scanId, evidence),
    staleTime: Infinity,
    enabled: contentHeld,
  })
  return (
    <figure className="overflow-hidden rounded-md border border-border">
      <figcaption className="flex items-center gap-2 border-b border-border bg-muted/40 px-2 py-1 font-mono text-[10px] text-muted-foreground">
        <span className="min-w-0 flex-1 truncate">{evidence.path}:{evidence.startLine}-{evidence.endLine}</span>
        <span className="shrink-0">{evidence.rule}</span>
        {evidence.url && (
          <a href={evidence.url} target="_blank" rel="noreferrer noopener" className="flex shrink-0 items-center gap-1 rounded text-foreground hover:underline focus-visible:outline-2 focus-visible:outline-primary">
            GitHub <ExternalLink className="h-3 w-3" />
          </a>
        )}
      </figcaption>
      {!contentHeld ? (
        <p className="px-2 py-1.5 text-xs text-muted-foreground" title={CONTENT_NOT_HELD}>Content not held. Open these lines on the machine that pushed them.</p>
      ) : source.isError ? (
        <p className="px-2 py-1.5 text-xs text-muted-foreground">{evidence.url ? 'These lines are not stored. Open them on GitHub.' : 'This line is not stored any more.'}</p>
      ) : (
        <pre tabIndex={0} className="max-h-48 overflow-auto bg-background px-2 py-1.5 font-mono text-[11px] leading-relaxed text-foreground focus-visible:outline-2 focus-visible:outline-primary">
          {source.data
            ? source.data.lines.map((line, i) => (
                <div key={i} className="flex gap-3">
                  <span className="w-8 shrink-0 select-none text-right text-muted-foreground">{source.data.startLine + i}</span>
                  {/* Repository text is shown as text. It is never rendered as markup. */}
                  <span className="whitespace-pre">{line}</span>
                </div>
              ))
            : 'Loading…'}
        </pre>
      )}
    </figure>
  )
}
