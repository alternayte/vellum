namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// Deployment descriptors. All facts are of kind Deployment.
/// <list type="bullet">
/// <item><c>deployment:dockerfile:&lt;path&gt;</c>: base image of the last stage, images of all
/// stages, and the ports, entrypoint and command of the last stage. Not recognised: <c>ARG</c> and <c>ENV</c>
/// (never read into a fact), variable substitution, heredocs, <c>ONBUILD</c>.</item>
/// <item><c>deployment:k8s:&lt;kind&gt;/[&lt;namespace&gt;/]&lt;name&gt;</c> for Deployment, StatefulSet,
/// DaemonSet, Service and Ingress: names, images, container ports, replicas, service ports,
/// selector, ingress rules. Not recognised: every other kind, environment variables, volumes,
/// Helm and Kustomize (a template is skipped, not evaluated). A file with a Secret in any
/// document is refused before any extractor reads it.</item>
/// <item><c>deployment:workflow:&lt;path&gt;</c> for <c>.github/workflows/*.y[a]ml</c>: workflow name,
/// triggers, job ids, and which jobs build or deploy. Not recognised: reusable workflows,
/// matrix expansion, step contents beyond the build/deploy classification.</item>
/// </list>
/// </summary>
internal sealed class DeploymentExtractor : IExtractor
{
    private static readonly ExtractionRule Dockerfile = new("docker.dockerfile", 1);
    private static readonly ExtractionRule Workload = new("k8s.workload", 1);
    private static readonly ExtractionRule KubeService = new("k8s.service", 1);
    private static readonly ExtractionRule Ingress = new("k8s.ingress", 1);
    private static readonly ExtractionRule Workflow = new("github.workflow", 1);

    private static readonly string[] BuildWords = ["build", "publish", "package", "docker", "image", "push"];
    private static readonly string[] DeployWords = ["deploy", "release", "rollout"];
    private static readonly string[] BuildActions = ["docker/build-push-action"];
    private static readonly string[] DeployActions =
    [
        "azure/k8s-deploy", "azure/webapps-deploy", "azure/aks-set-context", "aws-actions/amazon-ecs-deploy-task-definition",
        "google-github-actions/deploy-cloudrun", "google-github-actions/get-gke-credentials", "helm/",
    ];
    private static readonly string[] BuildCommands = ["docker build", "docker buildx", "docker push", "dotnet publish", "go build"];
    private static readonly string[] DeployCommands = ["kubectl ", "helm upgrade", "helm install", "terraform apply"];

    public string Id => "deployment";
    public IReadOnlyList<ExtractionRule> Rules => [Dockerfile, Workload, KubeService, Ingress, Workflow];

    public void Run(ExtractionContext context)
    {
        foreach (var file in context.Files)
        {
            if (context.Stopped)
                return;
            if (PathFilter.IsDockerfile(file.Name))
                AddDockerfile(context, file);
            else if (file.IsYaml && file.Path.StartsWith(".github/workflows/", StringComparison.Ordinal))
                AddWorkflow(context, file);
            else if (file.IsYaml && !ComposeExtractor.IsComposeFile(file))
                AddManifests(context, file);
        }
    }

    private static void AddManifests(ExtractionContext context, SourceText file)
    {
        var manifests = context.Documents(file)
            .Where(root => root.Kind == DocKind.Map && root.Get("apiVersion") is not null && root.Get("kind") is not null).ToList();
        if (manifests.Count == 0)
            return;
        // "{{ .Values.replicas }}" is valid YAML with another meaning. A chart template is
        // not a manifest until Helm renders it, so nothing is read from it.
        if (HasTemplateAction(file.Content))
        {
            context.Note(NoteKind.Skipped, "template-not-evaluated", file.Path);
            return;
        }
        foreach (var manifest in manifests)
            AddManifest(context, file, manifest);
    }

    /// <summary>True for "{{" that is not the "${{" of a GitHub Actions expression.</summary>
    private static bool HasTemplateAction(string content)
    {
        for (var at = content.IndexOf("{{", StringComparison.Ordinal); at >= 0; at = content.IndexOf("{{", at + 2, StringComparison.Ordinal))
            if (at == 0 || content[at - 1] != '$')
                return true;
        return false;
    }

