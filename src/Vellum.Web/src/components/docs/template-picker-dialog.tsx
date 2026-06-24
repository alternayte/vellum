import { useEffect, useState } from 'react'
import { loadTemplates, type DocTemplate } from '@/lib/doc-templates'
import { Input } from '@/components/ui/input'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  Scale,
  FileText,
  Boxes,
  File,
} from 'lucide-react'

const ICON_MAP: Record<string, React.ComponentType<{ className?: string }>> = {
  scale: Scale,
  'file-text': FileText,
  boxes: Boxes,
}

interface TemplatePickerDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  onCreateDoc: (title: string, content: string, adrStatus?: string) => void
}

export function TemplatePickerDialog({
  open,
  onOpenChange,
  onCreateDoc,
}: TemplatePickerDialogProps) {
  const [templates, setTemplates] = useState<DocTemplate[]>([])
  const [selected, setSelected] = useState<DocTemplate | null>(null)
  const [title, setTitle] = useState('')

  useEffect(() => {
    if (open) {
      loadTemplates().then(setTemplates)
      setSelected(null)
      setTitle('')
    }
  }, [open])

  const handleCreate = () => {
    const docTitle = title.trim() || (selected ? `New ${selected.name}` : 'Untitled')
    const content = selected?.content ?? ''
    const adrStatus = selected?.id === 'adr' ? 'proposed' : undefined
    onCreateDoc(docTitle, content, adrStatus)
    onOpenChange(false)
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-[480px]">
        <DialogHeader>
          <DialogTitle>New Document</DialogTitle>
        </DialogHeader>

        <div className="grid grid-cols-2 gap-3 py-4">
          {templates.map((template) => {
            const IconComponent = ICON_MAP[template.icon] ?? FileText
            const isSelected = selected?.id === template.id
            return (
              <button
                key={template.id}
                className={`flex flex-col items-center gap-2 rounded-lg border p-4 text-center transition-colors hover:bg-accent ${
                  isSelected ? 'border-primary bg-accent' : 'border-border'
                }`}
                onClick={() => setSelected(template)}
              >
                <IconComponent className="h-6 w-6 text-muted-foreground" />
                <span className="text-sm font-medium">{template.name}</span>
                <span className="text-xs text-muted-foreground">{template.description}</span>
              </button>
            )
          })}
          <button
            className={`flex flex-col items-center gap-2 rounded-lg border p-4 text-center transition-colors hover:bg-accent ${
              selected === null ? 'border-primary bg-accent' : 'border-border'
            }`}
            onClick={() => setSelected(null)}
          >
            <File className="h-6 w-6 text-muted-foreground" />
            <span className="text-sm font-medium">Blank</span>
            <span className="text-xs text-muted-foreground">Empty document</span>
          </button>
        </div>

        <Input
          placeholder="Document title"
          value={title}
          onChange={(e) => setTitle(e.target.value)}
          autoFocus
        />

        <div className="flex justify-end pt-2">
          <Button onClick={handleCreate}>
            Create
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}
