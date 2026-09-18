import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { AlertTriangle, Download, FolderKanban, Gauge, ListChecks, Loader2, Plus, ShieldAlert } from 'lucide-react'
import { api } from '../api'
import { useRole } from '../role'
import type { Group, ProjectSummary } from '../types'
import { Card } from '../ui'
import ProjectCard, { scoreColor } from '../components/ProjectCard'

type Sort = 'portfolio' | 'risk' | 'score' | 'name' | 'updated'

const riskWeight = (p: ProjectSummary) => {
  const s = p.openBySeverity ?? {}
  return (s.Critical ?? 0) * 1000 + (s.High ?? 0) * 100 + (s.Medium ?? 0) * 10 + (s.Low ?? 0)
}

function Stat({ icon, label, value, color, alert }: {
  icon: React.ReactNode; label: string; value: string | number; color?: string; alert?: boolean
}) {
  return (
    <Card className={`flex items-center gap-3 p-3.5 ${alert ? 'border-critical/45 bg-critical/8' : ''}`}>
      <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg"
        style={{ background: `color-mix(in srgb, ${color ?? 'var(--color-accent)'} 15%, transparent)`, color: color ?? 'var(--color-accent)' }}>
        {icon}
      </span>
      <div className="min-w-0">
        <div className="truncate text-[10px] uppercase tracking-wide text-muted">{label}</div>
        <div className="font-mono text-[22px] font-bold leading-tight" style={color ? { color } : undefined}>{value}</div>
      </div>
    </Card>
  )
}

