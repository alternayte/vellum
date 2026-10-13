namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// What the .NET rules keep of a scan between reading a file and writing facts. The tokens of
/// a file are dropped when the file is read; these records are linear in the size of the file.
/// </summary>
internal sealed partial class DotNetExtractor
{
    private sealed record Site(string Path, int Start, int End);

    private sealed class Project(string path, string directory, string name, string id)
    {
        public string Path { get; } = path;
        public string Directory { get; } = directory;
        /// <summary>The display name: the project file name without its extension.</summary>
        public string Name { get; } = name;
        /// <summary><see cref="ExtractionContext.ProjectId"/>: the part of every key of the project.</summary>
        public string Id { get; } = id;
        /// <summary>False for a project file that does not parse. Its source files have no owner.</summary>
        public bool IsReadable { get; set; }
        public string? ServiceKey { get; set; }
        public bool UsesMassTransit { get; set; }
        /// <summary>The brokers of the MassTransit transport packages of the project.</summary>
        public HashSet<string> Transports { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EfStores { get; } = new(StringComparer.Ordinal);
        public HashSet<string> GlobalUsings { get; } = new(StringComparer.Ordinal);
        public List<FileModel> Files { get; } = [];

        public List<EndpointSite> Endpoints { get; } = [];
        public List<PublishSite> Publishes { get; } = [];
        public List<EntityNameSite> EntityNames { get; } = [];
        public List<OutboxCall> OutboxCalls { get; } = [];

        /// <summary>
        /// The broker kind in a channel key: the one transport package of the project, else
        /// <c>masstransit</c> (no transport package, or more than one: the broker is not known).
        /// </summary>
        public string Broker => Transports.Count == 1 ? Transports.First() : "masstransit";

        public IEnumerable<(string, string?)> Owner => [("service", ServiceKey), ("project", Path)];
    }

    private sealed class FileModel(SourceText file, Project project)
    {
        public SourceText File { get; } = file;
        public Project Project { get; } = project;
        public HashSet<string> Usings { get; set; } = [];
        public HashSet<string> Aliases { get; set; } = [];
        public List<TypeInfo> Types { get; } = [];
    }

    /// <summary>A type name as it is written at a use site, and what is visible there.</summary>
    /// <param name="Written">Identifiers joined with dots.</param>
    /// <param name="Container">The namespace and enclosing types of the use site.</param>
    private sealed record TypeRef(string Written, string Container, FileModel File, int Line)
    {
        public string Simple => Written[(Written.LastIndexOf('.') + 1)..];
    }

    private enum Template { None, Static, Dynamic }

    private sealed record RouteTemplate(Template Kind, string Value, string Path, int Line);

    private sealed record VerbUse(string Verb, Template Kind, string Value, int Line);

    private sealed record ActionInfo(string Name, Site Site, List<VerbUse> Verbs, List<RouteTemplate> Routes, string? Conditional);

    private sealed record DbSetInfo(string Set, string Entity, Site Site);

    /// <summary>What a hosted service class does with typed receivers: the first line of each kind of access.</summary>
    private sealed class HostedInfo
    {
        /// <summary>"receiver.Member" that is not a call and not followed by a write, by declared type of the receiver and member name.</summary>
        public Dictionary<(string Type, string Member), int> Reads { get; } = [];
        /// <summary>"receiver.Set&lt;Entity&gt;()" that is not followed by a write, by declared type of the receiver and entity.</summary>
        public Dictionary<(string Type, string Entity), int> SetCalls { get; } = [];
        /// <summary>The line of the first call of a recognised publish, send or produce API, or 0.</summary>
        public int PublishLine { get; set; }
    }

    /// <summary>One declaration of a type: one part of it when the type is partial.</summary>
    private sealed class TypeInfo(FileModel file, CSharpType type, Site header, Site whole, int line, string? conditional)
    {
        public FileModel File { get; } = file;
        public string Keyword { get; } = type.Keyword;
        public string Name { get; } = type.Name;
        public string Container { get; } = type.Container;
        public string QualifiedName { get; } = type.Container.Length == 0 ? type.Name : type.Container + "." + type.Name;
        public bool IsPartial { get; } = type.IsPartial;
        public bool IsAbstract { get; } = type.IsAbstract;
        public bool IsStatic { get; } = type.IsStatic;
        /// <summary>From the first attribute or modifier to the opening brace.</summary>
        public Site Header { get; } = header;
        public Site Whole { get; } = whole;
        public int Line { get; } = line;
        public string? Conditional { get; } = conditional;

        public List<(string Name, TypeRef Ref)> Bases { get; } = [];
        public bool IsApiController { get; set; }
        public bool HasTooManyRoutes { get; set; }
        public List<RouteTemplate> Routes { get; } = [];
        public List<ActionInfo> Actions { get; } = [];
        /// <summary>The names of the methods the declaration has, for a class with a base list.</summary>
        public HashSet<string>? Methods { get; set; }
        public List<DbSetInfo> DbSets { get; } = [];
        /// <summary>"Name:Type" of positional parameters and public properties, at most one more than a list attribute holds.</summary>
        public List<string> Fields { get; } = [];
        /// <summary>The message types of the <c>IConsumer&lt;T&gt;</c> interfaces of the declaration.</summary>
        public List<TypeRef> Consumes { get; } = [];
        public HostedInfo? Hosted { get; set; }
    }

    private sealed record EndpointSite(string Queue, Site Site, string? Conditional, FileModel File)
    {
        public List<TypeRef> Consumers { get; } = [];
    }

