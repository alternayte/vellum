namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// Deployment descriptors. All facts are of kind Deployment and all rules have version 2.
/// <list type="bullet">
/// <item><c>docker.dockerfile</c>: <c>deployment:dockerfile:&lt;path&gt;</c> for a file named
/// <c>Dockerfile</c>, <c>Dockerfile.*</c> or <c>*.Dockerfile</c>. See <see cref="DockerfileReader"/>.</item>
/// <item><c>k8s.workload</c>, <c>k8s.service</c>, <c>k8s.ingress</c>:
/// <c>deployment:k8s:&lt;path&gt;#&lt;kind in lower case&gt;/&lt;namespace or "default"&gt;/&lt;name&gt;</c>
/// for each document of a YAML file (and each item of a <c>kind: List</c>) that has
/// <c>apiVersion</c>, <c>metadata.name</c> and the <c>kind</c> Deployment, StatefulSet, DaemonSet,
/// Service or Ingress. The attribute <c>namespace</c> is there only when the manifest names one.
/// <list type="bullet">
/// <item>Workload: <c>replicas</c> (a number), <c>containers</c>, <c>images</c> (safe references
/// only, see <see cref="ImageReference.Checked"/>), <c>ports</c> (container ports), and for the
/// first <see cref="MaxContainers"/> containers <c>command.&lt;container&gt;</c> and
/// <c>args.&lt;container&gt;</c>: the executable and the flag names only
/// (<see cref="CommandLine"/>); <c>args</c> is an argument list and has no executable.</item>
/// <item>Service: <c>type</c>, <c>ports</c> (<c>port-&gt;targetPort</c>) and <c>selector</c>
/// (<c>label=value</c> for identifier values under labels without a credential-like name;
/// another pair is left out with a <c>selector-not-emitted</c> note).</item>
/// <item>Ingress: <c>rules</c> as <c>host/path-&gt;service:port</c>, for hosts of letters, digits
/// and <c>. - *</c> and paths without white space.</item>
/// </list>
/// Not recognised: every other kind; environment variables (names or values); labels and
/// annotations; volumes; init containers; a <c>List</c> inside a <c>List</c>; Helm and Kustomize
/// (a file with a template action gives a <c>template-not-evaluated</c> note and nothing else).
/// A file with a Secret in any document or list item is refused before any extractor reads it.</item>
/// <item><c>github.workflow</c>: <c>deployment:workflow:&lt;path&gt;</c> for the one document of a
/// <c>.github/workflows/*.y[a]ml</c> file that has <c>jobs</c> or <c>on</c>: <c>name</c>,
/// <c>triggers</c>, <c>jobs</c>, and <c>buildJobs</c>, <c>deployJobs</c>, <c>otherJobs</c>. Every
/// job is in <c>otherJobs</c> or in one or both of the other two. A job builds or deploys
/// only when one of its steps
/// <list type="bullet">
/// <item>uses an action whose text before '@' is exactly (without case) one of
/// <see cref="BuildActions"/> or <see cref="DeployActions"/>, or</item>
/// <item>has a <c>run</c> script with a line whose first words, after leading
/// <c>NAME=value</c> assignments, are exactly one of <see cref="BuildCommands"/> or
/// <see cref="DeployCommands"/>.</item>
/// </list>
/// The name of a job says nothing. The text of a <c>run</c> script is read for this
/// classification only and is never emitted. <c>env</c>, <c>with</c>, <c>inputs</c> and
/// <c>secrets</c> are never read. A workflow name that looks like a credential is left out with
/// a <c>name-not-emitted</c> note; the fact is then named by its file.
/// Not recognised: reusable workflows (a job with <c>uses</c> is in <c>otherJobs</c>), matrix
/// expansion, composite actions, a command after <c>sudo</c>, <c>time</c> or in a script file,
/// and a command that is not the first of its line.</item>
/// </list>
/// </summary>
internal sealed class DeploymentExtractor : IExtractor
{
    private static readonly ExtractionRule Dockerfile = new("docker.dockerfile", 2);
    private static readonly ExtractionRule Workload = new("k8s.workload", 2);
    private static readonly ExtractionRule KubeService = new("k8s.service", 2);
    private static readonly ExtractionRule Ingress = new("k8s.ingress", 2);
    private static readonly ExtractionRule Workflow = new("github.workflow", 2);

    /// <summary>Containers of one workload whose command and arguments are read.</summary>
    internal const int MaxContainers = 32;

