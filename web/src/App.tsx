import { Link, Route, Routes } from 'react-router-dom';
import { Activity, Bell, FolderTree, GitBranch, Gauge } from 'lucide-react';
import BuildsPage from '@/pages/builds';
import BuildDetailPage from '@/pages/build-detail';
import TargetsPage from '@/pages/targets';
import ProjectsPage from '@/pages/projects';
import AlertsPage from '@/pages/alerts';

const NAV = [
  { to: '/', label: 'Builds', icon: Activity },
  { to: '/targets', label: 'Targets', icon: Gauge },
  { to: '/projects', label: 'Projects', icon: FolderTree },
  { to: '/alerts', label: 'Alerts', icon: Bell },
] as const;

export default function App() {
  return (
    <div className="min-h-screen flex flex-col">
      <header className="border-b">
        <div className="container flex items-center gap-6 py-3">
          <Link to="/" className="flex items-center gap-2 font-semibold">
            <GitBranch className="h-5 w-5" />
            <span>tamp-beacon</span>
          </Link>
          <nav className="flex gap-1">
            {NAV.map(({ to, label, icon: Icon }) => (
              <Link
                key={to}
                to={to}
                className="inline-flex items-center gap-2 rounded-md px-3 py-1.5 text-sm hover:bg-accent"
              >
                <Icon className="h-4 w-4" />
                {label}
              </Link>
            ))}
          </nav>
        </div>
      </header>
      <main className="container flex-1 py-6">
        <Routes>
          <Route path="/" element={<BuildsPage />} />
          <Route path="/builds/:id" element={<BuildDetailPage />} />
          <Route path="/targets" element={<TargetsPage />} />
          <Route path="/projects" element={<ProjectsPage />} />
          <Route path="/alerts" element={<AlertsPage />} />
        </Routes>
      </main>
    </div>
  );
}
