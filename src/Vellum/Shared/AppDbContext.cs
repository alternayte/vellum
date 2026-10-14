using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Npgsql.NameTranslation;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Vellum.Modules.Ai.Design;
using Vellum.Modules.Ai;
using Vellum.Modules.Costs;
using Vellum.Modules.Docs;
using Vellum.Modules.Drafts;
using Vellum.Modules.Identity;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.LoadTests;
using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;
using Vellum.Modules.Review;
using Vellum.Modules.Schemas;
using Vellum.Modules.Settings;
using Vellum.Modules.Stress;
using Vellum.Modules.Trace;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;
using DesignProposalEntity = Vellum.Modules.Ai.ProposalEntity;
using ImportProposalEntity = Vellum.Modules.Import.ProposalEntity;
using ImportSnapshotEntity = Vellum.Modules.Import.SnapshotEntity;
using ImportSnapshotFactEntity = Vellum.Modules.Import.SnapshotFactEntity;
using FactLinkEntity = Vellum.Modules.Import.FactLinkEntity;
using RejectionEntity = Vellum.Modules.Import.RejectionEntity;
using ReviewSnapshotEntity = Vellum.Modules.Review.SnapshotEntity;

namespace Vellum.Shared;

/// <summary>
/// The one DbContext of the app. Each module keeps its own Postgres schema and configures
/// its own tables (<c>*Tables.Configure</c> in the module's folder); this class only lists
/// them. It derives from the Identity context, which brings the user and role tables.
/// The Deedbox event store keeps its tables outside this model, in the schema <c>deedbox</c>.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    // identity
    public DbSet<AgentTokenEntity> AgentTokens => Set<AgentTokenEntity>();

    // workspaces
    public DbSet<WorkspaceEntity> Workspaces => Set<WorkspaceEntity>();
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();
    public DbSet<MembershipEntity> Memberships => Set<MembershipEntity>();
    public DbSet<InvitationEntity> Invitations => Set<InvitationEntity>();

    // modelling
    public DbSet<ElementEntity> Elements => Set<ElementEntity>();
    public DbSet<RelationshipEntity> Relationships => Set<RelationshipEntity>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<RevisionEntity> Revisions => Set<RevisionEntity>();
    public DbSet<VariantEntity> Variants => Set<VariantEntity>();

    // views
    public DbSet<ViewEntity> Views => Set<ViewEntity>();
    public DbSet<LayoutPositionEntity> LayoutPositions => Set<LayoutPositionEntity>();
    public DbSet<LayoutEdgeEntity> LayoutEdges => Set<LayoutEdgeEntity>();

    // docs
    public DbSet<SpaceEntity> Spaces => Set<SpaceEntity>();
    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();

    // drafts
    public DbSet<DraftEntity> Drafts => Set<DraftEntity>();
    public DbSet<CommentEntity> Comments => Set<CommentEntity>();

    // schemas
    public DbSet<SchemaEntity> Schemas => Set<SchemaEntity>();

    // trace
    public DbSet<ScenarioEntity> Scenarios => Set<ScenarioEntity>();

    // settings
    public DbSet<InstanceSettingsEntity> InstanceSettings => Set<InstanceSettingsEntity>();
    public DbSet<WorkspaceSettingsEntity> WorkspaceSettings => Set<WorkspaceSettingsEntity>();
    public DbSet<SecretEntity> Secrets => Set<SecretEntity>();
    public DbSet<AuditEntryEntity> AuditEntries => Set<AuditEntryEntity>();
    public DbSet<InstanceAdminEntity> InstanceAdmins => Set<InstanceAdminEntity>();
    public DbSet<DataProtectionKeyEntity> DataProtectionKeys => Set<DataProtectionKeyEntity>();

    // ai
    public DbSet<UsageRecordEntity> UsageRecords => Set<UsageRecordEntity>();
    public DbSet<ModelPriceEntity> ModelPrices => Set<ModelPriceEntity>();
    public DbSet<DesignProposalEntity> DesignProposals => Set<DesignProposalEntity>();

    // import
    public DbSet<ScanEntity> Scans => Set<ScanEntity>();
    public DbSet<ScanFileEntity> ScanFiles => Set<ScanFileEntity>();
    public DbSet<BlobEntity> Blobs => Set<BlobEntity>();
    public DbSet<ScanChargeEntity> ScanCharges => Set<ScanChargeEntity>();
    public DbSet<TrackedRefEntity> TrackedRefs => Set<TrackedRefEntity>();
    public DbSet<ImportSnapshotEntity> ImportSnapshots => Set<ImportSnapshotEntity>();
    public DbSet<ImportSnapshotFactEntity> SnapshotFacts => Set<ImportSnapshotFactEntity>();
    public DbSet<ImportProposalEntity> ImportProposals => Set<ImportProposalEntity>();
    public DbSet<FactLinkEntity> FactLinks => Set<FactLinkEntity>();
    public DbSet<RejectionEntity> Rejections => Set<RejectionEntity>();

    // promises
    public DbSet<PromiseEntity> Promises => Set<PromiseEntity>();
    public DbSet<RunEntity> PromiseRuns => Set<RunEntity>();
    public DbSet<NumberingEntity> Numbering => Set<NumberingEntity>();
    public DbSet<ImpactGateEntity> ImpactGates => Set<ImpactGateEntity>();

    // stress
    public DbSet<StressProfileEntity> Profiles => Set<StressProfileEntity>();
    public DbSet<StressRunEntity> StressRuns => Set<StressRunEntity>();

    // costs
    public DbSet<SheetEntity> Sheets => Set<SheetEntity>();
    public DbSet<PriceEntryEntity> PriceEntries => Set<PriceEntryEntity>();
    public DbSet<SavedEstimateEntity> Estimates => Set<SavedEstimateEntity>();

    // review
    public DbSet<DecisionEntity> Decisions => Set<DecisionEntity>();
    public DbSet<DecisionNumberingEntity> DecisionNumbering => Set<DecisionNumberingEntity>();
    public DbSet<DispositionEntity> Dispositions => Set<DispositionEntity>();
    public DbSet<ReviewSnapshotEntity> ReviewSnapshots => Set<ReviewSnapshotEntity>();
    public DbSet<QuestionEntity> Questions => Set<QuestionEntity>();
    public DbSet<CritiqueEntity> Critiques => Set<CritiqueEntity>();

    // loadtests
    public DbSet<TargetEntity> Targets => Set<TargetEntity>();
    public DbSet<ScriptEntity> Scripts => Set<ScriptEntity>();
    public DbSet<ResultEntity> Results => Set<ResultEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        IdentityTables.Configure(modelBuilder);
        WorkspacesTables.Configure(modelBuilder);
        ModellingTables.Configure(modelBuilder);
        ViewsTables.Configure(modelBuilder);
        DocsTables.Configure(modelBuilder);
        DraftsTables.Configure(modelBuilder);
        SchemasTables.Configure(modelBuilder);
        TraceTables.Configure(modelBuilder);
        SettingsTables.Configure(modelBuilder);
        AiTables.Configure(modelBuilder);
        ImportTables.Configure(modelBuilder);
        PromisesTables.Configure(modelBuilder);
        StressTables.Configure(modelBuilder);
        CostsTables.Configure(modelBuilder);
        ReviewTables.Configure(modelBuilder);
        LoadTestsTables.Configure(modelBuilder);
    }
}

/// <summary>
/// The database options of <see cref="AppDbContext"/>, in one place for the app, <c>dotnet ef</c>
/// and the tests: Npgsql, snake_case names, and the native Postgres enum types of the modules.
/// </summary>
public static class AppDatabase
{
    /// <summary>The labels of every Postgres enum type: the member names in snake_case.</summary>
    public static readonly INpgsqlNameTranslator EnumLabels = new NpgsqlSnakeCaseNameTranslator();

    public static TBuilder UseAppDatabase<TBuilder>(this TBuilder options, string? connectionString)
        where TBuilder : DbContextOptionsBuilder
    {
        options.UseNpgsql(connectionString, MapEnums).UseSnakeCaseNamingConvention();
        return options;
    }

    /// <summary>Each module maps the enums of its tables to enum types in its own schema.</summary>
    private static void MapEnums(NpgsqlDbContextOptionsBuilder npgsql)
    {
        WorkspacesTables.MapEnums(npgsql);
    }
}