    /// <summary>Actions that build or push an image, as <c>owner/name</c>.</summary>
    private static readonly HashSet<string> BuildActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "docker/build-push-action", "docker/bake-action", "redhat-actions/buildah-build",
    };

    /// <summary>Actions that deploy to a runtime, as <c>owner/name</c>. An action that only logs in or sets a context is not one.</summary>
    private static readonly HashSet<string> DeployActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "azure/k8s-deploy", "azure/webapps-deploy", "azure/functions-action", "azure/container-apps-deploy-action",
        "aws-actions/amazon-ecs-deploy-task-definition",
        "google-github-actions/deploy-cloudrun", "google-github-actions/deploy-appengine", "google-github-actions/deploy-cloud-functions",
    };

    /// <summary>The first words of a script line that build or push an artifact.</summary>
    private static readonly string[][] BuildCommands =
    [
        ["docker", "build"], ["docker", "buildx", "build"], ["docker", "push"], ["dotnet", "publish"], ["go", "build"],
    ];

    /// <summary>The first words of a script line that deploy.</summary>
    private static readonly string[][] DeployCommands =
    [
        ["kubectl", "apply"], ["kubectl", "rollout"], ["helm", "upgrade"], ["helm", "install"], ["terraform", "apply"],
    ];

    public string Id => "deployment";
    public IReadOnlyList<ExtractionRule> Rules => [Dockerfile, Workload, KubeService, Ingress, Workflow];

    public void Run(ExtractionContext context)
    {
        foreach (var file in context.Files)
        {
            if (context.Stopped)
                return;
            if (PathFilter.IsDockerfile(file.Name))
                context.Guarded(Id, file.Path, () => DockerfileReader.Read(context, file, Dockerfile));
            else if (file.IsYaml && file.Path.StartsWith(".github/workflows/", StringComparison.Ordinal))
                context.Guarded(Id, file.Path, () => AddWorkflow(context, file));
            else if (file.IsYaml && !ComposeExtractor.IsComposeFile(file))
                context.Guarded(Id, file.Path, () => AddManifests(context, file));
        }
    }

    // ---- Kubernetes --------------------------------------------------------------------

    private static void AddManifests(ExtractionContext context, SourceText file)
    {
        var meter = context.Meter(file);
        var manifests = new List<DocNode>();
        foreach (var root in context.Documents(file))
        {
            if (!meter.Charge())
                return;
            if (!IsManifest(root))
                continue;
            if (root.Str("kind") != "List")
            {
                manifests.Add(root);
                continue;
            }
            var items = root.Seq("items")?.Items ?? [];
            if (!meter.Charge(items.Count))
                return;
            foreach (var item in items)
                if (IsManifest(item))
                    manifests.Add(item);
        }
        if (manifests.Count == 0)
            return;
        // "{{ .Values.replicas }}" is valid YAML with another meaning. A chart template is
        // not a manifest until Helm renders it, so nothing is read from it.
        if (!meter.Charge(file.Content.Length))
            return;
        if (HasTemplateAction(file.Content))
        {
            context.Note(NoteKind.Skipped, "template-not-evaluated", file.Path);
            return;
        }
        foreach (var manifest in manifests)
        {
            if (context.Stopped)
                return;
            AddManifest(context, file, manifest, meter);
        }
    }

    private static bool IsManifest(DocNode node) =>
        node.Kind == DocKind.Map && node.Str("apiVersion") is not null && node.Str("kind") is not null;

    /// <summary>True for "{{" that is not the "${{" of a GitHub Actions expression.</summary>
    private static bool HasTemplateAction(string content)
    {
        for (var at = content.IndexOf("{{", StringComparison.Ordinal); at >= 0; at = content.IndexOf("{{", at + 2, StringComparison.Ordinal))
            if (at == 0 || content[at - 1] != '$')
                return true;
        return false;
    }

    private static void AddManifest(ExtractionContext context, SourceText file, DocNode root, WorkMeter meter)
    {
        if (!meter.Charge())
            return;
        var kind = root.Str("kind")!;
        if (kind is not ("Deployment" or "StatefulSet" or "DaemonSet" or "Service" or "Ingress"))
            return;
        var metadata = root.Map("metadata");
        if (metadata?.Str("name") is not { } name)
        {
            context.Note(NoteKind.Undecided, "unnamed-manifest", file.Path, root.StartLine);
            return;
        }
        var ns = metadata.Str("namespace");
        var key = $"deployment:k8s:{file.Path}#{kind.ToLowerInvariant()}/{ns ?? "default"}/{name}";
        var spec = root.Map("spec");
        var line = root.StartLine;
        var attributes = new List<(string, string?)>
        {
            ("source", "kubernetes"), ("kind", kind), ("name", name), ("namespace", ns),
        };
        var rule = Workload;

        if (kind == "Service")
        {
            rule = KubeService;
            attributes.Add(("type", spec?.Str("type") is { } type && CredentialGuard.IsIdentifierToken(type) ? type : null));
            var ports = new List<string>();
            var portItems = spec?.Seq("ports")?.Items ?? [];
            var selector = spec?.Map("selector")?.Entries ?? [];
            if (!meter.Charge(portItems.Count + selector.Count))
                return;
            foreach (var item in portItems)
            {
                if (item.Kind != DocKind.Map || item.Str("port") is not { } port || !CredentialGuard.IsIdentifierToken(port))
                    continue;
                ports.Add(item.Str("targetPort") is { } target && CredentialGuard.IsIdentifierToken(target) ? $"{port}->{target}" : port);
            }
            attributes.Add(("ports", context.List(ports, file.Path, line)));
            var labels = new List<string>();
            foreach (var label in selector)
            {
                if (label.Value.AsText() is not { } value)
                    continue;
                if (IsLabelKey(label.Key) && !CredentialGuard.IsCredentialName(label.Key) && CredentialGuard.IsIdentifierToken(value))
                    labels.Add($"{label.Key}={value}");
                else
                    context.Note(NoteKind.Redacted, "selector-not-emitted", file.Path, label.KeyLine);
            }
            attributes.Add(("selector", context.List(labels, file.Path, line, sorted: true)));
        }
        else if (kind == "Ingress")
        {
            rule = Ingress;
            var rules = new List<string>();
            foreach (var item in spec?.Seq("rules")?.Items ?? [])
            {
                if (!meter.Charge())
                    return;
                if (item.Kind != DocKind.Map)
                    continue;
                var host = item.Str("host") ?? "*";
                if (!IsHost(host))
                    continue;
                foreach (var path in item.Map("http")?.Seq("paths")?.Items ?? [])
                {
                    if (!meter.Charge())
                        return;
                    var service = path.Kind == DocKind.Map ? path.Map("backend")?.Map("service") : null;
                    if (service?.Str("name") is not { } backend || !CredentialGuard.IsIdentifierToken(backend))
                        continue;
                    var route = path.Str("path") ?? "/";
                    if (route.Length > 200 || !route.StartsWith('/') || route.AsSpan().IndexOfAny(' ', '\t', '\n') >= 0)
                        continue;
                    var port = service.Map("port")?.Str("number") ?? service.Map("port")?.Str("name");
                    rules.Add($"{host}{route}->{backend}{(port is not null && CredentialGuard.IsIdentifierToken(port) ? ":" + port : "")}");
                }
            }
            attributes.Add(("rules", context.List(rules, file.Path, line)));
        }
        else
        {
            var containers = spec?.Map("template")?.Map("spec")?.Seq("containers")?.Items ?? [];
            if (!meter.Charge(containers.Count))
                return;
            List<string> names = [], images = [], ports = [];
            var commands = 0;
            foreach (var container in containers)
            {
                if (container.Kind != DocKind.Map)
                    continue;
                var containerName = container.Str("name");
                if (containerName is not null && CredentialGuard.IsIdentifierToken(containerName))
                    names.Add(containerName);
                else
                    containerName = null;
                if (container.Entry("image") is { } image && image.Value.AsText() is { } reference
                    && ImageReference.Checked(context, file, reference, image.KeyLine) is { } safe)
                    images.Add(safe);
                var portItems = container.Seq("ports")?.Items ?? [];
                if (!meter.Charge(portItems.Count))
                    return;
                foreach (var item in portItems)
                    if (item.Kind == DocKind.Map && item.Str("containerPort") is { } port && CredentialGuard.IsIdentifierToken(port))
                        ports.Add(port);

                if (containerName is null || (container.Get("command") is null && container.Get("args") is null))
                    continue;
                if (commands++ >= MaxContainers)
                {
                    context.LimitReached("containers", file.Path, stop: false, line);
                    continue;
                }
                foreach (var field in (string[])["command", "args"])
                {
                    if (container.Entry(field) is not { Value.Kind: DocKind.Seq } words)
                        continue;
                    if (!meter.Charge(words.Value.Items.Count))
                        return;
                    attributes.Add(($"{field}.{containerName}", CommandLine.Reduce(
                        words.Value.Items.Select(w => w.AsText() ?? "").ToList(), field == "command", context, file.Path, words.KeyLine)));
                }
            }
            attributes.Add(("replicas", spec?.Get("replicas") is { Kind: DocKind.Scalar, Type: ScalarType.Number } replicas ? replicas.Text : null));
            attributes.Add(("containers", context.List(names, file.Path, line)));
            attributes.Add(("images", context.List(images, file.Path, line)));
            attributes.Add(("ports", context.List(ports, file.Path, line)));
        }

        context.Add(FactKind.Deployment, key, name, [rule.At(file.Path, root.StartLine, root.EndLine)], attributes);
    }

    /// <summary>A label key: an optional DNS prefix and '/', then letters, digits and <c>- _ .</c>; at most 317 characters.</summary>
    private static bool IsLabelKey(string key)
    {
        if (key.Length is 0 or > 317)
            return false;
        foreach (var c in key)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/'))
                return false;
        return true;
    }

    private static bool IsHost(string host)
    {
        if (host.Length is 0 or > 253)
            return false;
        foreach (var c in host)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '*'))
                return false;
        return true;
    }

    // ---- GitHub workflows --------------------------------------------------------------

    private static void AddWorkflow(ExtractionContext context, SourceText file)
    {
        if (DocumentSet.Single(context, file, document => document.Get("jobs") is not null || document.Get("on") is not null) is not { } root)
            return;
        var meter = context.Meter(file);
        var evidence = new List<SourceEvidence>();
        // The name is also the name of the fact. One that looks like a credential is left out.
        var name = root.Entry("name")?.Value.AsText() is { Length: > 0 and <= 256 } ? root.Entry("name") : null;
        if (name is not null && CredentialGuard.LooksSecret(name.Value.Text))
        {
            context.Note(NoteKind.Redacted, "name-not-emitted", file.Path, name.KeyLine);
            name = null;
        }
        if (name is not null)
            evidence.Add(Workflow.At(file.Path, name.KeyLine, name.Value.EndLine));

        var on = root.Get("on");
        if (on is not null && !meter.Charge(on.Entries.Count + on.Items.Count))
            return;
        var triggers = (on switch
        {
            { Kind: DocKind.Map } map => map.Entries.Select(e => e.Key),
            { Kind: DocKind.Seq } seq => seq.Items.Select(i => i.AsText()).OfType<string>(),
            { } scalar when scalar.AsText() is { } text => [text],
            _ => [],
        }).Where(CredentialGuard.IsIdentifierToken);

        List<string> jobs = [], build = [], deploy = [], other = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in root.Map("jobs")?.Entries ?? [])
        {
            if (!meter.Charge())
                return;
            if (job.Value.Kind != DocKind.Map || !seen.Add(job.Key))
                continue;
            if (!CredentialGuard.IsIdentifierToken(job.Key))
            {
                context.Note(NoteKind.Undecided, "invalid-job-id", file.Path, job.KeyLine);
                continue;
            }
            jobs.Add(job.Key);
            bool builds = false, deploys = false;
            foreach (var step in job.Value.Seq("steps")?.Items ?? [])
            {
                if (!meter.Charge())
                    return;
                if (step.Kind != DocKind.Map)
                    continue;
                if (step.Str("uses") is { } uses)
                {
                    var at = uses.IndexOf('@');
                    var action = at < 0 ? uses : uses[..at];
                    builds |= BuildActions.Contains(action);
                    deploys |= DeployActions.Contains(action);
                }
                if (step.Str("run") is { } script)
                {
                    if (!meter.Charge(script.Length))
                        return;
                    foreach (var line in script.AsSpan().EnumerateLines())
                    {
                        var words = CommandLine.SimpleCommand(line, maxWords: 3);
                        builds |= StartsWithAny(words, BuildCommands);
                        deploys |= StartsWithAny(words, DeployCommands);
                    }
                }
            }
            if (builds)
                build.Add(job.Key);
            if (deploys)
                deploy.Add(job.Key);
            if (!builds && !deploys)
                other.Add(job.Key);
            else if (evidence.Count < context.Limits.MaxListItems)
                evidence.Add(Workflow.At(file.Path, job.KeyLine, job.Value.EndLine));
        }
        if (evidence.Count == 0)
            evidence.Add(Workflow.At(file.Path, root.StartLine, root.StartLine));

        var first = root.StartLine;
        context.Add(FactKind.Deployment, "deployment:workflow:" + file.Path, name?.Value.Text ?? file.Name, evidence,
        [
            ("source", "github-actions"), ("name", name?.Value.Text),
            ("triggers", context.List(triggers, file.Path, first, sorted: true)),
            ("jobs", context.List(jobs, file.Path, first)),
            ("buildJobs", context.List(build, file.Path, first)),
            ("deployJobs", context.List(deploy, file.Path, first)),
            ("otherJobs", context.List(other, file.Path, first)),
        ]);
    }

    private static bool StartsWithAny(List<string> words, string[][] commands)
    {
        foreach (var command in commands)
        {
            if (words.Count < command.Length)
                continue;
            var match = true;
            for (var i = 0; match && i < command.Length; i++)
                match = words[i].Equals(command[i], StringComparison.Ordinal);
            if (match)
                return true;
        }
        return false;
    }
}
