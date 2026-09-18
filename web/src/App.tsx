import { useCallback, useEffect, useState } from 'react'
import { Link, Route, Routes, useLocation } from 'react-router-dom'
import { LayoutDashboard, ShieldCheck, Sparkles, WifiOff } from 'lucide-react'
import { api } from './api'
import type { Group, Health } from './types'
import { Pill } from './ui'
import RoleSwitcher from './components/RoleSwitcher'
import Home from './pages/Home'
import ProjectPage from './pages/ProjectPage'
import Dashboard from './pages/Dashboard'

export default function App() {
  const [health, setHealth] = useState<Health | null>(null)
  const [groups, setGroups] = useState<Group[]>([])
  const { pathname } = useLocation()

  const loadGroups = useCallback(() => { api.groups().then(setGroups).catch(() => {}) }, [])
  useEffect(() => { api.health().then(setHealth).catch(() => setHealth(null)) }, [])
  useEffect(() => { loadGroups() }, [loadGroups])

  return (
    <div className="flex h-full flex-col">
      <header className="no-print flex items-center justify-between border-b border-line bg-panel px-5 py-2.5">
        <div className="flex items-center gap-6">
          <Link to="/" className="flex items-center gap-2.5">
            <span className="grid h-8 w-8 place-items-center rounded-lg bg-accent/15 text-accent"><ShieldCheck size={20} /></span>
            <span className="text-base font-semibold tracking-tight">SecureFlow</span>
            <span className="hidden text-xs text-muted sm:inline">AI resilience &amp; threat modeling</span>
          </Link>
          <nav className="flex items-center gap-4">
            <Link to="/" className={`flex items-center gap-1.5 text-sm font-medium ${pathname === '/' ? 'text-accent' : 'text-muted hover:text-ink'}`}>
              <LayoutDashboard size={15} /> Dashboard
            </Link>
            <Link to="/projects" className={`text-sm font-medium ${pathname === '/projects' ? 'text-accent' : 'text-muted hover:text-ink'}`}>Projects</Link>
          </nav>
        </div>
        <div className="flex items-center gap-2">
          <RoleSwitcher groups={groups} />
          {health === null ? (
            <Pill tone="danger"><WifiOff size={12} /> API offline</Pill>
          ) : health.ai.available ? (
            <Pill tone="accent" title={health.offline ? 'Serving AI responses from the replay cache only' : undefined}>
              <Sparkles size={12} /> {health.ai.provider}{health.offline ? ' · replay' : ''}
            </Pill>
          ) : (
            <Pill tone="warn" title={health.ai.error ?? ''}>Rules only · no AI key</Pill>
          )}
        </div>
      </header>
      <main className="min-h-0 flex-1">
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/projects" element={<Home aiAvailable={health?.ai.available ?? false} groups={groups} />} />
          <Route path="/p/:id" element={<ProjectPage aiAvailable={health?.ai.available ?? false} groups={groups} />} />
        </Routes>
      </main>
    </div>
  )
}
