using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Tamp;
using Tamp.NetCli.V10;
using Tamp.Yarn.V4;
using Tamp.Docker.V27;
using Tamp.Http;
using Tamp.Telegram;
using Tamp.Telemetry;

/// <summary>
/// tamp-beacon's dogfooded build pipeline. Uses Tamp's own satellites end-to-end:
/// Yarn for the SPA install/build, NetCli for restore/build/test/publish,
/// Docker for the multi-arch image, and Http for the post-build smoke probe.
/// </summary>
class Build : TampBuild
{
    public static int Main(string[] args)
    {
        // Wire OTLP export when the canonical env vars are set
        // (OTEL_EXPORTER_OTLP_ENDPOINT + OTEL_EXPORTER_OTLP_HEADERS). With
        // them unset this is a no-op — same Main() shape works local + CI.
        using var telemetry = TampTelemetry.FromEnvironment();
        return Execute<Build>(args);
    }

    [Parameter("Build configuration")]
    Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Parameter("Package version override", EnvironmentVariable = "PACKAGE_VERSION")]
#pragma warning disable CS0649
    readonly string? Version;
#pragma warning restore CS0649

    [Parameter("Push the docker image to ghcr.io after build (set on tag releases)", EnvironmentVariable = "BEACON_PUSH_IMAGE")]
#pragma warning disable CS0649
    readonly bool PushImage;
