import { useState, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { ChevronRight, ChevronDown, Building2, FolderGit2, Layers } from 'lucide-react';
import { api, type ProjectFacet } from '@/lib/api';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { formatUnixNs } from '@/lib/utils';

export default function OrganizationsPage() {
  const orgs = useQuery({
    queryKey: ['organizations'],
    queryFn: api.getOrganizations,
    refetchInterval: 15000,
  });

  const projects = useQuery({
    queryKey: ['projects'],
    queryFn: api.getProjects,
    refetchInterval: 15000,
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Organizations</CardTitle>
      </CardHeader>
      <CardContent className="p-0">
        <div className="divide-y divide-border">
          {orgs.data?.organizations.map((org) => (
            <OrganizationRow
              key={org.name}
              name={org.name}
              projectsCount={org.projects_count}
              buildsCount={org.builds_count}
              failedCount={org.failed_count}
              lastSeenUnixNs={org.last_seen_unix_ns}
              projects={projects.data?.projects.filter((p) => p.organization === org.name) ?? []}
            />
          ))}
          {orgs.data?.organizations.length === 0 && (
            <div className="text-center text-muted-foreground py-12">
              No organizations yet. Send telemetry with <code className="font-mono">OTEL_BUILD_ORGANIZATION</code> set on the build process.
            </div>
          )}
        </div>
      </CardContent>
    </Card>
  );
}

interface OrganizationRowProps {
  name: string;
  projectsCount: number;
  buildsCount: number;
  failedCount: number;
  lastSeenUnixNs: number;
  projects: ProjectFacet[];
}

function OrganizationRow({ name, projectsCount, buildsCount, failedCount, lastSeenUnixNs, projects }: OrganizationRowProps) {
  const [open, setOpen] = useState(true);
  const grouped = groupByProjectName(projects);

  return (
    <div>
      <TreeRow
        depth={0}
        open={open}
        onToggle={() => setOpen((v) => !v)}
        icon={<Building2 className="h-4 w-4" />}
        primary={<span className="font-semibold">{name}</span>}
        meta={
          <Meta
            projects={projectsCount}
            builds={buildsCount}
            failed={failedCount}
            lastSeen={lastSeenUnixNs}
            buildsLink={`/builds?organization=${encodeURIComponent(name)}`}
          />
        }
      />
      {open &&
        grouped.map((g) => (
          <ProjectGroupRow
            key={`${name}::${g.name}`}
            organization={name}
            name={g.name}
            facets={g.facets}
          />
        ))}
    </div>
  );
}

interface ProjectGroup {
  name: string;
  facets: ProjectFacet[];
}

function groupByProjectName(facets: ProjectFacet[]): ProjectGroup[] {
  const map = new Map<string, ProjectFacet[]>();
  for (const f of facets) {
    const list = map.get(f.name) ?? [];
    list.push(f);
    map.set(f.name, list);
  }
  return Array.from(map.entries())
    .map(([name, facets]) => ({ name, facets }))
    .sort((a, b) => {
      const aMax = Math.max(...a.facets.map((f) => f.last_seen_unix_ns));
      const bMax = Math.max(...b.facets.map((f) => f.last_seen_unix_ns));
      return bMax - aMax;
    });
}

function ProjectGroupRow({ organization, name, facets }: { organization: string; name: string; facets: ProjectFacet[] }) {
  const [open, setOpen] = useState(false);
  const totalBuilds = facets.reduce((sum, f) => sum + Number(f.builds_count), 0);
  const totalFailed = facets.reduce((sum, f) => sum + Number(f.failed_count), 0);
  const lastSeen = Math.max(...facets.map((f) => f.last_seen_unix_ns));
  const hasAreas = facets.length > 1 || facets[0]?.area != null;

  return (
    <div>
      <TreeRow
        depth={1}
        open={hasAreas ? open : undefined}
        onToggle={hasAreas ? () => setOpen((v) => !v) : undefined}
        icon={<FolderGit2 className="h-4 w-4" />}
        primary={<span>{name}</span>}
        meta={
          <Meta
            builds={totalBuilds}
            failed={totalFailed}
            lastSeen={lastSeen}
            buildsLink={`/builds?organization=${encodeURIComponent(organization)}&project=${encodeURIComponent(name)}`}
          />
        }
      />
      {hasAreas &&
        open &&
        facets.map((f) => (
          <TreeRow
            key={`${organization}::${name}::${f.area ?? '_root'}`}
            depth={2}
            icon={<Layers className="h-4 w-4" />}
            primary={<span className="text-muted-foreground">{f.area ?? '—'}</span>}
            meta={
              <Meta
                builds={Number(f.builds_count)}
                failed={Number(f.failed_count)}
                lastSeen={f.last_seen_unix_ns}
                buildsLink={`/builds?organization=${encodeURIComponent(organization)}&project=${encodeURIComponent(name)}${
                  f.area ? `&area=${encodeURIComponent(f.area)}` : ''
                }`}
              />
            }
          />
        ))}
    </div>
  );
}

interface TreeRowProps {
  depth: number;
  open?: boolean;
  onToggle?: () => void;
  icon: ReactNode;
  primary: ReactNode;
  meta: ReactNode;
}

function TreeRow({ depth, open, onToggle, icon, primary, meta }: TreeRowProps) {
  const togglable = onToggle != null;
  return (
    <div
      className="flex items-center gap-3 px-4 py-2 hover:bg-accent/40"
      style={{ paddingLeft: `${16 + depth * 20}px` }}
    >
      <button
        type="button"
        className="inline-flex h-5 w-5 items-center justify-center text-muted-foreground disabled:opacity-30"
        onClick={onToggle}
        disabled={!togglable}
        aria-label={togglable ? (open ? 'collapse' : 'expand') : undefined}
      >
        {togglable ? open ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" /> : null}
      </button>
      <span className="text-muted-foreground">{icon}</span>
      <span className="flex-1 truncate">{primary}</span>
      <span className="flex items-center gap-3 text-xs text-muted-foreground">{meta}</span>
    </div>
  );
}

interface MetaProps {
  projects?: number;
  builds: number;
  failed: number;
  lastSeen: number;
  buildsLink: string;
}

function Meta({ projects, builds, failed, lastSeen, buildsLink }: MetaProps) {
  return (
    <>
      {projects != null && <span>{projects} projects</span>}
      <Link to={buildsLink} className="hover:underline">
        {builds} builds
      </Link>
      {failed > 0 ? (
        <Badge variant="destructive">{failed} failed</Badge>
      ) : (
        <Badge variant="success">clean</Badge>
      )}
      <span className="font-mono">{formatUnixNs(lastSeen)}</span>
    </>
  );
}
