import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Settings } from 'lucide-react';
import { api, type BuildConfigSummary } from '@/lib/api';
import { formatCpuMs, formatNs, formatUnix } from '@/lib/format';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';

export default function ProjectDetailPage() {
  const { slug = '' } = useParams<{ slug: string }>();
  const project = useQuery({
    queryKey: ['project', slug],
    queryFn: () => api.getProject(slug),
    enabled: !!slug,
  });
  const configs = useQuery({
    queryKey: ['configs', slug],
    queryFn: () => api.listConfigs(slug),
    enabled: !!slug,
    refetchInterval: 10_000,
  });

  if (!slug) return null;

  return (
    <div className="space-y-6">
      <div className="flex items-start justify-between">
        <div>
          <Link to="/projects" className="text-sm text-muted-foreground hover:text-foreground">
            ← Projects
          </Link>
          <h1 className="text-2xl font-semibold mt-1">
            {project.data?.name ?? slug}
            {project.data?.my_role && (
              <Badge className="ml-3" variant={project.data.my_role === 'admin' ? 'default' : 'secondary'}>
                {project.data.my_role}
              </Badge>
            )}
          </h1>
          <p className="font-mono text-xs text-muted-foreground mt-1">{slug}</p>
          {project.data?.description && (
            <p className="text-sm text-muted-foreground mt-2">{project.data.description}</p>
          )}
        </div>
        <Link to={`/projects/${slug}/settings`}>
          <Button variant="outline" size="sm">
            <Settings className="h-4 w-4 mr-1" />
            Settings
          </Button>
        </Link>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Build configs</CardTitle>
        </CardHeader>
        <CardContent>
          {configs.isLoading && <p className="text-muted-foreground text-sm">Loading…</p>}
          {configs.data && configs.data.configs.length === 0 && (
            <div className="text-sm text-muted-foreground space-y-2">
              <p>No builds yet under this project.</p>
              <p>
                Mint an ingest token under <span className="font-mono">Settings</span> and emit a
                Tamp build with the env vars below; the BuildConfig auto-creates on first ingest.
              </p>
              <pre className="font-mono bg-muted/50 p-3 rounded text-xs">
                {`export OTEL_EXPORTER_OTLP_ENDPOINT=https://<your-beacon>
export OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer tbk_..."
# optional — defaults to "default" if absent
export TAMP_BUILD_CONFIG_NAME=main-ci`}
              </pre>
            </div>
          )}
          {configs.data && configs.data.configs.length > 0 && (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Config</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead>Last build</TableHead>
                  <TableHead>Total</TableHead>
                  <TableHead>CPU time</TableHead>
                  <TableHead className="w-24"></TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {configs.data.configs.map((c: BuildConfigSummary) => (
                  <TableRow key={c.slug} className="hover:bg-muted/30">
                    <TableCell>
                      <Link to={`/projects/${slug}/configs/${c.slug}`} className="block">
                        <div className="font-medium">{c.name}</div>
                        <div className="font-mono text-xs text-muted-foreground">{c.slug}</div>
                      </Link>
                    </TableCell>
                    <TableCell>
                      {c.last_build ? (
                        <Badge variant={c.last_build.outcome === 'success' ? 'default' : 'destructive'}>
                          {c.last_build.outcome === 'success' ? '🟢' : '🔴'} {c.last_build.outcome}
                        </Badge>
                      ) : (
                        <span className="text-muted-foreground text-sm">no builds yet</span>
                      )}
                    </TableCell>
                    <TableCell>
                      {c.last_build ? (
                        <div>
                          <div>{formatNs(c.last_build.duration_ns)}</div>
                          <div className="text-xs text-muted-foreground">
                            {formatUnix(c.last_build.started_unix_ns)}
                          </div>
                        </div>
                      ) : (
                        <span className="text-muted-foreground">—</span>
                      )}
                    </TableCell>
                    <TableCell>{c.total_builds}</TableCell>
                    <TableCell>{c.cpu_time_ms_sum > 0 ? formatCpuMs(c.cpu_time_ms_sum) : '—'}</TableCell>
                    <TableCell>
                      <Link to={`/projects/${slug}/configs/${c.slug}`}>
                        <Button size="sm" variant="ghost">
                          Details →
                        </Button>
                      </Link>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
