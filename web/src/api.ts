import type { BlastRadiusResult, FixProposal, Group, Health, LogEntry, Project, ProjectStatus, ProjectSummary, RuleInfo, SampleInfo } from './types'

async function handle<T>(res: Response): Promise<T> {
  if (res.ok) {
    if (res.status === 204) return undefined as T
    return (await res.json()) as T
  }
  let detail = res.statusText
  try {
    const body = await res.json()
    detail = body.detail ?? body.error ?? body.title ?? JSON.stringify(body)
  } catch { /* not json */ }
  throw new Error(`${res.status}: ${detail}`)
}

const get = <T,>(path: string) => fetch(path).then(r => handle<T>(r))
const post = <T,>(path: string, body?: unknown) =>
  fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) }).then(r => handle<T>(r))
const upload = <T,>(path: string, form: FormData) => fetch(path, { method: 'POST', body: form }).then(r => handle<T>(r))

export const api = {
  health: () => get<Health>('/api/health'),
  rules: () => get<RuleInfo[]>('/api/rules'),
  samples: () => get<SampleInfo[]>('/api/samples'),
  sampleImages: () => get<SampleInfo[]>('/api/sample-images'),
  fromSampleImage: (sample: string, hint?: string) =>
    post<{ id: string }>('/api/projects/from-sample-image', { sample, hint: hint || null }),
  projects: (groupIds?: string[]) =>
    get<ProjectSummary[]>(`/api/projects${groupIds?.length ? `?groupIds=${groupIds.map(encodeURIComponent).join(',')}` : ''}`),
  project: (id: string) => get<Project>(`/api/projects/${id}`),
  deleteProject: (id: string) => fetch(`/api/projects/${id}`, { method: 'DELETE' }).then(r => handle<void>(r)),
  seedDemo: () => post<{ created: number; ids: string[]; running: boolean }>('/api/demo/seed'),
  groups: () => get<Group[]>('/api/groups'),
  createGroup: (name: string) => post<Group>('/api/groups', { name }),
  deleteGroup: (id: string) => fetch(`/api/groups/${id}`, { method: 'DELETE' }).then(r => handle<void>(r)),
  setProjectGroup: (id: string, groupId: string | null) => post<ProjectSummary>(`/api/projects/${id}/group`, { groupId }),
  fromSample: (sample: string) => post<{ id: string }>('/api/projects/from-sample', { sample }),
  // A root org/user URL (no repo segment) fans out into multiple projects instead of one.
  fromRepo: (url: string, branch?: string) =>
    post<{ id: string } | { ids: string[]; count: number; owner: string }>('/api/projects/from-repo', { url, branch: branch || null }),
  fromImage: (file: File, hint?: string) => {
    const form = new FormData()
    form.append('file', file)
    if (hint) form.append('hint', hint)
    return upload<{ id: string }>('/api/projects/from-image', form)
  },
  fromDrawio: (file: File) => {
    const form = new FormData()
    form.append('file', file)
    return upload<{ id: string }>('/api/projects/from-drawio', form)
  },
  reanalyze: (id: string) => post<Project>(`/api/projects/${id}/analyze`),
  scanVulnerabilities: (id: string) => post<Project>(`/api/projects/${id}/scan-vulnerabilities`),
  simulate: (id: string, componentId: string) => get<BlastRadiusResult>(`/api/projects/${id}/simulate/${encodeURIComponent(componentId)}`),
  proposeFix: (id: string, findingId: string) => post<FixProposal>(`/api/projects/${id}/findings/${findingId}/fix`),
  applyFix: (id: string, findingId: string) => post<Project>(`/api/projects/${id}/apply-fix`, { findingId }),
  setStatus: (id: string, findingId: string, status: string) => post<Project>(`/api/projects/${id}/findings/${findingId}/status`, { status }),
  report: (id: string) => fetch(`/api/projects/${id}/report`).then(r => r.text()),
  reportUrl: (id: string) => `/api/projects/${id}/report`,
  modelUrl: (id: string) => `/api/projects/${id}/model.json`,
}

/** Follow the pipeline log over SSE. Returns a dispose function. */
export function subscribe(id: string, onLog: (e: LogEntry) => void, onStatus: (s: ProjectStatus, error?: string | null) => void): () => void {
  const es = new EventSource(`/api/projects/${id}/events`)
  es.addEventListener('log', ev => onLog(JSON.parse((ev as MessageEvent).data)))
  es.addEventListener('status', ev => {
    const d = JSON.parse((ev as MessageEvent).data)
    onStatus(d.status, d.error)
  })
  es.onerror = () => { /* server closes the stream when done; ignore */ }
  return () => es.close()
}
