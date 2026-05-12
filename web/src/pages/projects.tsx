import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { formatUnixNs } from '@/lib/utils';

export default function ProjectsPage() {
  const projects = useQuery({
    queryKey: ['projects'],
    queryFn: api.getProjects,
    refetchInterval: 15000,
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Projects</CardTitle>
      </CardHeader>
      <CardContent className="p-0">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Name</TableHead>
              <TableHead>Area</TableHead>
              <TableHead>Last seen</TableHead>
              <TableHead>Builds</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {projects.data?.projects.map((p) => (
              <TableRow key={`${p.name}::${p.area ?? ''}`}>
                <TableCell className="font-medium">{p.name}</TableCell>
                <TableCell className="text-muted-foreground">{p.area ?? '—'}</TableCell>
                <TableCell className="font-mono text-xs">{formatUnixNs(p.last_seen_unix_ns)}</TableCell>
                <TableCell>{p.builds_count}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </CardContent>
    </Card>
  );
}
