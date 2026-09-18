import { useEffect, useState } from 'react'
import { Link, Route, Routes, useLocation } from 'react-router-dom'
import { LayoutDashboard, ShieldCheck } from 'lucide-react'
import { api } from './api'
import type { Group, Health } from './types'
import Home from './pages/Home'
import ProjectPage from './pages/ProjectPage'
import Dashboard from './pages/Dashboard'

export default function App() {
  // health is not shown in the header, but ai.available gates the ingestion and fix buttons.
  const [health, setHealth] = useState<Health | null>(null)
  const [groups, setGroups] = useState<Group[]>([])
  const { pathname } = useLocation()

  useEffect(() => { api.health().then(setHealth).catch(() => setHealth(null)) }, [])
  useEffect(() => { api.groups().then(setGroups).catch(() => {}) }, [])

  return (
    <div className="flex h-full flex-col">
      <header className="no-print flex items-center gap-6 border-b border-line bg-panel px-5 py-2.5">
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
