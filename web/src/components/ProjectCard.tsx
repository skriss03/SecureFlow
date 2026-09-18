import { Link } from 'react-router-dom'
import type { ProjectSummary, Severity } from '../types'
import { Pill, severityColor, timeAgo } from '../ui'

export function scoreColor(v: number): string {
  return v >= 75 ? 'var(--color-ok)' : v >= 50 ? 'var(--color-medium)' : v >= 30 ? 'var(--color-high)' : 'var(--color-critical)'
}

const CHIP_ORDER: Severity[] = ['Critical', 'High', 'Medium', 'Low']

function Score({ label, value, grade }: { label: string; value: number; grade: string }) {
  const color = scoreColor(value)
  return (
    <div className="min-w-0 flex-1">
      <div className="flex items-baseline justify-between gap-1.5">
        <span className="text-[10px] uppercase tracking-wide text-muted">{label}</span>
        <span className="text-[11px] font-bold" style={{ color }}>{grade}</span>
      </div>
      <div className="flex items-baseline gap-1">
        <span className="font-mono text-[22px] font-bold leading-tight" style={{ color }}>{value}</span>
        <span className="text-[10px] text-muted">/100</span>
      </div>
      <div className="mt-1 h-1 overflow-hidden rounded-full bg-line">
        <div className="h-full rounded-full" style={{ width: `${Math.max(0, Math.min(100, value))}%`, background: color }} />
      </div>
    </div>
  )
}

export default function ProjectCard({ p }: { p: ProjectSummary }) {
  const ready = p.status === 'Ready'
  const busy = p.status !== 'Ready' && p.status !== 'Error'
  const critical = p.openBySeverity?.Critical ?? 0
  const edge = p.status === 'Error' || critical > 0
    ? 'var(--color-critical)'
    : busy
      ? 'var(--color-accent)'
      : ready && p.resilience >= 75 && p.security >= 75
        ? 'var(--color-ok)'
        : 'var(--color-line)'

  const chips = CHIP_ORDER
    .map(s => ({ s, n: p.openBySeverity?.[s] ?? 0 }))
    .filter(c => c.n > 0)

  return (
    <Link
      to={`/p/${p.id}`}
      style={{ borderTopColor: edge }}
      className="group block rounded-xl border border-line border-t-[3px] bg-panel px-4 pb-3 pt-3.5 transition hover:-translate-y-0.5 hover:border-accent hover:shadow-[0_10px_26px_rgba(0,0,0,.5)]"
    >
      <div className="flex items-start gap-2">
        <div className="min-w-0 flex-1">
          <div className="truncate text-sm font-semibold">{p.name}</div>
          <div className="mt-0.5 truncate text-[11px] text-muted">{p.groupName ?? 'Ungrouped'} · {p.source}</div>
        </div>
        <Pill tone={ready ? 'ok' : p.status === 'Error' ? 'danger' : 'accent'}>{p.status}</Pill>
      </div>

      {ready ? (
        <>
          <div className="mt-3 flex gap-3">
            <Score label="Resilience" value={p.resilience} grade={p.resilienceGrade} />
            <Score label="Security" value={p.security} grade={p.securityGrade} />
          </div>
          <div className="mt-2.5 flex min-h-[19px] flex-wrap gap-1.5">
            {chips.length === 0 ? (
              <span className="rounded-full px-1.5 py-0.5 text-[11px] font-semibold" style={{ color: 'var(--color-ok)', background: 'color-mix(in srgb, var(--color-ok) 16%, transparent)' }}>
                0 open findings
              </span>
            ) : chips.map(c => (
              <span key={c.s} className="rounded-full px-1.5 py-0.5 text-[11px] font-semibold"
                style={{ color: severityColor[c.s], background: `color-mix(in srgb, ${severityColor[c.s]} 16%, transparent)` }}>
                {c.n} {c.s}
              </span>
            ))}
          </div>
        </>
      ) : (
        <div className="mt-4 flex h-[71px] flex-col justify-center gap-2">
          <div className="text-xs text-muted">{p.status === 'Error' ? 'Last run failed — open for the log.' : `${p.status}…`}</div>
          {busy && (
            <div className="h-1 overflow-hidden rounded-full bg-line">
              <div className="h-full w-2/3 animate-pulse rounded-full bg-accent" />
            </div>
          )}
        </div>
      )}

      <div className="mt-2.5 flex items-center justify-between gap-2 border-t border-line pt-2.5">
        <span className="truncate text-[11px] text-muted">
          {ready ? `${p.components} components · ` : ''}updated {timeAgo(p.updatedAt)}
        </span>
        <span className="shrink-0 text-[11px] font-semibold text-muted transition group-hover:text-accent">
          {busy ? 'View progress' : 'Open report'} →
        </span>
      </div>
    </Link>
  )
}
