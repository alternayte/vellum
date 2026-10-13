import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { selectClass } from '@/components/settings/fields'
import { shownInline } from '@/lib/mutation-errors'
import { ASSUMPTION_KINDS, ASSUMPTION_LABELS, costsApi, describeError, type AssumptionInput, type CostAssumption } from '@/lib/costs-api'

interface Props {
  projectId: string
  branchId: string | null
  /** Null for a new assumption. */
  assumption: CostAssumption | null
  /** The elements, operations and channels the number can be about. */
  subjects: { id: string; label: string; group: string }[]
  onClose: () => void
}

const labelClass = 'block text-xs font-medium text-muted-foreground'

/** A named number that usage lines read: requests, stored GB, egress GB or messages for one month. */
export function AssumptionDialog({ projectId, branchId, assumption, subjects, onClose }: Props) {
  const [form, setForm] = useState<AssumptionInput>(() => assumption
    ? { name: assumption.name, kind: assumption.kind, subjectId: assumption.subjectId, value: assumption.value, unit: assumption.unit, label: assumption.label, source: assumption.source, effectiveDate: assumption.effectiveDate }
    : { name: '', kind: 'requests', subjectId: null, value: 0, unit: 'requests/month', label: 'declared', source: null, effectiveDate: new Date().toISOString().slice(0, 10) })
  const set = <K extends keyof AssumptionInput>(field: K, value: AssumptionInput[K]) => setForm({ ...form, [field]: value })

  const save = useMutation({
    mutationFn: () => (assumption ? costsApi.changeAssumption(projectId, branchId, assumption.id, form) : costsApi.addAssumption(projectId, branchId, form)),
    onSuccess: onClose, onError: shownInline,
  })
  const remove = useMutation({ mutationFn: () => costsApi.removeAssumption(projectId, branchId, assumption!.id), onSuccess: onClose, onError: shownInline })
  const failure = save.error ?? remove.error
  const needsSource = form.label !== 'declared'
  const subjectMissing = form.subjectId !== null && !subjects.some((s) => s.id === form.subjectId)

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 sm:max-w-xl">
        <DialogHeader>
          <DialogTitle>{assumption ? assumption.name : 'New assumption'}</DialogTitle>
          <DialogDescription>
            A number that the estimate uses, with how it is known. A measured or an inferred number states its source.
          </DialogDescription>
        </DialogHeader>

        <form id="assumption-form" className="grid min-h-0 flex-1 gap-3 overflow-y-auto pr-1 sm:grid-cols-2" onSubmit={(e) => { e.preventDefault(); save.mutate() }}>
          <label className={`${labelClass} sm:col-span-2`}>
            Name
            <Input className="mt-1" value={form.name} onChange={(e) => set('name', e.target.value)} placeholder="Orders placed" maxLength={200} required />
          </label>
          <label className={labelClass}>
            Kind
            <select className={`${selectClass} mt-1`} value={form.kind} onChange={(e) => set('kind', e.target.value)}>
              {ASSUMPTION_KINDS.map((k) => <option key={k} value={k}>{k}</option>)}
            </select>
          </label>
          <label className={labelClass}>
            About
            <select className={`${selectClass} mt-1`} value={form.subjectId ?? ''} onChange={(e) => set('subjectId', e.target.value || null)}>
              <option value="">The whole system</option>
              {subjectMissing && <option value={form.subjectId!}>Not in the model shown on the canvas</option>}
              {subjects.map((s) => <option key={s.id} value={s.id}>{s.group.replace(/s$/, '')}: {s.label}</option>)}
            </select>
          </label>
          <label className={labelClass}>
            Value
            <Input className="mt-1" type="number" min={0} step="any" value={form.value} onChange={(e) => set('value', e.target.value === '' ? 0 : Number(e.target.value))} required />
          </label>
          <label className={labelClass}>
            Unit
            <Input className="mt-1" value={form.unit} onChange={(e) => set('unit', e.target.value)} placeholder="requests/month, GB, messages/day" maxLength={50} required />
          </label>
          <label className={labelClass}>
            Label
            <select className={`${selectClass} mt-1`} value={form.label} onChange={(e) => set('label', e.target.value)}>
              {ASSUMPTION_LABELS.map((l) => <option key={l} value={l}>{l}</option>)}
            </select>
          </label>
          <label className={labelClass}>
            Effective from
            <Input className="mt-1" type="date" value={form.effectiveDate} onChange={(e) => set('effectiveDate', e.target.value)} required />
          </label>
          <label className={`${labelClass} sm:col-span-2`}>
            Source{needsSource ? '' : ' (optional)'}
            <Input className="mt-1" value={form.source ?? ''} onChange={(e) => set('source', e.target.value || null)} placeholder="Where the number comes from, for example a dashboard and its period" maxLength={2000} required={needsSource} />
          </label>
          <p className="text-[11px] leading-relaxed text-muted-foreground sm:col-span-2">
            A unit is a name, with an optional period: hour, day or month. A number with no period is the amount for one month.
          </p>
        </form>

        {failure && <p role="alert" className="text-xs text-destructive">{describeError(failure)}</p>}
        <div className="flex items-center justify-between gap-2">
          {assumption ? <Button type="button" variant="ghost" size="sm" disabled={remove.isPending} onClick={() => remove.mutate()}>Delete</Button> : <span />}
          <div className="flex gap-2">
            <Button type="button" variant="ghost" size="sm" onClick={onClose}>Cancel</Button>
            <Button type="submit" form="assumption-form" size="sm" disabled={save.isPending}>Save</Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
