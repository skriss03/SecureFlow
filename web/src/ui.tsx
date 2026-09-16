import type { ReactNode } from 'react'
import type { ComponentType, ImpactLevel, Severity } from './types'
import {
  Activity, Cloud, Cog, Database, ExternalLink, Globe, HardDrive, KeyRound, ListOrdered, Lock, Monitor, Scale, Server, User, Zap,
} from 'lucide-react'

export const severityColor: Record<Severity, string> = {
  Critical: 'var(--color-critical)', High: 'var(--color-high)', Medium: 'var(--color-medium)', Low: 'var(--color-low)', Info: 'var(--color-info)',
}
export const severityRank: Record<Severity, number> = { Critical: 4, High: 3, Medium: 2, Low: 1, Info: 0 }

export const impactColor: Record<ImpactLevel, string> = {
  Killed: '#7f1d1d', Down: 'var(--color-down)', Degraded: 'var(--color-degraded)', Ok: 'var(--color-ok)',
}

export function SeverityBadge({ severity, small }: { severity: Severity; small?: boolean }) {
  return (
    <span
      className={`inline-flex items-center rounded-md font-semibold uppercase tracking-wide text-white ${small ? 'px-1.5 py-0.5 text-[10px]' : 'px-2 py-0.5 text-xs'}`}
      style={{ background: severityColor[severity] }}
    >
      {severity}
    </span>
  )
}

export function Pill({ children, tone = 'muted', title }: { children: ReactNode; tone?: 'muted' | 'accent' | 'ok' | 'warn' | 'danger'; title?: string }) {
  const tones = {
    muted: 'bg-panel-2 text-muted border-line',
    accent: 'bg-accent/15 text-accent border-accent/40',
    ok: 'bg-ok/15 text-ok border-ok/40',
    warn: 'bg-medium/15 text-medium border-medium/40',
    danger: 'bg-critical/15 text-critical border-critical/40',
  }
  return <span title={title} className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs ${tones[tone]}`}>{children}</span>
}

export function Button({ children, onClick, variant = 'primary', disabled, className = '', type = 'button', title }: {
  children: ReactNode; onClick?: () => void; variant?: 'primary' | 'ghost' | 'danger'; disabled?: boolean; className?: string; type?: 'button' | 'submit'; title?: string
}) {
  const v = {
    primary: 'bg-accent text-bg hover:brightness-110',
    ghost: 'bg-panel-2 text-ink border border-line hover:bg-line',
    danger: 'bg-critical text-white hover:brightness-110',
  }[variant]
  return (
    <button type={type} title={title} onClick={onClick} disabled={disabled}
      className={`inline-flex items-center gap-2 rounded-lg px-3 py-1.5 text-sm font-medium transition disabled:cursor-not-allowed disabled:opacity-50 ${v} ${className}`}>
      {children}
    </button>
  )
}

export function Card({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <div className={`rounded-xl border border-line bg-panel ${className}`}>{children}</div>
}

export function TypeIcon({ type, size = 16 }: { type: ComponentType; size?: number }) {
  const p = { size, strokeWidth: 1.8 }
  switch (type) {
    case 'User': return <User {...p} />
    case 'Gateway': return <Globe {...p} />
    case 'LoadBalancer': return <Scale {...p} />
    case 'Cdn': return <Cloud {...p} />
    case 'WebApp': return <Monitor {...p} />
    case 'Api': return <Server {...p} />
    case 'Worker': return <Cog {...p} />
    case 'Database': return <Database {...p} />
    case 'Cache': return <Zap {...p} />
    case 'Queue': return <ListOrdered {...p} />
    case 'ObjectStorage': return <HardDrive {...p} />
    case 'Identity': return <KeyRound {...p} />
    case 'Secrets': return <Lock {...p} />
    case 'Monitoring': return <Activity {...p} />
    case 'External': return <ExternalLink {...p} />
  }
}

export function heatToColor(heat: number | undefined): string {
  const h = Math.max(0, Math.min(1, heat ?? 0))
  if (h === 0) return '#3b4a6b'
  // slate → amber → red
  const r = Math.round(h < 0.5 ? 120 + (245 - 120) * (h / 0.5) : 245 - (245 - 239) * ((h - 0.5) / 0.5))
  const g = Math.round(h < 0.5 ? 140 + (158 - 140) * (h / 0.5) : 158 - (158 - 68) * ((h - 0.5) / 0.5))
  const b = Math.round(h < 0.5 ? 180 - (180 - 11) * (h / 0.5) : 11 + (68 - 11) * ((h - 0.5) / 0.5))
  return `rgb(${r},${g},${b})`
}

export function timeAgo(iso: string): string {
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000)
  if (s < 60) return `${Math.round(s)}s ago`
  if (s < 3600) return `${Math.round(s / 60)}m ago`
  if (s < 86400) return `${Math.round(s / 3600)}h ago`
  return `${Math.round(s / 86400)}d ago`
}
