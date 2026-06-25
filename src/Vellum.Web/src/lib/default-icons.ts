import {
  Database, Server, Monitor, User, Component, Box,
  Globe, Cloud, Shield, Zap, Cpu, HardDrive, Smartphone,
  Mail, Lock, Key, Wifi, FileText, Folder, GitBranch,
  Layers, Network, Terminal, Code, Cog, Bell, Search,
  BarChart, Clock, Archive,
} from 'lucide-react'
import type { LucideIcon } from 'lucide-react'

const KIND_DEFAULTS: Record<string, string> = {
  store: 'database',
  app: 'server',
  system: 'monitor',
  actor: 'user',
  component: 'component',
}

export function defaultIconForKind(kind: string): string {
  return KIND_DEFAULTS[kind] ?? 'box'
}

export const ARCHITECTURE_ICONS: { name: string; icon: LucideIcon }[] = [
  { name: 'database', icon: Database },
  { name: 'server', icon: Server },
  { name: 'monitor', icon: Monitor },
  { name: 'user', icon: User },
  { name: 'component', icon: Component },
  { name: 'box', icon: Box },
  { name: 'globe', icon: Globe },
  { name: 'cloud', icon: Cloud },
  { name: 'shield', icon: Shield },
  { name: 'zap', icon: Zap },
  { name: 'cpu', icon: Cpu },
  { name: 'hard-drive', icon: HardDrive },
  { name: 'smartphone', icon: Smartphone },
  { name: 'mail', icon: Mail },
  { name: 'lock', icon: Lock },
  { name: 'key', icon: Key },
  { name: 'wifi', icon: Wifi },
  { name: 'file-text', icon: FileText },
  { name: 'folder', icon: Folder },
  { name: 'git-branch', icon: GitBranch },
  { name: 'layers', icon: Layers },
  { name: 'network', icon: Network },
  { name: 'terminal', icon: Terminal },
  { name: 'code', icon: Code },
  { name: 'cog', icon: Cog },
  { name: 'bell', icon: Bell },
  { name: 'search', icon: Search },
  { name: 'bar-chart', icon: BarChart },
  { name: 'clock', icon: Clock },
  { name: 'archive', icon: Archive },
]

export function getIconComponent(name: string): LucideIcon | null {
  return ARCHITECTURE_ICONS.find((i) => i.name === name)?.icon ?? null
}
