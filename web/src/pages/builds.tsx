import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '@/lib/api';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { formatDurationNs, formatUnixNs } from '@/lib/utils';

export default function BuildsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const [organization, setOrganization] = useState(searchParams.get('organization') ?? '');
  const [project, setProject] = useState(searchParams.get('project') ?? '');
  const [area, setArea] = useState(searchParams.get('area') ?? '');

  useEffect(() => {
    const next = new URLSearchParams();
    if (organization) next.set('organization', organization);
    if (project) next.set('project', project);
    if (area) next.set('area', area);
    setSearchParams(next, { replace: true });
  }, [organization, project, area, setSearchParams]);

  const builds = useQuery({
    queryKey: ['builds', organization, project, area],
    queryFn: () =>
      api.getBuilds({
        organization: organization || undefined,
        project: project || undefined,
        area: area || undefined,
        limit: 100,
      }),
  });

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader>
          <CardTitle>Builds</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-wrap gap-2">
          <Input
            placeholder="Filter by organization (e.g. Tamp)"
            value={organization}
            onChange={(e) => setOrganization(e.target.value)}
            className="max-w-xs"
          />
          <Input
            placeholder="Filter by project (e.g. HoldFast)"
            value={project}
            onChange={(e) => setProject(e.target.value)}
            className="max-w-xs"
          />
          <Input
            placeholder="Filter by area (e.g. frontend)"
            value={area}
            onChange={(e) => setArea(e.target.value)}
            className="max-w-xs"
          />
        </CardContent>
      </Card>

      <Card>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Outcome</TableHead>
                <TableHead>Organization</TableHead>
                <TableHead>Project</TableHead>
                <TableHead>Area</TableHead>
                <TableHead>Started</TableHead>
                <TableHead>Duration</TableHead>
                <TableHead>Targets</TableHead>
                <TableHead>Commands</TableHead>
                <TableHead>CI</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {builds.data?.builds.map((b) => (
                <TableRow key={b.id} className="cursor-pointer">
                  <TableCell>
                    <Link to={`/builds/${b.id}`}>
                      <Badge variant={b.outcome === 'success' ? 'success' : 'destructive'}>{b.outcome}</Badge>
                    </Link>
                  </TableCell>
                  <TableCell>{b.organization}</TableCell>
                  <TableCell><Link to={`/builds/${b.id}`}>{b.project_name}</Link></TableCell>
                  <TableCell>{b.project_area ?? '—'}</TableCell>
                  <TableCell className="font-mono text-xs">{formatUnixNs(b.started_unix_ns)}</TableCell>
                  <TableCell>{formatDurationNs(b.duration_ns)}</TableCell>
                  <TableCell>
                    {b.targets_total} ({b.targets_failed} failed)
                  </TableCell>
                  <TableCell>{b.commands_total}</TableCell>
                  <TableCell>{b.ci_vendor ?? 'local'}</TableCell>
                </TableRow>
              ))}
              {builds.data?.builds.length === 0 && (
                <TableRow>
                  <TableCell colSpan={9} className="text-center text-muted-foreground py-12">
                    No builds yet. Point a Tamp build at OTEL_EXPORTER_OTLP_ENDPOINT and run something.
                  </TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        </CardContent>
      </Card>
    </div>
  );
}
