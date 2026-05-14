import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';

function formatNs(ns: number): string {
  const ms = ns / 1_000_000;
  if (ms < 1000) return `${ms.toFixed(0)} ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(1)} s`;
  return `${(s / 60).toFixed(1)} m`;
}

function formatUnix(ns: number): string {
  return new Date(ns / 1_000_000).toLocaleString();
}

export default function BuildDetailPage() {
  const { id = '' } = useParams<{ id: string }>();
  const buildId = Number.parseInt(id, 10);
  const { data, isLoading, error } = useQuery({
    queryKey: ['build', buildId],
    queryFn: () => api.getBuild(buildId),
    enabled: Number.isFinite(buildId) && buildId > 0,
  });

  if (isLoading) return <p className="text-muted-foreground">Loading…</p>;
  if (error || !data)
    return <p className="text-destructive">Build not found or you don't have access.</p>;

  const { build, targets, commands, events } = data;
  const commandsByTarget = new Map<number, typeof commands>();
  for (const c of commands) {
    const arr = commandsByTarget.get(c.target_id) ?? [];
    arr.push(c);
    commandsByTarget.set(c.target_id, arr);
  }

  return (
    <div className="space-y-6">
      <div>
        <Link to="/builds" className="text-sm text-muted-foreground hover:text-foreground">
          ← Builds
        </Link>
        <h1 className="text-2xl font-semibold mt-1">
          Build #{build.seq}
          <Badge className="ml-3" variant={build.outcome === 'success' ? 'default' : 'destructive'}>
            {build.outcome}
          </Badge>
        </h1>
        <div className="text-sm text-muted-foreground mt-1 space-x-2">
          <Link to={`/projects/${build.project_slug}`} className="font-mono hover:text-foreground">
            {build.project_slug}
          </Link>
          {build.project_area && <span>· {build.project_area}</span>}
          <span>· {formatUnix(build.started_unix_ns)}</span>
          <span>· {formatNs(build.duration_ns)}</span>
        </div>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Summary</CardTitle>
        </CardHeader>
        <CardContent className="grid grid-cols-2 md:grid-cols-4 gap-4 text-sm">
          <Stat label="Exit code" value={build.exit_code} />
          <Stat label="Targets total" value={build.targets_total} />
          <Stat label="Targets failed" value={build.targets_failed} />
          <Stat label="Commands total" value={build.commands_total} />
          <Stat label="CLI version" value={build.cli_version ?? '—'} />
          <Stat label="Host" value={`${build.host_os ?? '?'}/${build.host_arch ?? '?'}`} />
          <Stat label="CI vendor" value={build.ci_vendor ?? '—'} />
          <Stat
            label="Peak memory"
            value={
              build.peak_memory_b > 0
                ? `${(build.peak_memory_b / 1024 / 1024).toFixed(1)} MB`
                : '—'
            }
          />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Targets ({targets.length})</CardTitle>
        </CardHeader>
        <CardContent>
          {targets.length === 0 ? (
            <p className="text-muted-foreground">No target spans landed for this build.</p>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead>
                  <TableHead>Phase</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead>Duration</TableHead>
                  <TableHead>Commands</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {targets.map((t) => (
                  <TableRow key={t.id}>
                    <TableCell className="font-mono">{t.name}</TableCell>
                    <TableCell className="text-muted-foreground">{t.phase ?? '—'}</TableCell>
                    <TableCell>
                      <Badge
                        variant={
                          t.status === 'success' || t.status === 'skipped'
                            ? 'default'
                            : 'destructive'
                        }
                      >
                        {t.status}
                      </Badge>
                    </TableCell>
                    <TableCell>{formatNs(t.duration_ns)}</TableCell>
                    <TableCell className="text-sm text-muted-foreground">
                      {(commandsByTarget.get(t.id) ?? []).length}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>

      {events.length > 0 && (
        <Card>
          <CardHeader>
            <CardTitle>Events ({events.length})</CardTitle>
          </CardHeader>
          <CardContent>
            <ul className="space-y-1 font-mono text-sm">
              {events.map((e) => (
                <li key={e.id} className="text-muted-foreground">
                  <span>{formatUnix(e.at_unix_ns)}</span> <span className="text-foreground">{e.name}</span>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>
      )}
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string | number }) {
  return (
    <div>
      <div className="text-xs text-muted-foreground uppercase tracking-wide">{label}</div>
      <div className="text-sm font-medium mt-0.5">{value}</div>
    </div>
  );
}