    /// <summary>The receive endpoints that configure one consumer type.</summary>
    private sealed class ConsumerEndpoints(string channel)
    {
        public string Channel { get; } = channel;
        public bool HasOneQueue { get; set; } = true;
        public List<Site> Sites { get; } = [];
    }

    private sealed record PublishSite(TypeRef Message, string Mode, Site Site, string? Conditional);

    private sealed record EntityNameSite(TypeRef Message, string Entity, Site Site, string? Conditional);

    private sealed record OutboxCall(string Call, string? DbContext, Site Site, string? Conditional);

    /// <summary>A type of one project: one declaration, or the parts of a partial type.</summary>
    private sealed class LogicalType(Project project, TypeInfo first)
    {
        public Project Project { get; } = project;
        public string Name { get; } = first.Name;
        public string Container { get; } = first.Container;
        public string QualifiedName { get; } = first.QualifiedName;
        public string Keyword { get; } = first.Keyword;
        public List<TypeInfo> Parts { get; } = [first];

        /// <summary>False for two declarations of one name that are not parts of one partial type: which one a name means is not known.</summary>
        public bool IsConsistent { get; set; } = true;

        /// <summary>A partial type with one part in the scan: the other parts, and what they declare, are not known.</summary>
        public bool IsIncomplete => Parts.Count == 1 && Parts[0].IsPartial;
    }

    /// <summary>
    /// Every type declaration of the scan by simple name, and the rule that resolves a written
    /// type name to one of them.
    /// <para>The candidates for a name are the types with that simple name whose container (namespace
    /// and enclosing types) is visible at the use site:</para>
    /// <list type="bullet">
    /// <item>an unqualified name: the global namespace, the container of the use site and its
    /// parents, the namespaces of the <c>using</c> directives of the file, and of the
    /// <c>global using</c> directives of any file of the same project;</item>
    /// <item>a qualified name <c>Q.Name</c>: the container <c>Q</c>, or <c>P.Q</c> where <c>P</c> is the container of the use site or a parent of it.</item>
    /// </list>
    /// Exactly one candidate that is declared consistently is the answer. None, several, a name
    /// that is a <c>using</c> alias of the file, or a simple name with more than
    /// <see cref="MaxCandidates"/> declarations in the scan: no answer.
    /// </summary>
    private sealed class TypeIndex
    {
        public const int MaxCandidates = 32;

        private readonly Dictionary<string, List<LogicalType>> _bySimpleName = new(StringComparer.Ordinal);

        /// <summary>In project, file and source order.</summary>
        public List<LogicalType> All { get; } = [];

        /// <summary>The passes of the rules over all types of the scan, charged here to the file of each type.</summary>
        private const int PassesOverTypes = 8;

        public TypeIndex(ExtractionContext context, IEnumerable<Project> projects)
        {
            foreach (var project in projects)
            {
                var ofProject = new Dictionary<string, LogicalType>(StringComparer.Ordinal);
                foreach (var file in project.Files)
                {
                    // A file whose budget is spent is not in the index.
                    if (!context.Meter(file.File).Charge((long)PassesOverTypes * file.Types.Count))
                        continue;
                    foreach (var type in file.Types)
                    {
                        if (ofProject.TryGetValue(type.QualifiedName, out var logical))
                        {
                            logical.IsConsistent &= type.IsPartial && logical.Parts[0].IsPartial && type.Keyword == logical.Keyword;
                            logical.Parts.Add(type);
                            continue;
                        }
                        ofProject[type.QualifiedName] = logical = new LogicalType(project, type);
                        All.Add(logical);
                        if (!_bySimpleName.TryGetValue(type.Name, out var list))
                            _bySimpleName[type.Name] = list = [];
                        list.Add(logical);
                    }
                }
            }
        }

        public LogicalType? Resolve(TypeRef reference, WorkMeter meter)
        {
            var written = reference.Written;
            var dot = written.LastIndexOf('.');
            var qualifier = dot < 0 ? "" : written[..dot];
            var simple = written[(dot + 1)..];
            if (dot < 0 && reference.File.Aliases.Contains(simple))
                return null;
            if (!_bySimpleName.TryGetValue(simple, out var candidates) || candidates.Count > MaxCandidates)
                return null;
            LogicalType? found = null;
            foreach (var candidate in candidates)
            {
                if (!meter.Charge())
                    return null;
                if (!IsVisible(candidate.Container, qualifier, reference))
                    continue;
                if (found is not null)
                    return null;
                found = candidate;
            }
            return found is { IsConsistent: true } ? found : null;
        }

        private static bool IsVisible(string container, string qualifier, TypeRef reference)
        {
            if (qualifier.Length == 0)
                return container.Length == 0 || reference.File.Usings.Contains(container)
                    || reference.File.Project.GlobalUsings.Contains(container) || IsSameOrParent(container, reference.Container);
            if (container == qualifier)
                return true;
            // "P.Q" where P is around the use site.
            var prefix = container.Length - qualifier.Length - 1;
            return prefix > 0 && container[prefix] == '.' && container.EndsWith(qualifier, StringComparison.Ordinal)
                && IsSameOrParent(container.AsSpan(0, prefix), reference.Container);
        }

        private static bool IsSameOrParent(ReadOnlySpan<char> parent, string container) =>
            container.AsSpan().SequenceEqual(parent)
            || (container.Length > parent.Length && container[parent.Length] == '.' && container.AsSpan().StartsWith(parent, StringComparison.Ordinal));
    }
}