#pragma warning restore CS0649

    [Solution] readonly Solution Solution = null!;
    [GitRepository] readonly GitRepository Git = null!;

    [FromPath("yarn")] readonly Tool YarnTool = null!;

    // TAM-230 — Telegram failure-notify reporter. Reads TELEGRAM_BOT_TOKEN /
    // TELEGRAM_CHAT_ID / TELEGRAM_BUILD_LABEL env vars; returns null when
    // any required var is missing, and the framework silently skips null-
    // valued reporters. Local devs run without env vars set → no Telegram
    // pings; CI/lab/me set them → red builds ping the @Tampbuild_bot chat.
    [BuildReporter] readonly IBuildReporter? TelegramNotify =
        TelegramBuildReporter.FromEnvironment();

    AbsolutePath Artifacts => RootDirectory / "artifacts";
    AbsolutePath WebDir => RootDirectory / "web";
    AbsolutePath WwwRoot => RootDirectory / "src" / "Tamp.Beacon" / "wwwroot";
    AbsolutePath PublishDir => Artifacts / "publish";
    string ImageTag => string.IsNullOrEmpty(Version) ? "0.1.0" : Version!;

    Target Info => _ => _.Executes(() =>
    {
        Console.WriteLine($"  Branch:        {Git.Branch ?? "<detached>"}");
        Console.WriteLine($"  Commit:        {(Git.Commit is { } c && c.Length >= 7 ? c[..7] : "<unknown>")}");
        Console.WriteLine($"  Configuration: {Configuration}");
        Console.WriteLine($"  ImageTag:      {ImageTag}");
    });

    // TAM-230 — temporary smoke target that always fails. Used to verify the
    // Tamp.Telegram reporter wiring end-to-end: run with TELEGRAM_BOT_TOKEN
    // + TELEGRAM_CHAT_ID set and confirm the @Tampbuild_bot chat receives a
    // failure notification (target name + reason + stdout tail). Delete
    // before the next merge; this is not a production target.
    Target TestTelegramFailure => _ => _
        .Executes((Action)(() =>
        {
            Console.WriteLine("about to fail intentionally (TAM-230 smoke for Tamp.Telegram)");
            Console.WriteLine("this line should show up in the Telegram message body");
            for (var i = 1; i <= 5; i++)
                Console.WriteLine($"  context line {i}");
            throw new InvalidOperationException("intentional failure to exercise Tamp.Telegram BuildReporter");
        }));

    Target Clean => _ => _.Executes(() => CleanArtifacts());

    Target Restore => _ => _
        .Internal()
        .Executes(() => DotNet.Restore(s => s.SetProject(Solution.Path)));

    Target YarnInstall => _ => _
        .Internal()
        .Executes(() => Yarn.Install(YarnTool, s => s
            .SetWorkingDirectory(WebDir)
            .SetImmutable(IsServerBuild)));

    Target FrontendBuild => _ => _
        .DependsOn(YarnInstall)
        .Description("Yarn + Vite build the SPA into web/dist/.")
        .Executes(() => Yarn.Run(YarnTool, s => s
            .SetWorkingDirectory(WebDir)
            .SetScript("build")));

    Target CopyWwwroot => _ => _
        .Internal()
        .DependsOn(FrontendBuild)
        .Description("Copies web/dist/ into src/Tamp.Beacon/wwwroot/ for the .NET host to static-serve.")
        .Executes(() =>
        {
            var dist = WebDir / "dist";
            if (!dist.DirectoryExists())
                throw new InvalidOperationException($"FrontendBuild did not produce {dist}. Check `yarn build` output.");

            // Wipe wwwroot but leave .gitkeep so the empty placeholder behaviour still works
            // on the first inner-loop build before yarn ran.
            if (WwwRoot.DirectoryExists())
            {
                foreach (var f in WwwRoot.GlobFiles("**/*"))
                {
                    if (f.Value.EndsWith(".gitkeep", StringComparison.Ordinal)) continue;
                    f.DeleteFile();
                }
            }
            WwwRoot.EnsureDirectoryExists();

            foreach (var src in dist.GlobFiles("**/*"))
            {
                var rel = Path.GetRelativePath(dist.Value, src.Value);
                var dest = WwwRoot / rel;
                dest.Parent?.EnsureDirectoryExists();
                src.CopyTo(dest, overwrite: true);
            }
        });

    Target Compile => _ => _
        .DependsOn(Restore, CopyWwwroot)
        .Executes(() => DotNet.Build(s => s
            .SetProject(Solution.Path)
            .SetConfiguration(Configuration)
            .SetNoRestore(true)));

    Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() => DotNet.Test(s => s
            .SetProject(RootDirectory / "tests" / "Tamp.Beacon.Tests" / "Tamp.Beacon.Tests.csproj")
            .SetConfiguration(Configuration)
            .SetNoBuild(true)
            .AddLogger("trx;LogFileName=test-results.trx")
            .SetResultsDirectory(Artifacts / "test-results")));

    Target Publish => _ => _
        .DependsOn(Test)
        .Description("dotnet publish Tamp.Beacon, self-contained, for the container image (one rid per platform).")
        .Executes(() =>
        {
            var rids = new[] { "linux-x64", "linux-arm64" };
            return rids.Select(rid => DotNet.Publish(s => s
                .SetProject(RootDirectory / "src" / "Tamp.Beacon" / "Tamp.Beacon.csproj")
                .SetConfiguration(Configuration)
                .SetRuntime(rid)
                .SetSelfContained(true)
                .SetOutput(PublishDir / rid)));
        });

    Target DockerBuild => _ => _
        .DependsOn(Publish)
        .Description("Multi-arch Docker buildx → ghcr.io/tamp-build/tamp-beacon:{ImageTag} (push only when PushImage).")
        .Executes(() => Docker.Build(s => s
            .SetContext(RootDirectory)
            .SetDockerfile(RootDirectory / "Dockerfile")
            .AddTag($"ghcr.io/tamp-build/tamp-beacon:{ImageTag}")
            .AddTag("ghcr.io/tamp-build/tamp-beacon:latest")
            .AddPlatform("linux/amd64")
            .AddPlatform("linux/arm64")
            .SetPush(PushImage)));

    Target SmokeQa => _ => _
        .DependsOn(DockerBuild)
        .Description("Spins up the built image, waits for /healthz on :8080, then tears down.")
        .Executes(async () =>
        {
            var imageRef = $"ghcr.io/tamp-build/tamp-beacon:{ImageTag}";
            var containerName = $"tamp-beacon-smoke-{Guid.NewGuid():N}";

            RunShell("docker", $"run -d --name {containerName} -p 8080:8080 {imageRef}");
            try
            {
                await HttpProbe.WaitForHealthy(
                    "http://localhost:8080/healthz",
                    TimeSpan.FromSeconds(60));
            }
            finally
            {
                RunShell("docker", $"rm -f {containerName}", ignoreFailure: true);
            }
        });

    Target Ci => _ => _
        .DependsOn(Info, Clean, Test, DockerBuild);

    Target Default => _ => _.DependsOn(Compile);

    private static void RunShell(string file, string args, bool ignoreFailure = false)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {file}");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (!string.IsNullOrEmpty(stdout)) Console.WriteLine(stdout);
        if (!string.IsNullOrEmpty(stderr)) Console.Error.WriteLine(stderr);
        if (p.ExitCode != 0 && !ignoreFailure)
            throw new InvalidOperationException($"{file} {args} exited {p.ExitCode}");
    }
}