export default function Dashboard() {
  const { managedGroupIds } = useRole()
  const [groups, setGroups] = useState<Group[]>([])
  const [projects, setProjects] = useState<ProjectSummary[] | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [activeGroup, setActiveGroup] = useState<string | null>(null)
  const [sort, setSort] = useState<Sort>('portfolio')
  const [seeding, setSeeding] = useState(false)

  // No groups picked = the whole portfolio. Managers narrow it from the header switcher.
  const scope = managedGroupIds.length ? managedGroupIds : undefined
  const scopeKey = scope?.join(',') ?? ''

  const load = useCallback(() =>
    api.projects(scope).then(setProjects).catch(e => setErr((e as Error).message)),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [scopeKey])

  useEffect(() => { api.groups().then(setGroups).catch(() => {}) }, [])
  useEffect(() => { setProjects(null); setErr(null); load() }, [load])

  const all = useMemo(() => projects ?? [], [projects])
  const pending = all.some(p => p.status !== 'Ready' && p.status !== 'Error')

  // While anything is still extracting or analysing, keep the cards fresh.
  useEffect(() => {
    if (!pending && !seeding) return
    const t = setInterval(() => { load(); api.groups().then(setGroups).catch(() => {}) }, 3000)
    return () => clearInterval(t)
  }, [pending, seeding, load])

  useEffect(() => { if (seeding && pending) setSeeding(false) }, [seeding, pending])

  const scopeGroups = managedGroupIds.length ? groups.filter(g => managedGroupIds.includes(g.id)) : groups

  const shown = useMemo(() => {
    const list = activeGroup ? all.filter(p => p.groupId === activeGroup) : [...all]
    return list.sort((a, b) => {
      if (sort === 'portfolio') return +new Date(a.createdAt) - +new Date(b.createdAt)
      if (sort === 'name') return a.name.localeCompare(b.name)
      if (sort === 'updated') return +new Date(b.updatedAt) - +new Date(a.updatedAt)
      if (sort === 'score') return (b.resilience + b.security) - (a.resilience + a.security)
      const d = riskWeight(b) - riskWeight(a)
      return d !== 0 ? d : (a.resilience + a.security) - (b.resilience + b.security)
    })
  }, [all, activeGroup, sort])

  const ready = all.filter(p => p.status === 'Ready')
  const avg = (xs: number[]) => xs.length ? Math.round(xs.reduce((a, b) => a + b, 0) / xs.length) : 0
  const avgResilience = avg(ready.map(p => p.resilience))
  const avgSecurity = avg(ready.map(p => p.security))
  const totalOpen = all.reduce((a, p) => a + p.openFindings, 0)
  const criticals = all.reduce((a, p) => a + (p.openBySeverity?.Critical ?? 0), 0)
  const criticalProjects = all.filter(p => (p.openBySeverity?.Critical ?? 0) > 0).length

  const seed = async () => {
    setSeeding(true)
    setErr(null)
    try { await api.seedDemo(); await load() } catch (e) { setErr((e as Error).message); setSeeding(false) }
  }

  const exportSummary = () => {
    const lines = [
      `# SecureFlow portfolio summary`,
      ``,
      `_${scopeGroups.map(g => g.name).join(', ') || 'All groups'} · generated ${new Date().toISOString().slice(0, 16).replace('T', ' ')}_`,
      ``,
      `${all.length} projects · avg resilience ${avgResilience} · avg security ${avgSecurity} · ${totalOpen} open findings · ${criticals} critical`,
      ``,
      `| Project | Group | Status | Resilience | Security | Critical | High | Open |`,
      `|---|---|---|---|---|---|---|---|`,
      ...shown.map(p => `| ${p.name} | ${p.groupName ?? '–'} | ${p.status} | ${p.resilience} (${p.resilienceGrade}) | ${p.security} (${p.securityGrade}) | ${p.openBySeverity?.Critical ?? 0} | ${p.openBySeverity?.High ?? 0} | ${p.openFindings} |`),
    ]
    const url = URL.createObjectURL(new Blob([lines.join('\n')], { type: 'text/markdown' }))
    const a = document.createElement('a')
    a.href = url
    a.download = 'secureflow-portfolio.md'
    a.click()
    URL.revokeObjectURL(url)
  }

  return (
    <div className="sf-scroll h-full overflow-auto">
      <div className="mx-auto max-w-[1360px] px-10 py-7">

        <div className="flex flex-wrap items-end justify-between gap-5">
          <div>
            <h1 className="text-2xl font-semibold tracking-tight">Group Manager Dashboard</h1>
            <p className="mt-1 text-[13px] text-muted">
              {all.length} project{all.length === 1 ? '' : 's'} across {scopeGroups.length ? scopeGroups.map(g => g.name).join(', ') : 'all groups'}
              {pending && <> · <span className="text-accent">{all.filter(p => p.status !== 'Ready' && p.status !== 'Error').length} still analysing</span></>}
            </p>
          </div>
          <div className="flex items-center gap-2">
            <Link to="/projects" className="inline-flex items-center gap-2 rounded-lg border border-line bg-panel-2 px-3 py-1.5 text-[13px] font-medium hover:bg-line">
              <Plus size={14} /> New project
            </Link>
            {all.length > 0 && (
              <button onClick={exportSummary}
                className="inline-flex items-center gap-2 rounded-lg border border-line bg-panel-2 px-3 py-1.5 text-[13px] font-medium hover:bg-line">
                <Download size={14} /> Export portfolio summary
              </button>
            )}
          </div>
        </div>

        {err && <div className="mt-4 rounded-lg border border-critical/40 bg-critical/10 px-3 py-2 text-sm text-critical">{err}</div>}

        {projects === null ? (
          <div className="mt-6 flex items-center gap-2 text-sm text-muted"><Loader2 size={16} className="animate-spin" /> Loading portfolio…</div>
        ) : all.length === 0 ? (
          <div className="mt-6 rounded-xl border border-dashed border-line p-10 text-center">
            <div className="text-sm text-muted">No projects yet. Load the demo portfolio to scan a dozen public repositories, or add your own.</div>
            <button onClick={seed} disabled={seeding}
              className="mx-auto mt-4 inline-flex items-center gap-2 rounded-lg bg-accent px-3.5 py-2 text-sm font-medium text-bg hover:brightness-110 disabled:opacity-50">
              {seeding ? <Loader2 size={14} className="animate-spin" /> : <Download size={14} />} Load demo portfolio
            </button>
          </div>
        ) : (
          <>
            <div className="mt-5 grid gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-5">
              <Stat icon={<FolderKanban size={18} />} label="Projects" value={all.length} />
              <Stat icon={<Gauge size={18} />} label="Avg. resilience" value={ready.length ? avgResilience : '–'} color={ready.length ? scoreColor(avgResilience) : undefined} />
              <Stat icon={<ShieldAlert size={18} />} label="Avg. security" value={ready.length ? avgSecurity : '–'} color={ready.length ? scoreColor(avgSecurity) : undefined} />
              <Stat icon={<ListChecks size={18} />} label="Open findings" value={totalOpen} />
              <Stat icon={<AlertTriangle size={18} />} label={`Critical · ${criticalProjects} project${criticalProjects === 1 ? '' : 's'}`}
                value={criticals} color="var(--color-critical)" alert={criticals > 0} />
            </div>

            <div className="mt-5 flex flex-wrap items-center justify-between gap-4">
              <div className="flex flex-wrap items-center gap-2">
                <button onClick={() => setActiveGroup(null)}
                  className={`rounded-full border px-3 py-1 text-xs transition ${activeGroup === null ? 'border-accent bg-accent/15 font-semibold text-accent' : 'border-line bg-panel-2 text-muted hover:text-ink'}`}>
                  All groups · {all.length}
                </button>
                {scopeGroups.map(g => {
                  const n = all.filter(p => p.groupId === g.id).length
                  if (n === 0) return null
                  return (
                    <button key={g.id} onClick={() => setActiveGroup(g.id)}
                      className={`rounded-full border px-3 py-1 text-xs transition ${activeGroup === g.id ? 'border-accent bg-accent/15 font-semibold text-accent' : 'border-line bg-panel-2 text-muted hover:text-ink'}`}>
                      {g.name} · {n}
                    </button>
                  )
                })}
              </div>
              <div className="flex items-center gap-2">
                <label htmlFor="sort" className="text-xs text-muted">Sort</label>
                <select id="sort" value={sort} onChange={e => setSort(e.target.value as Sort)}
                  className="rounded-lg border border-line bg-panel-2 px-2.5 py-1.5 text-xs text-ink outline-none focus:border-accent">
                  <option value="portfolio">Portfolio order</option>
                  <option value="risk">Risk · worst first</option>
                  <option value="score">Score · highest first</option>
                  <option value="updated">Recently updated</option>
                  <option value="name">Name</option>
                </select>
              </div>
            </div>

            {shown.length === 0 ? (
              <div className="mt-5 rounded-xl border border-dashed border-line p-10 text-center text-sm text-muted">
                No projects in this group yet.
              </div>
            ) : (
              <div className="mt-5 grid gap-5 sm:grid-cols-2 xl:grid-cols-4">
                {shown.map(p => <ProjectCard key={p.id} p={p} />)}
              </div>
            )}
          </>
        )}
      </div>
    </div>
  )
}