    private static void AddDockerfile(ExtractionContext context, SourceText file)
    {
        var stages = new List<(string Image, string? Alias)>();
        // What each stage exposes and runs. A stage that starts from an earlier stage inherits
        // from it; a stage that starts from an image starts clean, so a debug port of a build
        // stage is not reported for the image.
        var states = new List<(List<string> Ports, string? Entrypoint, string? Command, List<SourceEvidence> Evidence)>();
        var evidence = new List<SourceEvidence>();

        foreach (var (text, start, end) in LogicalLines(file.Content))
        {
            var space = text.IndexOfAny([' ', '\t']);
            var instruction = (space < 0 ? text : text[..space]).ToUpperInvariant();
            var rest = space < 0 ? "" : text[(space + 1)..].Trim();
            var at = Dockerfile.At(file.Path, start, end);
            if (instruction == "FROM")
            {
                var words = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(w => !w.StartsWith("--", StringComparison.Ordinal)).ToArray();
                if (words.Length == 0)
                    continue;
                var parent = stages.FindIndex(s => s.Alias is not null && s.Alias.Equals(words[0], StringComparison.OrdinalIgnoreCase));
                states.Add(parent < 0
                    ? ([], null, null, [])
                    : ([.. states[parent].Ports], states[parent].Entrypoint, states[parent].Command, [.. states[parent].Evidence]));
                stages.Add((words[0], words.Length >= 3 && words[1].Equals("AS", StringComparison.OrdinalIgnoreCase) ? words[2] : null));
                evidence.Add(at);
            }
            else if (states.Count > 0 && instruction is "EXPOSE" or "ENTRYPOINT" or "CMD")
            {
                var state = states[^1];
                state.Evidence.Add(at);
                if (instruction == "EXPOSE")
                    state.Ports.AddRange(rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                else if (instruction == "ENTRYPOINT")
                    states[^1] = state with { Entrypoint = TextUtil.Bounded(rest, 300) };
                else
                    states[^1] = state with { Command = TextUtil.Bounded(rest, 300) };
            }
        }
        if (stages.Count == 0)
            return;

        // "FROM build AS final" names an earlier stage. The base image is where the chain ends.
        var baseImage = stages[^1].Image;
        for (var hop = 0; hop < stages.Count; hop++)
        {
            var named = stages.FindIndex(s => s.Alias is not null && s.Alias.Equals(baseImage, StringComparison.OrdinalIgnoreCase));
            if (named < 0)
                break;
            baseImage = stages[named].Image;
        }
        var aliases = stages.Select(s => s.Alias).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var last = states[^1];
        evidence.AddRange(last.Evidence);
        context.Add(FactKind.Deployment, "deployment:dockerfile:" + file.Path, file.Path, evidence,
        [
            ("source", "dockerfile"), ("baseImage", baseImage),
            ("stageImages", TextUtil.JoinSorted(stages.Select(s => s.Image).Where(i => !aliases.Contains(i)))),
            ("ports", TextUtil.Join(last.Ports)), ("entrypoint", last.Entrypoint), ("command", last.Command),
        ]);
    }

    /// <summary>Instructions with their line ranges: a trailing backslash joins lines, a '#' line is a comment.</summary>
    private static IEnumerable<(string Text, int Start, int End)> LogicalLines(string content)
    {
        var builder = new System.Text.StringBuilder();
        int line = 0, start = 0;
        foreach (var raw in content.Split('\n'))
        {
            line++;
            var trimmed = raw.Trim();
            if (trimmed.StartsWith('#') || (trimmed.Length == 0 && builder.Length == 0))
                continue;
            if (builder.Length == 0)
                start = line;
            var continued = trimmed.EndsWith('\\');
            builder.Append(continued ? trimmed[..^1].TrimEnd() : trimmed).Append(' ');
            if (continued)
                continue;
            yield return (builder.ToString().Trim(), start, line);
            builder.Clear();
        }
        if (builder.Length > 0)
            yield return (builder.ToString().Trim(), start, line);
    }

    private static void AddManifest(ExtractionContext context, SourceText file, DocNode root)
    {
        if (root.Kind != DocKind.Map || root.Str("apiVersion") is null || root.Str("kind") is not { } kind)
            return;
        if (kind is not ("Deployment" or "StatefulSet" or "DaemonSet" or "Service" or "Ingress"))
            return;
        var metadata = root.Map("metadata");
        if (metadata?.Str("name") is not { } name)
        {
            context.Note(NoteKind.Undecided, "unnamed-manifest", file.Path, root.StartLine);
            return;
        }
        var ns = metadata.Str("namespace");
        var key = $"deployment:k8s:{kind.ToLowerInvariant()}/{(ns is null ? "" : ns + "/")}{name}";
        var spec = root.Map("spec");
        var attributes = new List<(string, string?)>
        {
            ("source", "kubernetes"), ("kind", kind), ("name", name), ("namespace", ns),
        };
        var rule = Workload;

        if (kind == "Service")
        {
            rule = KubeService;
            attributes.Add(("type", spec?.Str("type")));
            attributes.Add(("ports", TextUtil.Join((spec?.Seq("ports")?.Items ?? [])
                .Where(p => p.Kind == DocKind.Map && p.Str("port") is not null)
                .Select(p => p.Str("targetPort") is { } target ? $"{p.Str("port")}->{target}" : p.Str("port")!))));
            attributes.Add(("selector", TextUtil.JoinSorted((spec?.Map("selector")?.Entries ?? [])
                .Where(e => e.Value.AsText() is not null).Select(e => $"{e.Key}={e.Value.AsText()}"))));
        }
        else if (kind == "Ingress")
        {
            rule = Ingress;
            var rules = new List<string>();
            foreach (var item in spec?.Seq("rules")?.Items ?? [])
            {
                if (item.Kind != DocKind.Map)
                    continue;
                var host = item.Str("host") ?? "*";
                foreach (var path in item.Map("http")?.Seq("paths")?.Items ?? [])
                {
                    var service = path.Kind == DocKind.Map ? path.Map("backend")?.Map("service") : null;
                    if (service?.Str("name") is not { } backend)
                        continue;
                    var port = service.Map("port")?.Str("number") ?? service.Map("port")?.Str("name");
                    rules.Add($"{host}{path.Str("path") ?? "/"}->{backend}{(port is null ? "" : ":" + port)}");
                }
            }
            attributes.Add(("rules", TextUtil.Join(rules)));
        }
        else
        {
            var containers = (spec?.Map("template")?.Map("spec")?.Seq("containers")?.Items ?? []).Where(c => c.Kind == DocKind.Map).ToList();
            attributes.Add(("replicas", spec?.Str("replicas")));
            attributes.Add(("containers", TextUtil.Join(containers.Select(c => c.Str("name")).OfType<string>())));
            attributes.Add(("images", TextUtil.Join(containers.Select(c => c.Str("image")).OfType<string>())));
            attributes.Add(("ports", TextUtil.Join(containers
                .SelectMany(c => c.Seq("ports")?.Items ?? [])
                .Select(p => p.Kind == DocKind.Map ? p.Str("containerPort") : null).OfType<string>())));
        }

        context.Add(FactKind.Deployment, key, name,
            [rule.At(file.Path, root.StartLine, file.TrimEnd(root.StartLine, root.EndLine))], attributes);
    }

    private static void AddWorkflow(ExtractionContext context, SourceText file)
    {
        if (context.Documents(file).FirstOrDefault() is not { Kind: DocKind.Map } root)
            return;
        var evidence = new List<SourceEvidence>();
        if (root.Entry("name") is { } name)
            evidence.Add(Workflow.At(file.Path, name.KeyLine, name.Value.EndLine));

        var triggers = root.Get("on") switch
        {
            { Kind: DocKind.Map } map => map.Entries.Select(e => e.Key),
            { Kind: DocKind.Seq } seq => seq.Items.Select(i => i.AsText()).OfType<string>(),
            { } scalar when scalar.AsText() is { } text => [text],
            _ => [],
        };

        List<string> jobs = [], build = [], deploy = [];
        foreach (var job in root.Map("jobs")?.Entries ?? [])
        {
            if (job.Value.Kind != DocKind.Map)
                continue;
            jobs.Add(job.Key);
            var label = job.Key + " " + (job.Value.Str("name") ?? "");
            var steps = (job.Value.Seq("steps")?.Items ?? []).Where(s => s.Kind == DocKind.Map).ToList();
            var uses = steps.Select(s => s.Str("uses")).OfType<string>().ToList();
            var runs = steps.Select(s => s.Str("run")).OfType<string>().ToList();

            var builds = ContainsAny(label, BuildWords) || uses.Any(u => StartsWithAny(u, BuildActions)) || runs.Any(r => ContainsAny(r, BuildCommands));
            var deploys = ContainsAny(label, DeployWords) || uses.Any(u => StartsWithAny(u, DeployActions)) || runs.Any(r => ContainsAny(r, DeployCommands));
            if (builds)
                build.Add(job.Key);
            if (deploys)
                deploy.Add(job.Key);
            if (builds || deploys)
                evidence.Add(Workflow.At(file.Path, job.KeyLine, file.TrimEnd(job.KeyLine, job.Value.EndLine)));
        }
        if (evidence.Count == 0)
            evidence.Add(Workflow.At(file.Path, root.StartLine, root.StartLine));

        context.Add(FactKind.Deployment, "deployment:workflow:" + file.Path, root.Str("name") ?? file.Name, evidence,
        [
            ("source", "github-actions"), ("name", root.Str("name")),
            ("triggers", TextUtil.JoinSorted(triggers)),
            ("jobs", TextUtil.Join(jobs)), ("buildJobs", TextUtil.Join(build)), ("deployJobs", TextUtil.Join(deploy)),
        ]);
    }

    private static bool ContainsAny(string text, string[] words) =>
        words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool StartsWithAny(string text, string[] prefixes) =>
        prefixes.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
