import { sourceSides, type Proposal } from '@/lib/import-api'

function text(value: unknown): string {
  if (value === null || value === undefined || value === '') return 'empty'
  return typeof value === 'string' ? value : JSON.stringify(value)
}

/**
 * Every side of one fact: the model, this Source and each other Source that holds it, one
 * column each. Repository text is shown as text. No column is marked as the winner.
 */
export function SourceSides({ proposal, sourceName }: { proposal: Proposal; sourceName: string }) {
  const { fields, model, here } = sourceSides(proposal)
  if (fields.length === 0) return null
  return (
    <div className="overflow-x-auto rounded-md border border-border">
      <table className="w-full min-w-[28rem] table-fixed text-left text-xs">
        <thead className="text-[10px] uppercase tracking-wider text-muted-foreground">
          <tr className="border-b border-border">
            <th scope="col" className="w-28 px-2 py-1 font-medium">Field</th>
            <th scope="col" className="px-2 py-1 font-medium">In the model</th>
            <th scope="col" className="px-2 py-1 font-medium">
              <span className="block truncate normal-case tracking-normal text-foreground" title={sourceName}>{sourceName}</span>
              this scan
            </th>
            {proposal.otherSources.map((other) => (
              <th key={other.sourceId + other.source} scope="col" className="px-2 py-1 font-medium">
                <span className="block truncate normal-case tracking-normal text-foreground" title={other.source}>{other.sourceName}</span>
                {other.factStatus}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {fields.map((field) => {
            const values = [here[field], ...proposal.otherSources.map((o) => o.values[field])]
            const disagree = new Set(values.map((v) => text(v))).size > 1
            return (
              <tr key={field} className="border-b border-border align-top last:border-b-0">
                <th scope="row" className="break-words px-2 py-1 font-mono font-normal text-muted-foreground">{field}</th>
                <td className="break-all px-2 py-1 text-muted-foreground">{text(model[field])}</td>
                <td className="break-all px-2 py-1 text-foreground" style={disagree ? { color: 'var(--trace-event)' } : undefined}>{text(here[field])}</td>
                {proposal.otherSources.map((other) => (
                  <td key={other.sourceId + other.source} className="break-all px-2 py-1 text-foreground" style={disagree ? { color: 'var(--trace-event)' } : undefined}>
                    {text(other.values[field])}
                  </td>
                ))}
              </tr>
            )
          })}
        </tbody>
      </table>
      {proposal.otherSources.some((o) => o.citation) && (
        <ul className="border-t border-border px-2 py-1 font-mono text-[10px] text-muted-foreground">
          {proposal.otherSources.filter((o) => o.citation).map((o) => <li key={o.citation!} className="truncate" title={o.citation!}>{o.citation}</li>)}
        </ul>
      )}
    </div>
  )
}

/**
 * "Same as X?": the element of another Source has the name of a fact of this scan. Vellum
 * never joins them by name. Yes binds the element to this Source's key; no adds a new element.
 */
export function BindQuestion({ proposal, sourceName, answer, onAnswer }: {
  proposal: Proposal
  sourceName: string
  answer: boolean | undefined
  onAnswer: (same: boolean) => void
}) {
  const element = (proposal.current?.name as string | undefined) ?? 'this element'
  const holders = [...new Set(proposal.otherSources.map((o) => o.sourceName))]
  const name = `bind-${proposal.id}`
  return (
    <fieldset className="mt-2 space-y-2 pb-1 text-xs">
      <legend className="text-foreground">
        Is “{proposal.name}” in {sourceName} the same as the element “{element}”{holders.length > 0 ? ` that ${holders.join(', ')} holds` : ''}?
      </legend>
      <SourceSides proposal={proposal} sourceName={sourceName} />
      <div className="grid gap-1.5 sm:grid-cols-2">
        <label className="flex items-start gap-2 rounded-md border border-border px-2.5 py-1.5">
          <input type="radio" name={name} className="mt-0.5 accent-[var(--primary)]" checked={answer === true} onChange={() => onAnswer(true)} />
          <span><span className="text-foreground">Yes, the same element.</span> <span className="text-muted-foreground">Bind it to this Source. Its values come back as a proposal.</span></span>
        </label>
        <label className="flex items-start gap-2 rounded-md border border-border px-2.5 py-1.5">
          <input type="radio" name={name} className="mt-0.5 accent-[var(--primary)]" checked={answer === false} onChange={() => onAnswer(false)} />
          <span><span className="text-foreground">No, another thing.</span> <span className="text-muted-foreground">Propose it as a new element.</span></span>
        </label>
      </div>
    </fieldset>
  )
}
