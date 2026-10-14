"use client"

import * as React from "react"
import { Dialog as DialogPrimitive } from "radix-ui"

import { cn } from "@/lib/utils"
import { Button } from "@/components/ui/button"
import { XIcon } from "lucide-react"

function Dialog({
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Root>) {
  return <DialogPrimitive.Root data-slot="dialog" {...props} />
}

function DialogTrigger({
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Trigger>) {
  return <DialogPrimitive.Trigger data-slot="dialog-trigger" {...props} />
}

function DialogPortal({
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Portal>) {
  return <DialogPrimitive.Portal data-slot="dialog-portal" {...props} />
}

function DialogClose({
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Close>) {
  return <DialogPrimitive.Close data-slot="dialog-close" {...props} />
}

function DialogOverlay({
  className,
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Overlay>) {
  return (
    <DialogPrimitive.Overlay
      data-slot="dialog-overlay"
      className={cn(
        "fixed inset-0 isolate z-50 bg-black/50 duration-100 supports-backdrop-filter:backdrop-blur-sm data-open:animate-in data-open:fade-in-0 data-closed:animate-out data-closed:fade-out-0",
        className
      )}
      {...props}
    />
  )
}

/**
 * The open dialogs, the lowest first. Only the last one is in the accessibility tree and takes
 * input: every dialog under it is `inert` and `aria-hidden`. The primitive hides the rest of
 * the page only when a dialog opens, so a dialog that opened later or rendered again could
 * leave the one under it readable.
 */
const openDialogs: HTMLElement[] = []

function layerDialogs() {
  openDialogs.forEach((dialog, index) => {
    // The primitive renders each dialog into its own element at the end of the body.
    const layer = dialog.parentElement && dialog.parentElement !== document.body ? dialog.parentElement : dialog
    const below = index < openDialogs.length - 1
    dialog.inert = below
    if (below) {
      layer.setAttribute("aria-hidden", "true")
      layer.dataset.dialogBelow = ""
    } else {
      if (layer.dataset.dialogBelow !== undefined || layer.getAttribute("aria-hidden") === "true") layer.removeAttribute("aria-hidden")
      delete layer.dataset.dialogBelow
    }
  })
}

function useDialogLayer() {
  const [dialog, setDialog] = React.useState<HTMLElement | null>(null)
  React.useLayoutEffect(() => {
    if (!dialog) return
    openDialogs.push(dialog)
    layerDialogs()
    return () => {
      openDialogs.splice(openDialogs.indexOf(dialog), 1)
      dialog.inert = false
      const layer = dialog.parentElement && dialog.parentElement !== document.body ? dialog.parentElement : dialog
      if (layer.dataset.dialogBelow !== undefined) {
        layer.removeAttribute("aria-hidden")
        delete layer.dataset.dialogBelow
      }
      layerDialogs()
    }
  }, [dialog])
  return setDialog
}

function DialogContent({
  className,
  children,
  showCloseButton = true,
  onOpenAutoFocus,
  onCloseAutoFocus,
  ref,
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Content> & {
  showCloseButton?: boolean
}) {
  // The control that had the focus when the dialog opened. The primitive gives the focus
  // back only to a DialogTrigger; most dialogs here are opened from state (a menu item, a
  // key, a row), and they give it back to this control.
  const opener = React.useRef<HTMLElement | null>(null)
  const setLayer = useDialogLayer()
  const contentRef = React.useCallback((node: HTMLDivElement | null) => {
    setLayer(node)
    if (typeof ref === "function") ref(node)
    else if (ref) ref.current = node
  }, [ref, setLayer])
  return (
    <DialogPortal>
      <DialogOverlay />
      <DialogPrimitive.Content
        ref={contentRef}
        data-slot="dialog-content"
        // The field that takes the focus first is marked with data-autofocus, not with
        // autoFocus: React focuses an autoFocus field before the dialog has noted which
        // control had the focus, and the focus then has no place to go back to on close.
        onCloseAutoFocus={(event) => {
          onCloseAutoFocus?.(event)
          const target = opener.current
          if (event.defaultPrevented || !target?.isConnected) return
          event.preventDefault()
          target.focus()
        }}
        onOpenAutoFocus={(event) => {
          // A second mount of the same dialog (React's strict mode in development) finds the
          // focus inside the dialog already, and keeps the control it noted first.
          const active = document.activeElement
          const inside = (event.currentTarget as HTMLElement | null)?.contains(active) ?? false
          if (active instanceof HTMLElement && active !== document.body && !inside) opener.current = active
          onOpenAutoFocus?.(event)
          if (event.defaultPrevented) return
          const first = (event.currentTarget as HTMLElement | null)?.querySelector<HTMLElement>('[data-autofocus]')
          if (first) {
            event.preventDefault()
            first.focus()
          }
        }}
        className={cn(
          "fixed top-1/2 left-1/2 z-50 grid w-full max-w-[calc(100%-2rem)] -translate-x-1/2 -translate-y-1/2 gap-4 rounded-xl bg-popover p-4 text-sm text-popover-foreground ring-1 ring-foreground/10 duration-100 outline-none sm:max-w-sm data-open:animate-in data-open:fade-in-0 data-open:zoom-in-95 data-closed:animate-out data-closed:fade-out-0 data-closed:zoom-out-95",
          className
        )}
        {...props}
      >
        {children}
        {showCloseButton && (
          <DialogPrimitive.Close data-slot="dialog-close" asChild>
            <Button
              variant="ghost"
              className="absolute top-2 right-2"
              size="icon-sm"
            >
              <XIcon
              />
              <span className="sr-only">Close</span>
            </Button>
          </DialogPrimitive.Close>
        )}
      </DialogPrimitive.Content>
    </DialogPortal>
  )
}

function DialogHeader({ className, ...props }: React.ComponentProps<"div">) {
  return (
    <div
      data-slot="dialog-header"
      className={cn("flex flex-col gap-2", className)}
      {...props}
    />
  )
}

function DialogFooter({
  className,
  showCloseButton = false,
  children,
  ...props
}: React.ComponentProps<"div"> & {
  showCloseButton?: boolean
}) {
  return (
    <div
      data-slot="dialog-footer"
      className={cn(
        "-mx-4 -mb-4 flex flex-col-reverse gap-2 rounded-b-xl border-t bg-muted/50 p-4 sm:flex-row sm:justify-end",
        className
      )}
      {...props}
    >
      {children}
      {showCloseButton && (
        <DialogPrimitive.Close asChild>
          <Button variant="outline">Close</Button>
        </DialogPrimitive.Close>
      )}
    </div>
  )
}

function DialogTitle({
  className,
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Title>) {
  return (
    <DialogPrimitive.Title
      data-slot="dialog-title"
      className={cn(
        "font-heading text-base leading-none font-medium",
        className
      )}
      {...props}
    />
  )
}

function DialogDescription({
  className,
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Description>) {
  return (
    <DialogPrimitive.Description
      data-slot="dialog-description"
      className={cn(
        "text-sm text-muted-foreground *:[a]:underline *:[a]:underline-offset-3 *:[a]:hover:text-foreground",
        className
      )}
      {...props}
    />
  )
}

export {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogOverlay,
  DialogPortal,
  DialogTitle,
  DialogTrigger,
}
