import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { FileCode2, GitBranch, Loader2, Shapes, Trash2 } from 'lucide-react'
import { api } from '../api'
import type { DiagramFormats, Group, ProjectSummary, SampleDiagram, SampleInfo } from '../types'
import { Button, Card, Pill, timeAgo } from '../ui'

export default function Home({ aiAvailable, groups }: { aiAvailable: boolean; groups: Group[] }) {
  const nav = useNavigate()
  const [projects, setProjects] = useState<ProjectSummary[]>([])
  const [samples, setSamples] = useState<SampleInfo[]>([])
  const [sampleImages, setSampleImages] = useState<SampleInfo[]>([])
  const [sampleDiagrams, setSampleDiagrams] = useState<SampleDiagram[]>([])
  const [formats, setFormats] = useState<DiagramFormats | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [repoUrl, setRepoUrl] = useState('')
  const [branch, setBranch] = useState('')
  const [hint, setHint] = useState('')
  const diagramRef = useRef<HTMLInputElement>(null)

  const refresh = () => api.projects().then(setProjects).catch(() => {})
  useEffect(() => {
    refresh()
    api.samples().then(setSamples).catch(() => {})
    api.sampleImages().then(setSampleImages).catch(() => {})
    api.sampleDiagrams().then(setSampleDiagrams).catch(() => {})
    api.diagramFormats().then(setFormats).catch(() => {})
  }, [])

  // Keep the list live while an org scan (or any scan) is still working through its queue.
  useEffect(() => {
    if (!projects.some(p => p.status !== 'Ready' && p.status !== 'Error')) return
    const t = setInterval(refresh, 3000)
    return () => clearInterval(t)
  }, [projects])

  async function run(label: string, fn: () => Promise<{ id: string }>) {
    setBusy(label); setErr(null); setNotice(null)
    try {
      const { id } = await fn()
      nav(`/p/${id}?from=projects`)
    } catch (e) {
      setErr((e as Error).message)
    } finally {
      setBusy(null)
    }
  }

  async function runRepo() {
    setBusy('repo'); setErr(null); setNotice(null)
    try {
      const res = await api.fromRepo(repoUrl.trim(), branch.trim() || undefined)
      if ('ids' in res) {
        // Org/user root: many projects were queued at once, not one to jump into.
        setNotice(`Scanning ${res.count} repositories under ${res.owner}. They'll appear below as each finishes.`)
        setRepoUrl('')
        refresh()
      } else {
        nav(`/p/${res.id}?from=projects`)
      }
    } catch (e) {
      setErr((e as Error).message)
    } finally {
      setBusy(null)
    }
  }

  return (
    <div className="sf-scroll h-full overflow-auto">
      <div className="mx-auto max-w-6xl px-5 py-8">
        <div className="mb-8">
          <h1 className="text-2xl font-semibold tracking-tight">Model the failure before it happens.</h1>
          <p className="mt-1 max-w-3xl text-sm text-muted">
            Feed SecureFlow an architecture diagram from any tool, a whiteboard photo, or a git repository. It rebuilds the architecture as a model,
            runs 26 resilience and STRIDE rules with evidence, simulates blast radius, and proposes fixes you can apply and re-score live.
          </p>
        </div>

        {err && <div className="mb-4 rounded-lg border border-critical/40 bg-critical/10 px-3 py-2 text-sm text-critical">{err}</div>}
        {notice && <div className="mb-4 rounded-lg border border-accent/40 bg-accent/10 px-3 py-2 text-sm text-accent">{notice}</div>}

        <div className="grid gap-4 md:grid-cols-3">
          <Card className="p-4">
            <div className="mb-2 flex items-center gap-2 font-medium"><Shapes size={18} className="text-accent" /> Diagram from any tool</div>
            <p className="mb-2 text-xs text-muted">
              draw.io, Visio (and Lucidchart via .vsdx), Mermaid, PlantUML / C4, Structurizr, Excalidraw and Gliffy files are parsed exactly,
              with no AI. Images, PDFs and SVG exports from Figma, Miro or any other tool, and whiteboard photos, are read by the vision model.
            </p>
            {formats && (
              <div className="mb-3 flex flex-wrap gap-1">
                {formats.formats.map(f => (
                  <span key={f.format} title={`${f.tools} — ${f.extensions.join(', ')}`}
                    className={`rounded-full border px-2 py-0.5 text-[11px] ${f.needsAi ? 'border-line text-muted' : 'border-accent/40 text-accent'}`}>
                    {f.name}{f.needsAi ? ' · AI' : ''}
                  </span>
                ))}
              </div>
            )}
            <input value={hint} onChange={e => setHint(e.target.value)} placeholder="Optional context, e.g. “Azure, orders service is .NET”"
              className="mb-3 w-full rounded-lg border border-line bg-panel-2 px-3 py-1.5 text-sm outline-none focus:border-accent" />
            <input ref={diagramRef} type="file" className="hidden"
              accept={formats ? [...new Set(formats.formats.flatMap(f => f.extensions))].join(',') : undefined}
              onChange={e => { const f = e.target.files?.[0]; if (f) run('diagram', () => api.fromDiagram(f, hint)); e.target.value = '' }} />
            <Button onClick={() => diagramRef.current?.click()} disabled={busy !== null}>
              {busy === 'diagram' ? <Loader2 size={14} className="animate-spin" /> : <Shapes size={14} />} Upload diagram
            </Button>
            {!aiAvailable && <div className="mt-2 text-xs text-medium">No AI key on the server: structured files work, images / PDF / SVG exports need ANTHROPIC_API_KEY (or OpenAI / Foundry).</div>}
            {sampleImages.length > 0 && (
              <div className="mt-4 border-t border-line pt-3">
                <div className="mb-1.5 text-xs uppercase tracking-wide text-muted">Or try a sample image</div>
                <div className="flex flex-wrap gap-2">
                  {sampleImages.map(s => (
                    <Button key={s.id} variant="ghost" title={s.description} disabled={!aiAvailable || busy !== null}
                      onClick={() => run(s.id, () => api.fromSampleImage(s.id, hint))}>
                      {busy === s.id && <Loader2 size={14} className="animate-spin" />}{s.name}
                    </Button>
                  ))}
                </div>
              </div>
            )}
          </Card>

          <Card className="p-4">
            <div className="mb-2 flex items-center gap-2 font-medium"><GitBranch size={18} className="text-accent" /> Git repository</div>
            <p className="mb-3 text-xs text-muted">
              Shallow-clones, digests deployment and config files, and reconstructs the architecture with file:line evidence.
              Point it at a GitHub org or user instead of one repo (e.g. <code className="text-ink">github.com/acme</code>) to scan every
              repository under it — up to 20, most-recently-active first, skipping forks and archives.
            </p>
            <input value={repoUrl} onChange={e => setRepoUrl(e.target.value)} placeholder="https://github.com/org/repo.git, an org/user URL, or a local path"
              className="mb-2 w-full rounded-lg border border-line bg-panel-2 px-3 py-1.5 text-sm outline-none focus:border-accent" />
            <input value={branch} onChange={e => setBranch(e.target.value)} placeholder="branch (optional, single repo only)"
              className="mb-3 w-full rounded-lg border border-line bg-panel-2 px-3 py-1.5 text-sm outline-none focus:border-accent" />
            <Button onClick={runRepo} disabled={!aiAvailable || busy !== null || !repoUrl.trim()}>
              {busy === 'repo' ? <Loader2 size={14} className="animate-spin" /> : <GitBranch size={14} />} Scan repository
            </Button>
          </Card>

          <Card className="p-4">
            <div className="mb-2 flex items-center gap-2 font-medium"><FileCode2 size={18} className="text-accent" /> Sample diagrams</div>
            {sampleDiagrams.length > 0 && (
              <>
                <p className="mb-2 text-xs text-muted">The same NorthBank Payments system, drawn in each tool. Every one gives the same analysis.</p>
                <div className="mb-3 flex flex-wrap gap-2">
                  {sampleDiagrams.map(s => (
                    <Button key={s.id} variant="ghost" title={s.description} disabled={busy !== null || (s.needsAi && !aiAvailable)}
                      onClick={() => run(s.id, () => api.fromSampleDiagram(s.id, hint))}>
                      {busy === s.id && <Loader2 size={14} className="animate-spin" />}{s.name}
                    </Button>
                  ))}
                </div>
              </>
            )}
            <div className="border-t border-line pt-3">
              <div className="mb-1.5 text-xs uppercase tracking-wide text-muted">Built-in models</div>
              <div className="flex flex-wrap gap-2">
                {samples.map(s => (
                  <Button key={s.id} variant="ghost" onClick={() => run(s.id, () => api.fromSample(s.id))} disabled={busy !== null} title={s.description}>
                    {busy === s.id && <Loader2 size={14} className="animate-spin" />}{s.name}
                  </Button>
                ))}
              </div>
            </div>
          </Card>
        </div>

        <div className="mt-10">
          <div className="mb-3 flex items-center justify-between">
            <h2 className="text-sm font-semibold uppercase tracking-wide text-muted">Recent models</h2>
          </div>
          {projects.length === 0 ? (
            <div className="rounded-xl border border-dashed border-line p-8 text-center text-sm text-muted">No models yet. Start with a sample.</div>
          ) : (
            <div className="overflow-hidden rounded-xl border border-line">
              <table className="w-full text-sm">
                <thead className="bg-panel text-left text-xs uppercase tracking-wide text-muted">
                  <tr><th className="px-4 py-2">Name</th><th className="px-4 py-2">Source</th><th className="px-4 py-2">Group</th><th className="px-4 py-2">Status</th><th className="px-4 py-2 text-right">Resilience</th><th className="px-4 py-2 text-right">Security</th><th className="px-4 py-2 text-right">Open</th><th className="px-4 py-2">Created</th><th /></tr>
                </thead>
                <tbody>
                  {projects.map(p => (
                    <tr key={p.id} className="cursor-pointer border-t border-line hover:bg-panel" onClick={() => nav(`/p/${p.id}?from=projects`)}>
                      <td className="px-4 py-2 font-medium">{p.name}</td>
                      <td className="px-4 py-2 text-muted">{p.source}</td>
                      <td className="px-4 py-2" onClick={e => e.stopPropagation()}>
                        <select value={p.groupId ?? ''} title="Assign this project to a team group"
                          onChange={e => api.setProjectGroup(p.id, e.target.value || null).then(refresh)}
                          className="rounded-md border border-line bg-panel-2 px-1.5 py-0.5 text-xs text-ink outline-none focus:border-accent">
                          <option value="">Ungrouped</option>
                          {groups.map(g => <option key={g.id} value={g.id}>{g.name}</option>)}
                        </select>
                      </td>
                      <td className="px-4 py-2"><Pill tone={p.status === 'Ready' ? 'ok' : p.status === 'Error' ? 'danger' : 'accent'}>{p.status}</Pill></td>
                      <td className="px-4 py-2 text-right font-mono">{p.status === 'Ready' ? p.resilience : '–'}</td>
                      <td className="px-4 py-2 text-right font-mono">{p.status === 'Ready' ? p.security : '–'}</td>
                      <td className="px-4 py-2 text-right font-mono">{p.openFindings}</td>
                      <td className="px-4 py-2 text-muted">{timeAgo(p.createdAt)}</td>
                      <td className="px-2 py-2 text-right">
                        <button className="rounded p-1 text-muted hover:bg-line hover:text-critical" title="Delete"
                          onClick={e => { e.stopPropagation(); if (confirm(`Delete "${p.name}"?`)) api.deleteProject(p.id).then(refresh) }}>
                          <Trash2 size={14} />
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
