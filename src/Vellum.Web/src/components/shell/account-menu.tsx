import { DropdownMenu } from 'radix-ui'
import { useNavigate } from '@tanstack/react-router'
import { useQuery } from '@tanstack/react-query'
import { Check, FolderOpen, LogOut, Server, Settings, UserRound } from 'lucide-react'
import type { ReactNode } from 'react'
import { useAuth } from '@/hooks/use-auth'
import { initialsOf, useMyWorkspaces } from '@/hooks/use-my-workspaces'
import { api, type InstanceInfo } from '@/lib/settings-api'
import { cn } from '@/lib/utils'


const itemClass =
  'flex cursor-default items-center gap-2 rounded-md px-2 py-1.5 text-sm outline-none data-[highlighted]:bg-muted data-[disabled]:opacity-50'
const icon = 'h-3.5 w-3.5 shrink-0 text-muted-foreground'

function Item({ children, onSelect, className }: { children: ReactNode; onSelect: () => void; className?: string }) {
  return (
    <DropdownMenu.Item onSelect={onSelect} className={cn(itemClass, className)}>
      {children}
    </DropdownMenu.Item>
  )
}

/**
 * The avatar of the signed-in person. It opens the account menu: who is signed in, the account
 * page, the settings of the workspace in use, a switch to another workspace, the project list,
 * the instance settings for an instance administrator, and log out.
 */
export function AccountMenu({ workspaceId }: { workspaceId?: string | null }) {
  const { user, logout } = useAuth()
  const navigate = useNavigate()
  const workspaces = useMyWorkspaces()
  const instance = useQuery({ queryKey: ['instance'], queryFn: () => api<InstanceInfo>('GET', '/api/instance'), staleTime: 60_000 })
  const initials = initialsOf(user?.displayName, user?.email)
  const list = workspaces.data ?? []
  const current = list.find((w) => w.id === workspaceId) ?? null
  // The instance settings are a section of the settings page of a workspace.
  const settingsHome = current ?? list[0] ?? null

  return (
    <DropdownMenu.Root modal={false}>
      <DropdownMenu.Trigger asChild>
        <button
          type="button"
          className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-primary text-[10px] font-medium text-primary-foreground focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary data-[state=open]:ring-2 data-[state=open]:ring-primary/40"
          aria-label={`Account menu${user?.displayName ? ` for ${user.displayName}` : ''}`}
          title="Account"
        >
          {initials}
        </button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content
          align="end"
          sideOffset={6}
          collisionPadding={8}
          className="z-50 max-h-[var(--radix-dropdown-menu-content-available-height)] w-64 max-w-[calc(100vw-16px)] overflow-y-auto rounded-lg bg-popover p-1 text-popover-foreground shadow-md ring-1 ring-foreground/10"
        >
          <DropdownMenu.Label className="px-2 py-1.5">
            <span className="block truncate text-sm font-medium text-foreground">{user?.displayName || user?.email}</span>
            {user?.displayName && <span className="block truncate text-xs text-muted-foreground">{user.email}</span>}
          </DropdownMenu.Label>
          <DropdownMenu.Separator className="my-1 h-px bg-border" />
          <Item onSelect={() => navigate({ to: '/account' })}>
            <UserRound aria-hidden="true" className={icon} />
            Account
          </Item>
          {current && (
            <Item onSelect={() => navigate({ to: '/workspaces/$workspaceId/settings', params: { workspaceId: current.id }, search: {} })}>
              <Settings aria-hidden="true" className={icon} />
              <span className="min-w-0 truncate">Workspace settings</span>
            </Item>
          )}
          <Item onSelect={() => navigate({ to: '/', search: current ? { workspace: current.id } : {} })}>
            <FolderOpen aria-hidden="true" className={icon} />
            All projects
          </Item>
          {list.length > 1 && (
            <>
              <DropdownMenu.Separator className="my-1 h-px bg-border" />
              <DropdownMenu.Label className="px-2 pt-1 pb-0.5 text-[10px] uppercase tracking-wider text-muted-foreground">Switch workspace</DropdownMenu.Label>
              <DropdownMenu.RadioGroup
                value={current?.id ?? ''}
                onValueChange={(id) => navigate({ to: '/', search: { workspace: id } })}
              >
                {list.map((w) => (
                  <DropdownMenu.RadioItem key={w.id} value={w.id} className={itemClass}>
                    <span className="flex h-3.5 w-3.5 shrink-0 items-center justify-center">
                      <DropdownMenu.ItemIndicator>
                        <Check aria-hidden="true" className="h-3.5 w-3.5 text-primary" />
                      </DropdownMenu.ItemIndicator>
                    </span>
                    <span className="min-w-0 truncate">{w.name}</span>
                  </DropdownMenu.RadioItem>
                ))}
              </DropdownMenu.RadioGroup>
            </>
          )}
          {instance.data?.isInstanceAdmin && settingsHome && (
            <>
              <DropdownMenu.Separator className="my-1 h-px bg-border" />
              <Item onSelect={() => navigate({ to: '/workspaces/$workspaceId/settings', params: { workspaceId: settingsHome.id }, search: { section: 'instance' } })}>
                <Server aria-hidden="true" className={icon} />
                Instance settings
              </Item>
            </>
          )}
          <DropdownMenu.Separator className="my-1 h-px bg-border" />
          <Item
            onSelect={async () => {
              await logout()
              navigate({ to: '/login' })
            }}
          >
            <LogOut aria-hidden="true" className={icon} />
            Log out
          </Item>
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  )
}
