using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Vellum.Modules.Ai;
using Vellum.Modules.Costs;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.LoadTests;
using Vellum.Modules.Promises;
using Vellum.Modules.Review;
using Vellum.Modules.Workspaces;

#nullable disable

namespace Vellum.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.EnsureSchema(
                name: "settings");

            migrationBuilder.EnsureSchema(
                name: "import");

            migrationBuilder.EnsureSchema(
                name: "drafts");

            migrationBuilder.EnsureSchema(
                name: "review");

            migrationBuilder.EnsureSchema(
                name: "ai");

            migrationBuilder.EnsureSchema(
                name: "docs");

            migrationBuilder.EnsureSchema(
                name: "modelling");

            migrationBuilder.EnsureSchema(
                name: "costs");

            migrationBuilder.EnsureSchema(
                name: "promises");

            migrationBuilder.EnsureSchema(
                name: "workspaces");

            migrationBuilder.EnsureSchema(
                name: "views");

            migrationBuilder.EnsureSchema(
                name: "stress");

            migrationBuilder.EnsureSchema(
                name: "loadtests");

            migrationBuilder.EnsureSchema(
                name: "trace");

            migrationBuilder.EnsureSchema(
                name: "schemas");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:ai.ai_call_outcome", "abandoned,cancelled,failed,invalid_output,provider_error,rate_limited,refused,started,succeeded,timed_out,truncated")
                .Annotation("Npgsql:Enum:ai.ai_provider_kind", "anthropic,gemini,openai,openai_compatible")
                .Annotation("Npgsql:Enum:ai.ai_usage_note", "cost_estimated,reaped,schema_rejected_fallback,usage_out_of_range")
                .Annotation("Npgsql:Enum:ai.ai_usage_task", "critique,explain,infer_architecture,propose_flow,test")
                .Annotation("Npgsql:Enum:ai.design_proposal_kind", "architecture,flow")
                .Annotation("Npgsql:Enum:ai.design_proposal_status", "accepted,decided,draft,rejected")
                .Annotation("Npgsql:Enum:costs.price_source_kind", "custom,fetched")
                .Annotation("Npgsql:Enum:identity.agent_scope", "export,propose,read,scan_pull_request")
                .Annotation("Npgsql:Enum:import.coverage_gap", "failed,limit,scope,withheld")
                .Annotation("Npgsql:Enum:import.entity_kind", "binding,channel,element,message,operation,schema,subscription")
                .Annotation("Npgsql:Enum:import.fact_disposition", "blocked,evidence_only,folded,mapped")
                .Annotation("Npgsql:Enum:import.fact_kind", "channel,data_access,data_store,dependency,deployment,message,operation,pattern,publication,schema,service,subscription")
                .Annotation("Npgsql:Enum:import.proposal_action", "add,change,conflict,relink,remove")
                .Annotation("Npgsql:Enum:import.proposal_status", "accepted,pending,rejected,superseded")
                .Annotation("Npgsql:Enum:import.scan_file_state", "pending,read,skipped")
                .Annotation("Npgsql:Enum:import.scan_status", "cancelled,completed,failed,queued,running,waiting")
                .Annotation("Npgsql:Enum:import.subject", "fact,repository")
                .Annotation("Npgsql:Enum:loadtests.target_environment", "development,production,staging,test")
                .Annotation("Npgsql:Enum:promises.impact_gate_policy", "fail_on_broken,off")
                .Annotation("Npgsql:Enum:promises.promise_lifecycle", "approved,draft,retired")
                .Annotation("Npgsql:Enum:promises.promise_origin", "agent_suggested,ai_suggested,human")
                .Annotation("Npgsql:Enum:promises.run_result", "failed,not_run,passed,unsupported")
                .Annotation("Npgsql:Enum:review.decision_status", "accepted,proposed,rejected")
                .Annotation("Npgsql:Enum:review.disposition_status", "wont_fix")
                .Annotation("Npgsql:Enum:review.question_origin", "agent_suggested,ai_suggested,human")
                .Annotation("Npgsql:Enum:review.question_status", "answered,dropped,open")
                .Annotation("Npgsql:Enum:workspaces.invitation_status", "accepted,declined,expired,pending,revoked")
                .Annotation("Npgsql:Enum:workspaces.workspace_role", "editor,owner,viewer");

            migrationBuilder.CreateTable(
                name: "agent_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    scopes = table.Column<AgentScope[]>(type: "identity.agent_scope[]", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    master_key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_refused_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_entries",
                schema: "settings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    target = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "blobs",
                schema: "import",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blob_sha = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: true),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    withheld_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blobs", x => new { x.workspace_id, x.blob_sha });
                });

            migrationBuilder.CreateTable(
                name: "critiques",
                schema: "review",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    question = table.Column<string>(type: "text", nullable: false),
                    document = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_critiques", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                schema: "settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "decision_numbering",
                schema: "review",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decision_numbering", x => x.project_id);
                });

            migrationBuilder.CreateTable(
                name: "decisions",
                schema: "review",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<StoredDecisionStatus>(type: "review.decision_status", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "design_proposals",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<DesignProposalKind>(type: "ai.design_proposal_kind", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<DesignProposalStatus>(type: "ai.design_proposal_status", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    document = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    origin = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_design_proposals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dispositions",
                schema: "review",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_version = table.Column<int>(type: "integer", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<StoredDispositionStatus>(type: "review.disposition_status", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dispositions", x => new { x.project_id, x.rule_id, x.subject_id });
                });

            migrationBuilder.CreateTable(
                name: "drafts",
                schema: "drafts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fork_version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    merged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    abandoned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    base_snapshot = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drafts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "elements",
                schema: "modelling",
                columns: table => new
                {
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    technology = table.Column<string>(type: "text", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tags = table.Column<string[]>(type: "text[]", nullable: false),
                    icon = table.Column<string>(type: "text", nullable: true),
                    fact_status = table.Column<string>(type: "text", nullable: false),
                    provenance_source = table.Column<string>(type: "text", nullable: true),
                    provenance_revision = table.Column<string>(type: "text", nullable: true),
                    provenance_confidence = table.Column<double>(type: "double precision", nullable: true),
                    provenance_accepted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    provenance_accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provenance_accepted_at_version = table.Column<long>(type: "bigint", nullable: true),
                    provenance_accepted_on_branch = table.Column<Guid>(type: "uuid", nullable: true),
                    stale_since_revision = table.Column<string>(type: "text", nullable: true),
                    stale_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_elements", x => x.row_id);
                });

            migrationBuilder.CreateTable(
                name: "estimates",
                schema: "costs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    model_version = table.Column<long>(type: "bigint", nullable: false),
                    as_of = table.Column<DateOnly>(type: "date", nullable: false),
                    inputs_fingerprint = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "text", nullable: false),
                    inputs = table.Column<string>(type: "jsonb", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: false),
                    saved_by = table.Column<string>(type: "text", nullable: false),
                    saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_estimates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fact_links",
                schema: "import",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    repository = table.Column<string>(type: "text", nullable: false),
                    repository_id = table.Column<long>(type: "bigint", nullable: true),
                    subject = table.Column<Subject>(type: "import.subject", nullable: false, defaultValue: Subject.Fact),
                    fact_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    fact_key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_kind = table.Column<StoredEntityKind>(type: "import.entity_kind", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    commit_sha = table.Column<string>(type: "text", nullable: false),
                    rule_fingerprint = table.Column<string>(type: "text", nullable: true),
                    attributes = table.Column<string>(type: "jsonb", nullable: false),
                    consumer_keys = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    accepted = table.Column<string>(type: "jsonb", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    accepted_by = table.Column<string>(type: "text", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fact_links", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "impact_gates",
                schema: "promises",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy = table.Column<ImpactGatePolicy>(type: "promises.impact_gate_policy", nullable: false),
                    changed_by = table.Column<string>(type: "text", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_impact_gates", x => x.project_id);
                });

            migrationBuilder.CreateTable(
                name: "instance_admins",
                schema: "settings",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instance_admins", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "instance_settings",
                schema: "settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    policy = table.Column<string>(type: "jsonb", nullable: true),
                    defaults = table.Column<string>(type: "jsonb", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instance_settings", x => x.id);
                    table.CheckConstraint("ck_instance_settings_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "invitations",
                schema: "workspaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    role = table.Column<WorkspaceRole>(type: "workspaces.workspace_role", nullable: false),
                    status = table.Column<InvitationStatus>(type: "workspaces.invitation_status", nullable: false),
                    invited_by = table.Column<string>(type: "text", nullable: false),
                    secret_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    responded_by = table.Column<string>(type: "text", nullable: true),
                    responded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invitations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "memberships",
                schema: "workspaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<WorkspaceRole>(type: "workspaces.workspace_role", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_memberships", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "modelling",
                columns: table => new
                {
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    producer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    schema_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tags = table.Column<string[]>(type: "text[]", nullable: false),
                    fact_status = table.Column<string>(type: "text", nullable: false),
                    provenance_source = table.Column<string>(type: "text", nullable: true),
                    provenance_revision = table.Column<string>(type: "text", nullable: true),
                    provenance_confidence = table.Column<double>(type: "double precision", nullable: true),
                    provenance_accepted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    provenance_accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provenance_accepted_at_version = table.Column<long>(type: "bigint", nullable: true),
                    provenance_accepted_on_branch = table.Column<Guid>(type: "uuid", nullable: true),
                    stale_since_revision = table.Column<string>(type: "text", nullable: true),
                    stale_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.row_id);
                });

            migrationBuilder.CreateTable(
                name: "model_prices",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_kind = table.Column<AiProviderKind>(type: "ai.ai_provider_kind", nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    input_usd_per_million_tokens = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    output_usd_per_million_tokens = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    effective_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_prices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "numbering",
                schema: "promises",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prefix = table.Column<string>(type: "text", nullable: false),
                    next_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_numbering", x => x.project_id);
                });

            migrationBuilder.CreateTable(
                name: "price_entries",
                schema: "costs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    unit = table.Column<string>(type: "text", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    source = table.Column<PriceSourceKind>(type: "costs.price_source_kind", nullable: false),
                    accepted_by = table.Column<string>(type: "text", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    meter_id = table.Column<string>(type: "text", nullable: true),
                    provider_effective_date = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "profiles",
                schema: "stress",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    document = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                schema: "workspaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "promises",
                schema: "promises",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requirement_id = table.Column<string>(type: "text", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    lifecycle = table.Column<StoredPromiseLifecycle>(type: "promises.promise_lifecycle", nullable: false),
                    origin = table.Column<StoredPromiseOrigin>(type: "promises.promise_origin", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false),
                    latest_runs = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promises", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "questions",
                schema: "review",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    about = table.Column<string>(type: "jsonb", nullable: false),
                    origin = table.Column<QuestionOrigin>(type: "review.question_origin", nullable: false),
                    status = table.Column<QuestionStatus>(type: "review.question_status", nullable: false),
                    answer = table.Column<string>(type: "text", nullable: true),
                    answered_by = table.Column<string>(type: "text", nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_questions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rejections",
                schema: "import",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    repository = table.Column<string>(type: "text", nullable: false),
                    repository_id = table.Column<long>(type: "bigint", nullable: true),
                    subject = table.Column<Subject>(type: "import.subject", nullable: false, defaultValue: Subject.Fact),
                    fact_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    fact_key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rejected_by = table.Column<string>(type: "text", nullable: false),
                    rejected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rejections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "relationships",
                schema: "modelling",
                columns: table => new
                {
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch = table.Column<Guid>(type: "uuid", nullable: false),
                    from_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: true),
                    technology = table.Column<string>(type: "text", nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    line_shape = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    fact_status = table.Column<string>(type: "text", nullable: false),
                    provenance_source = table.Column<string>(type: "text", nullable: true),
                    provenance_revision = table.Column<string>(type: "text", nullable: true),
                    provenance_confidence = table.Column<double>(type: "double precision", nullable: true),
                    provenance_accepted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    provenance_accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provenance_accepted_at_version = table.Column<long>(type: "bigint", nullable: true),
                    provenance_accepted_on_branch = table.Column<Guid>(type: "uuid", nullable: true),
                    stale_since_revision = table.Column<string>(type: "text", nullable: true),
                    stale_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_relationships", x => x.row_id);
                });

            migrationBuilder.CreateTable(
                name: "revisions",
                schema: "modelling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_revisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "runs",
                schema: "promises",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    promise_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    obligation_id = table.Column<string>(type: "text", nullable: false),
                    promise_revision = table.Column<int>(type: "integer", nullable: false),
                    result = table.Column<StoredRunResult>(type: "promises.run_result", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scan_charges",
                schema: "import",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    charged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_charges", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scans",
                schema: "import",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    repository = table.Column<string>(type: "text", nullable: false),
                    repository_id = table.Column<long>(type: "bigint", nullable: true),
                    repository_name = table.Column<string>(type: "text", nullable: true),
                    requested_ref = table.Column<string>(type: "text", nullable: true),
                    resolved_ref = table.Column<string>(type: "text", nullable: true),
                    commit_sha = table.Column<string>(type: "text", nullable: true),
                    pull_request_number = table.Column<int>(type: "integer", nullable: true),
                    pull_request = table.Column<string>(type: "jsonb", nullable: true),
                    repository_is_private = table.Column<bool>(type: "boolean", nullable: true),
                    include_paths = table.Column<string[]>(type: "text[]", nullable: false),
                    exclude_paths = table.Column<string[]>(type: "text[]", nullable: false),
                    limits = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<ScanStatus>(type: "import.scan_status", nullable: false),
                    tree_listed = table.Column<bool>(type: "boolean", nullable: false),
                    files_listed = table.Column<int>(type: "integer", nullable: false),
                    files_read = table.Column<int>(type: "integer", nullable: false),
                    files_skipped = table.Column<int>(type: "integer", nullable: false),
                    files_extracted = table.Column<int>(type: "integer", nullable: false),
                    bytes_read = table.Column<long>(type: "bigint", nullable: false),
                    partial = table.Column<bool>(type: "boolean", nullable: false),
                    is_complete = table.Column<bool>(type: "boolean", nullable: false),
                    extraction_gap = table.Column<StoredCoverageGap>(type: "import.coverage_gap", nullable: true),
                    coverage = table.Column<string>(type: "jsonb", nullable: true),
                    relinked = table.Column<int>(type: "integer", nullable: false),
                    needs_review = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "jsonb", nullable: false),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    internal_failures = table.Column<int>(type: "integer", nullable: false),
                    crashes = table.Column<int>(type: "integer", nullable: false),
                    manual_resumes = table.Column<int>(type: "integer", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_owner = table.Column<string>(type: "text", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_token = table.Column<long>(type: "bigint", nullable: false),
                    cancel_requested = table.Column<bool>(type: "boolean", nullable: false),
                    tracked_by = table.Column<string>(type: "text", nullable: true),
                    tracked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scenarios",
                schema: "trace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scenarios", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "schemas",
                schema: "schemas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schemas", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scripts",
                schema: "loadtests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hash = table.Column<string>(type: "text", nullable: false),
                    scenario_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_version = table.Column<long>(type: "bigint", nullable: false),
                    model_stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_version = table.Column<long>(type: "bigint", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_version = table.Column<int>(type: "integer", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_name = table.Column<string>(type: "text", nullable: false),
                    base_url = table.Column<string>(type: "text", nullable: false),
                    environment = table.Column<StoredTargetEnvironment>(type: "loadtests.target_environment", nullable: false),
                    authorised_by = table.Column<string>(type: "text", nullable: false),
                    authorised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    plan = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scripts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "secrets",
                schema: "settings",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    master_key_id = table.Column<string>(type: "text", nullable: false),
                    wrap_nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    wrapped_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    bound_origin = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_secrets", x => new { x.workspace_id, x.name });
                });

            migrationBuilder.CreateTable(
                name: "sheets",
                schema: "costs",
                columns: table => new
                {
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sheets", x => x.branch_id);
                });

            migrationBuilder.CreateTable(
                name: "snapshots",
                schema: "review",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch = table.Column<Guid>(type: "uuid", nullable: false),
                    model_version = table.Column<long>(type: "bigint", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    inputs_fingerprint = table.Column<string>(type: "text", nullable: false),
                    rule_set_version = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    findings = table.Column<string>(type: "jsonb", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshots", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "spaces",
                schema: "docs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_spaces", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "targets",
                schema: "loadtests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    removed = table.Column<bool>(type: "boolean", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_targets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tracked_refs",
                schema: "import",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    repository = table.Column<string>(type: "text", nullable: false),
                    repository_id = table.Column<long>(type: "bigint", nullable: true),
                    ref_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    commit_sha = table.Column<string>(type: "text", nullable: true),
                    scan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    set_by = table.Column<string>(type: "text", nullable: false),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tracked_refs", x => new { x.project_id, x.host, x.repository });
                });

            migrationBuilder.CreateTable(
                name: "usage_records",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_kind = table.Column<AiProviderKind>(type: "ai.ai_provider_kind", nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reported_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    task = table.Column<AiUsageTask>(type: "ai.ai_usage_task", nullable: false),
                    purpose = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    prompt_template_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: true),
                    output_tokens = table.Column<int>(type: "integer", nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    reserved_usd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    outcome = table.Column<AiCallOutcome>(type: "ai.ai_call_outcome", nullable: false),
                    usage_note = table.Column<AiUsageNote>(type: "ai.ai_usage_note", nullable: true),
                    error_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                    phone_number = table.Column<string>(type: "text", nullable: true),
                    phone_number_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "variants",
                schema: "modelling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fork_version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_variants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "views",
                schema: "views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    root_element_id = table.Column<Guid>(type: "uuid", nullable: true),
                    visible_element_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    active_lens = table.Column<string>(type: "text", nullable: true),
                    active_flow_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_views", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workspace_settings",
                schema: "settings",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    overrides = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_settings", x => x.workspace_id);
                });

            migrationBuilder.CreateTable(
                name: "workspaces",
                schema: "workspaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspaces", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "comments",
                schema: "drafts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    draft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entity_type = table.Column<string>(type: "text", nullable: true),
                    author = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comments", x => x.id);
                    table.ForeignKey(
                        name: "fk_comments_drafts_draft_id",
                        column: x => x.draft_id,
                        principalSchema: "drafts",
                        principalTable: "drafts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "runs",
                schema: "stress",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_version = table.Column<int>(type: "integer", nullable: false),
                    model_stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_version = table.Column<long>(type: "bigint", nullable: false),
                    scenario_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_version = table.Column<long>(type: "bigint", nullable: false),
                    seed = table.Column<string>(type: "text", nullable: false),
                    engine_version = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ran_by = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    profile = table.Column<string>(type: "text", nullable: false),
                    result = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_runs_profiles_profile_id",
                        column: x => x.profile_id,
                        principalSchema: "stress",
                        principalTable: "profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_claims",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    role_id = table.Column<string>(type: "text", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_role_claims_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "identity",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "proposals",
                schema: "import",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<ProposalAction>(type: "import.proposal_action", nullable: false),
                    entity_kind = table.Column<StoredEntityKind>(type: "import.entity_kind", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<Subject>(type: "import.subject", nullable: false, defaultValue: Subject.Fact),
                    fact_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    proposed = table.Column<string>(type: "jsonb", nullable: true),
                    current = table.Column<string>(type: "jsonb", nullable: true),
                    changes = table.Column<string>(type: "jsonb", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    attributes = table.Column<string>(type: "jsonb", nullable: false),
                    consumer_keys = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    depends_on = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    status = table.Column<ProposalStatus>(type: "import.proposal_status", nullable: false),
                    partial = table.Column<bool>(type: "boolean", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proposals", x => x.id);
                    table.ForeignKey(
                        name: "fk_proposals_scans_scan_id",
                        column: x => x.scan_id,
                        principalSchema: "import",
                        principalTable: "scans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scan_files",
                schema: "import",
                columns: table => new
                {
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    blob_sha = table.Column<string>(type: "text", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<ScanFileState>(type: "import.scan_file_state", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_files", x => new { x.scan_id, x.path_hash });
                    table.ForeignKey(
                        name: "fk_scan_files_scans_scan_id",
                        column: x => x.scan_id,
                        principalSchema: "import",
                        principalTable: "scans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "snapshots",
                schema: "import",
                columns: table => new
                {
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    repository = table.Column<string>(type: "text", nullable: false),
                    commit_sha = table.Column<string>(type: "text", nullable: false),
                    is_complete = table.Column<bool>(type: "boolean", nullable: false),
                    rule_versions = table.Column<string>(type: "jsonb", nullable: false),
                    rule_fingerprint = table.Column<string>(type: "text", nullable: true),
                    limits = table.Column<string>(type: "jsonb", nullable: false),
                    fact_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshots", x => x.scan_id);
                    table.ForeignKey(
                        name: "fk_snapshots_scans_scan_id",
                        column: x => x.scan_id,
                        principalSchema: "import",
                        principalTable: "scans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "results",
                schema: "loadtests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    script_id = table.Column<Guid>(type: "uuid", nullable: false),
                    script_hash = table.Column<string>(type: "text", nullable: false),
                    scenario_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_version = table.Column<long>(type: "bigint", nullable: false),
                    model_stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_version = table.Column<long>(type: "bigint", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment = table.Column<StoredTargetEnvironment>(type: "loadtests.target_environment", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    elapsed_ms = table.Column<double>(type: "double precision", nullable: true),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false),
                    imported_by = table.Column<string>(type: "text", nullable: false),
                    imported_via = table.Column<string>(type: "text", nullable: true),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_results", x => x.id);
                    table.ForeignKey(
                        name: "fk_results_scripts_script_id",
                        column: x => x.script_id,
                        principalSchema: "loadtests",
                        principalTable: "scripts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                schema: "docs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: true),
                    element_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    draft_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_documents_spaces_space_id",
                        column: x => x.space_id,
                        principalSchema: "docs",
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "user_claims",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_claims_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_logins",
                schema: "identity",
                columns: table => new
                {
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    provider_display_name = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_logins", x => new { x.login_provider, x.provider_key });
                    table.ForeignKey(
                        name: "fk_user_logins_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    role_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "identity",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_tokens",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_tokens", x => new { x.user_id, x.login_provider, x.name });
                    table.ForeignKey(
                        name: "fk_user_tokens_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "layout_edges",
                schema: "views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    view_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relationship_id = table.Column<Guid>(type: "uuid", nullable: false),
                    route_points = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_layout_edges", x => x.id);
                    table.ForeignKey(
                        name: "fk_layout_edges_views_view_id",
                        column: x => x.view_id,
                        principalSchema: "views",
                        principalTable: "views",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "layout_positions",
                schema: "views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    view_id = table.Column<Guid>(type: "uuid", nullable: false),
                    element_id = table.Column<Guid>(type: "uuid", nullable: false),
                    x = table.Column<double>(type: "double precision", nullable: false),
                    y = table.Column<double>(type: "double precision", nullable: false),
                    width = table.Column<double>(type: "double precision", nullable: true),
                    height = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_layout_positions", x => x.id);
                    table.ForeignKey(
                        name: "fk_layout_positions_views_view_id",
                        column: x => x.view_id,
                        principalSchema: "views",
                        principalTable: "views",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "snapshot_facts",
                schema: "import",
                columns: table => new
                {
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<FactKind>(type: "import.fact_kind", nullable: false),
                    fact_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    attributes = table.Column<string>(type: "jsonb", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    disposition = table.Column<FactDisposition>(type: "import.fact_disposition", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshot_facts", x => new { x.scan_id, x.seq });
                    table.ForeignKey(
                        name: "fk_snapshot_facts_snapshots_scan_id",
                        column: x => x.scan_id,
                        principalSchema: "import",
                        principalTable: "snapshots",
                        principalColumn: "scan_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_tokens_project_id",
                schema: "identity",
                table: "agent_tokens",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_tokens_workspace_id",
                schema: "identity",
                table: "agent_tokens",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_workspace_id_id",
                schema: "settings",
                table: "audit_entries",
                columns: new[] { "workspace_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_comments_draft_id",
                schema: "drafts",
                table: "comments",
                column: "draft_id");

            migrationBuilder.CreateIndex(
                name: "ix_critiques_project_id_created_at_id",
                schema: "review",
                table: "critiques",
                columns: new[] { "project_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_decisions_project_id_number",
                schema: "review",
                table: "decisions",
                columns: new[] { "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_design_proposals_project_id_kind_created_at_id",
                schema: "ai",
                table: "design_proposals",
                columns: new[] { "project_id", "kind", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_documents_element_id",
                schema: "docs",
                table: "documents",
                column: "element_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_project_id",
                schema: "docs",
                table: "documents",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_space_id",
                schema: "docs",
                table: "documents",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_drafts_project_id",
                schema: "drafts",
                table: "drafts",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_drafts_project_id_status",
                schema: "drafts",
                table: "drafts",
                columns: new[] { "project_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_elements_project_id_branch_id",
                schema: "modelling",
                table: "elements",
                columns: new[] { "project_id", "branch", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_elements_project_id_branch_parent_id",
                schema: "modelling",
                table: "elements",
                columns: new[] { "project_id", "branch", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_estimates_project_id_branch_id_saved_at",
                schema: "costs",
                table: "estimates",
                columns: new[] { "project_id", "branch_id", "saved_at" });

            migrationBuilder.CreateIndex(
                name: "ix_fact_links_project_id_entity_id",
                schema: "import",
                table: "fact_links",
                columns: new[] { "project_id", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_fact_links_project_id_host_repository_subject_fact_key_hash",
                schema: "import",
                table: "fact_links",
                columns: new[] { "project_id", "host", "repository", "subject", "fact_key_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invitations_normalized_email_status",
                schema: "workspaces",
                table: "invitations",
                columns: new[] { "normalized_email", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_invitations_workspace_id_normalized_email",
                schema: "workspaces",
                table: "invitations",
                columns: new[] { "workspace_id", "normalized_email" },
                unique: true,
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_layout_edges_view_id_relationship_id",
                schema: "views",
                table: "layout_edges",
                columns: new[] { "view_id", "relationship_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_layout_positions_view_id_element_id",
                schema: "views",
                table: "layout_positions",
                columns: new[] { "view_id", "element_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_memberships_workspace_id_user_id",
                schema: "workspaces",
                table: "memberships",
                columns: new[] { "workspace_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_project_id_branch_id",
                schema: "modelling",
                table: "messages",
                columns: new[] { "project_id", "branch", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_project_id_branch_producer_id",
                schema: "modelling",
                table: "messages",
                columns: new[] { "project_id", "branch", "producer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_model_prices_provider_kind_model_effective_from",
                schema: "ai",
                table: "model_prices",
                columns: new[] { "provider_kind", "model", "effective_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_price_entries_workspace_id_provider_sku_id",
                schema: "costs",
                table: "price_entries",
                columns: new[] { "workspace_id", "provider", "sku", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_profiles_project_id_name",
                schema: "stress",
                table: "profiles",
                columns: new[] { "project_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_projects_workspace_id",
                schema: "workspaces",
                table: "projects",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_promises_project_id_number",
                schema: "promises",
                table: "promises",
                columns: new[] { "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_proposals_scan_id_seq",
                schema: "import",
                table: "proposals",
                columns: new[] { "scan_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_proposals_scan_id_status",
                schema: "import",
                table: "proposals",
                columns: new[] { "scan_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_questions_project_id_created_at_id",
                schema: "review",
                table: "questions",
                columns: new[] { "project_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_rejections_project_id_host_repository_subject_fact_key_hash",
                schema: "import",
                table: "rejections",
                columns: new[] { "project_id", "host", "repository", "subject", "fact_key_hash", "fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_relationships_project_id_branch_id",
                schema: "modelling",
                table: "relationships",
                columns: new[] { "project_id", "branch", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_results_project_id_content_hash",
                schema: "loadtests",
                table: "results",
                columns: new[] { "project_id", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_results_project_id_scenario_id_target_id_started_at",
                schema: "loadtests",
                table: "results",
                columns: new[] { "project_id", "scenario_id", "target_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_results_script_id_started_at_elapsed_ms",
                schema: "loadtests",
                table: "results",
                columns: new[] { "script_id", "started_at", "elapsed_ms" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_revisions_project_id_name",
                schema: "modelling",
                table: "revisions",
                columns: new[] { "project_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_role_claims_role_id",
                schema: "identity",
                table: "role_claims",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_roles_normalized_name",
                schema: "identity",
                table: "roles",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_runs_promise_id_version",
                schema: "promises",
                table: "runs",
                columns: new[] { "promise_id", "version" });

            migrationBuilder.CreateIndex(
                name: "ix_runs_profile_id_at",
                schema: "stress",
                table: "runs",
                columns: new[] { "profile_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_runs_project_id",
                schema: "stress",
                table: "runs",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_scan_charges_workspace_id_charged_at",
                schema: "import",
                table: "scan_charges",
                columns: new[] { "workspace_id", "charged_at" });

            migrationBuilder.CreateIndex(
                name: "ix_scan_files_blob_sha",
                schema: "import",
                table: "scan_files",
                column: "blob_sha");

            migrationBuilder.CreateIndex(
                name: "ix_scan_files_scan_id_state",
                schema: "import",
                table: "scan_files",
                columns: new[] { "scan_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_scans_one_active_per_repository",
                schema: "import",
                table: "scans",
                columns: new[] { "project_id", "host", "repository" },
                unique: true,
                filter: "status IN ('queued', 'running', 'waiting')");

            migrationBuilder.CreateIndex(
                name: "ix_scans_project_id_created_at_id",
                schema: "import",
                table: "scans",
                columns: new[] { "project_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_scans_status_created_at",
                schema: "import",
                table: "scans",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_scans_workspace_id_claimed_at",
                schema: "import",
                table: "scans",
                columns: new[] { "workspace_id", "claimed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_scans_workspace_id_created_at",
                schema: "import",
                table: "scans",
                columns: new[] { "workspace_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_scenarios_project_id_name",
                schema: "trace",
                table: "scenarios",
                columns: new[] { "project_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_schemas_project_id",
                schema: "schemas",
                table: "schemas",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_scripts_project_id_created_at",
                schema: "loadtests",
                table: "scripts",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_scripts_project_id_hash",
                schema: "loadtests",
                table: "scripts",
                columns: new[] { "project_id", "hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sheets_project_id",
                schema: "costs",
                table: "sheets",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_snapshot_facts_scan_id_disposition_seq",
                schema: "import",
                table: "snapshot_facts",
                columns: new[] { "scan_id", "disposition", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_project_id_host_repository_created_at",
                schema: "import",
                table: "snapshots",
                columns: new[] { "project_id", "host", "repository", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_project_id_created_at_id",
                schema: "review",
                table: "snapshots",
                columns: new[] { "project_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_spaces_project_id",
                schema: "docs",
                table: "spaces",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_targets_project_id_name",
                schema: "loadtests",
                table: "targets",
                columns: new[] { "project_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_workspace_id_provider_id_started_at",
                schema: "ai",
                table: "usage_records",
                columns: new[] { "workspace_id", "provider_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_workspace_id_started_at",
                schema: "ai",
                table: "usage_records",
                columns: new[] { "workspace_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_user_claims_user_id",
                schema: "identity",
                table: "user_claims",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_logins_user_id",
                schema: "identity",
                table: "user_logins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_role_id",
                schema: "identity",
                table: "user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_email",
                schema: "identity",
                table: "users",
                column: "normalized_email");

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_user_name",
                schema: "identity",
                table: "users",
                column: "normalized_user_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_variants_project_id_name",
                schema: "modelling",
                table: "variants",
                columns: new[] { "project_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_variants_stream_id",
                schema: "modelling",
                table: "variants",
                column: "stream_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_views_project_id",
                schema: "views",
                table: "views",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "audit_entries",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "blobs",
                schema: "import");

            migrationBuilder.DropTable(
                name: "comments",
                schema: "drafts");

            migrationBuilder.DropTable(
                name: "critiques",
                schema: "review");

            migrationBuilder.DropTable(
                name: "data_protection_keys",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "decision_numbering",
                schema: "review");

            migrationBuilder.DropTable(
                name: "decisions",
                schema: "review");

            migrationBuilder.DropTable(
                name: "design_proposals",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "dispositions",
                schema: "review");

            migrationBuilder.DropTable(
                name: "documents",
                schema: "docs");

            migrationBuilder.DropTable(
                name: "elements",
                schema: "modelling");

            migrationBuilder.DropTable(
                name: "estimates",
                schema: "costs");

            migrationBuilder.DropTable(
                name: "fact_links",
                schema: "import");

            migrationBuilder.DropTable(
                name: "impact_gates",
                schema: "promises");

            migrationBuilder.DropTable(
                name: "instance_admins",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "instance_settings",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "invitations",
                schema: "workspaces");

            migrationBuilder.DropTable(
                name: "layout_edges",
                schema: "views");

            migrationBuilder.DropTable(
                name: "layout_positions",
                schema: "views");

            migrationBuilder.DropTable(
                name: "memberships",
                schema: "workspaces");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "modelling");

            migrationBuilder.DropTable(
                name: "model_prices",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "numbering",
                schema: "promises");

            migrationBuilder.DropTable(
                name: "price_entries",
                schema: "costs");

            migrationBuilder.DropTable(
                name: "projects",
                schema: "workspaces");

            migrationBuilder.DropTable(
                name: "promises",
                schema: "promises");

            migrationBuilder.DropTable(
                name: "proposals",
                schema: "import");

            migrationBuilder.DropTable(
                name: "questions",
                schema: "review");

            migrationBuilder.DropTable(
                name: "rejections",
                schema: "import");

            migrationBuilder.DropTable(
                name: "relationships",
                schema: "modelling");

            migrationBuilder.DropTable(
                name: "results",
                schema: "loadtests");

            migrationBuilder.DropTable(
                name: "revisions",
                schema: "modelling");

            migrationBuilder.DropTable(
                name: "role_claims",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "runs",
                schema: "promises");

            migrationBuilder.DropTable(
                name: "runs",
                schema: "stress");

            migrationBuilder.DropTable(
                name: "scan_charges",
                schema: "import");

            migrationBuilder.DropTable(
                name: "scan_files",
                schema: "import");

            migrationBuilder.DropTable(
                name: "scenarios",
                schema: "trace");

            migrationBuilder.DropTable(
                name: "schemas",
                schema: "schemas");

            migrationBuilder.DropTable(
                name: "secrets",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "sheets",
                schema: "costs");

            migrationBuilder.DropTable(
                name: "snapshot_facts",
                schema: "import");

            migrationBuilder.DropTable(
                name: "snapshots",
                schema: "review");

            migrationBuilder.DropTable(
                name: "targets",
                schema: "loadtests");

            migrationBuilder.DropTable(
                name: "tracked_refs",
                schema: "import");

            migrationBuilder.DropTable(
                name: "usage_records",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "user_claims",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_logins",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "variants",
                schema: "modelling");

            migrationBuilder.DropTable(
                name: "workspace_settings",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "workspaces",
                schema: "workspaces");

            migrationBuilder.DropTable(
                name: "drafts",
                schema: "drafts");

            migrationBuilder.DropTable(
                name: "spaces",
                schema: "docs");

            migrationBuilder.DropTable(
                name: "views",
                schema: "views");

            migrationBuilder.DropTable(
                name: "scripts",
                schema: "loadtests");

            migrationBuilder.DropTable(
                name: "profiles",
                schema: "stress");

            migrationBuilder.DropTable(
                name: "snapshots",
                schema: "import");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "users",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "scans",
                schema: "import");
        }
    }
}
