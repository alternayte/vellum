using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Migrations
{
    /// <inheritdoc />
    public partial class InboxProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:ai.ai_call_outcome", "abandoned,cancelled,failed,invalid_output,provider_error,rate_limited,refused,started,succeeded,timed_out,truncated")
                .Annotation("Npgsql:Enum:ai.ai_provider_kind", "anthropic,gemini,openai,openai_compatible")
                .Annotation("Npgsql:Enum:ai.ai_usage_note", "cost_estimated,reaped,schema_rejected_fallback,usage_out_of_range")
                .Annotation("Npgsql:Enum:ai.ai_usage_task", "critique,explain,infer_architecture,propose_flow,test")
                .Annotation("Npgsql:Enum:ai.design_proposal_kind", "architecture,flow")
                .Annotation("Npgsql:Enum:ai.design_proposal_status", "accepted,decided,draft,rejected")
                .Annotation("Npgsql:Enum:comments.delivery_status", "delivered,failed,pending")
                .Annotation("Npgsql:Enum:comments.inbox_reason", "mention,proposal,reply")
                .Annotation("Npgsql:Enum:comments.target_kind", "channel,decision,document,element,message,operation,project,promise,relationship,step")
                .Annotation("Npgsql:Enum:comments.thread_status", "open,resolved")
                .Annotation("Npgsql:Enum:costs.price_source_kind", "custom,fetched")
                .Annotation("Npgsql:Enum:drafts.draft_status", "abandoned,merged,open")
                .Annotation("Npgsql:Enum:identity.agent_scope", "export,propose,push,read,scan_pull_request")
                .Annotation("Npgsql:Enum:import.coverage_gap", "failed,limit,scope,withheld")
                .Annotation("Npgsql:Enum:import.entity_kind", "binding,channel,element,message,operation,relationship,schema,subscription")
                .Annotation("Npgsql:Enum:import.fact_disposition", "blocked,evidence_only,folded,mapped")
                .Annotation("Npgsql:Enum:import.fact_kind", "channel,data_access,data_store,decision,dependency,deployment,element,interaction,message,operation,pattern,publication,relationship,schema,service,subscription")
                .Annotation("Npgsql:Enum:import.proposal_action", "add,bind,change,conflict,relink,remove")
                .Annotation("Npgsql:Enum:import.proposal_status", "accepted,pending,rejected,superseded")
                .Annotation("Npgsql:Enum:import.scan_file_state", "pending,read,skipped")
                .Annotation("Npgsql:Enum:import.scan_status", "cancelled,completed,failed,queued,running,waiting")
                .Annotation("Npgsql:Enum:import.source_kind", "agent_push,archive,azure_devops,bitbucket,directory,github,gitlab,model_file,newrelic,traces")
                .Annotation("Npgsql:Enum:import.subject", "fact,repository")
                .Annotation("Npgsql:Enum:loadtests.target_environment", "development,production,staging,test")
                .Annotation("Npgsql:Enum:modelling.element_kind", "actor,app,component,store,system")
                .Annotation("Npgsql:Enum:modelling.element_status", "current,deprecated,planned,removed")
                .Annotation("Npgsql:Enum:modelling.fact_status", "declared,inferred,measured,observed_in_source,unknown")
                .Annotation("Npgsql:Enum:modelling.relationship_kind", "behavioural,structural")
                .Annotation("Npgsql:Enum:modelling.variant_status", "open")
                .Annotation("Npgsql:Enum:promises.impact_gate_policy", "fail_on_broken,off")
                .Annotation("Npgsql:Enum:promises.promise_lifecycle", "approved,draft,retired")
                .Annotation("Npgsql:Enum:promises.promise_origin", "agent_suggested,ai_suggested,human")
                .Annotation("Npgsql:Enum:promises.run_result", "failed,not_run,passed,unsupported")
                .Annotation("Npgsql:Enum:review.decision_status", "accepted,proposed,rejected")
                .Annotation("Npgsql:Enum:review.disposition_status", "wont_fix")
                .Annotation("Npgsql:Enum:review.question_origin", "agent_suggested,ai_suggested,human")
                .Annotation("Npgsql:Enum:review.question_status", "answered,dropped,open")
                .Annotation("Npgsql:Enum:workspaces.invitation_status", "accepted,declined,expired,pending,revoked")
                .Annotation("Npgsql:Enum:workspaces.workspace_role", "commenter,editor,owner,viewer")
                .OldAnnotation("Npgsql:Enum:ai.ai_call_outcome", "abandoned,cancelled,failed,invalid_output,provider_error,rate_limited,refused,started,succeeded,timed_out,truncated")
                .OldAnnotation("Npgsql:Enum:ai.ai_provider_kind", "anthropic,gemini,openai,openai_compatible")
                .OldAnnotation("Npgsql:Enum:ai.ai_usage_note", "cost_estimated,reaped,schema_rejected_fallback,usage_out_of_range")
                .OldAnnotation("Npgsql:Enum:ai.ai_usage_task", "critique,explain,infer_architecture,propose_flow,test")
                .OldAnnotation("Npgsql:Enum:ai.design_proposal_kind", "architecture,flow")
                .OldAnnotation("Npgsql:Enum:ai.design_proposal_status", "accepted,decided,draft,rejected")
                .OldAnnotation("Npgsql:Enum:comments.delivery_status", "delivered,failed,pending")
                .OldAnnotation("Npgsql:Enum:comments.inbox_reason", "mention,reply")
                .OldAnnotation("Npgsql:Enum:comments.target_kind", "channel,decision,document,element,message,operation,project,promise,relationship,step")
                .OldAnnotation("Npgsql:Enum:comments.thread_status", "open,resolved")
                .OldAnnotation("Npgsql:Enum:costs.price_source_kind", "custom,fetched")
                .OldAnnotation("Npgsql:Enum:drafts.draft_status", "abandoned,merged,open")
                .OldAnnotation("Npgsql:Enum:identity.agent_scope", "export,propose,push,read,scan_pull_request")
                .OldAnnotation("Npgsql:Enum:import.coverage_gap", "failed,limit,scope,withheld")
                .OldAnnotation("Npgsql:Enum:import.entity_kind", "binding,channel,element,message,operation,relationship,schema,subscription")
                .OldAnnotation("Npgsql:Enum:import.fact_disposition", "blocked,evidence_only,folded,mapped")
                .OldAnnotation("Npgsql:Enum:import.fact_kind", "channel,data_access,data_store,decision,dependency,deployment,element,interaction,message,operation,pattern,publication,relationship,schema,service,subscription")
                .OldAnnotation("Npgsql:Enum:import.proposal_action", "add,bind,change,conflict,relink,remove")
                .OldAnnotation("Npgsql:Enum:import.proposal_status", "accepted,pending,rejected,superseded")
                .OldAnnotation("Npgsql:Enum:import.scan_file_state", "pending,read,skipped")
                .OldAnnotation("Npgsql:Enum:import.scan_status", "cancelled,completed,failed,queued,running,waiting")
                .OldAnnotation("Npgsql:Enum:import.source_kind", "agent_push,archive,azure_devops,bitbucket,directory,github,gitlab,model_file,newrelic,traces")
                .OldAnnotation("Npgsql:Enum:import.subject", "fact,repository")
                .OldAnnotation("Npgsql:Enum:loadtests.target_environment", "development,production,staging,test")
                .OldAnnotation("Npgsql:Enum:modelling.element_kind", "actor,app,component,store,system")
                .OldAnnotation("Npgsql:Enum:modelling.element_status", "current,deprecated,planned,removed")
                .OldAnnotation("Npgsql:Enum:modelling.fact_status", "declared,inferred,measured,observed_in_source,unknown")
                .OldAnnotation("Npgsql:Enum:modelling.relationship_kind", "behavioural,structural")
                .OldAnnotation("Npgsql:Enum:modelling.variant_status", "open")
                .OldAnnotation("Npgsql:Enum:promises.impact_gate_policy", "fail_on_broken,off")
                .OldAnnotation("Npgsql:Enum:promises.promise_lifecycle", "approved,draft,retired")
                .OldAnnotation("Npgsql:Enum:promises.promise_origin", "agent_suggested,ai_suggested,human")
                .OldAnnotation("Npgsql:Enum:promises.run_result", "failed,not_run,passed,unsupported")
                .OldAnnotation("Npgsql:Enum:review.decision_status", "accepted,proposed,rejected")
                .OldAnnotation("Npgsql:Enum:review.disposition_status", "wont_fix")
                .OldAnnotation("Npgsql:Enum:review.question_origin", "agent_suggested,ai_suggested,human")
                .OldAnnotation("Npgsql:Enum:review.question_status", "answered,dropped,open")
                .OldAnnotation("Npgsql:Enum:workspaces.invitation_status", "accepted,declined,expired,pending,revoked")
                .OldAnnotation("Npgsql:Enum:workspaces.workspace_role", "commenter,editor,owner,viewer");

            migrationBuilder.AlterColumn<Guid>(
                name: "thread_id",
                schema: "comments",
                table: "inbox_items",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "comment_id",
                schema: "comments",
                table: "inbox_items",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "proposal_id",
                schema: "comments",
                table: "inbox_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_inbox_items_user_id_proposal_id",
                schema: "comments",
                table: "inbox_items",
                columns: new[] { "user_id", "proposal_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_inbox_items_user_id_proposal_id",
                schema: "comments",
                table: "inbox_items");

            migrationBuilder.DropColumn(
                name: "proposal_id",
                schema: "comments",
                table: "inbox_items");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:ai.ai_call_outcome", "abandoned,cancelled,failed,invalid_output,provider_error,rate_limited,refused,started,succeeded,timed_out,truncated")
                .Annotation("Npgsql:Enum:ai.ai_provider_kind", "anthropic,gemini,openai,openai_compatible")
                .Annotation("Npgsql:Enum:ai.ai_usage_note", "cost_estimated,reaped,schema_rejected_fallback,usage_out_of_range")
                .Annotation("Npgsql:Enum:ai.ai_usage_task", "critique,explain,infer_architecture,propose_flow,test")
                .Annotation("Npgsql:Enum:ai.design_proposal_kind", "architecture,flow")
                .Annotation("Npgsql:Enum:ai.design_proposal_status", "accepted,decided,draft,rejected")
                .Annotation("Npgsql:Enum:comments.delivery_status", "delivered,failed,pending")
                .Annotation("Npgsql:Enum:comments.inbox_reason", "mention,reply")
                .Annotation("Npgsql:Enum:comments.target_kind", "channel,decision,document,element,message,operation,project,promise,relationship,step")
                .Annotation("Npgsql:Enum:comments.thread_status", "open,resolved")
                .Annotation("Npgsql:Enum:costs.price_source_kind", "custom,fetched")
                .Annotation("Npgsql:Enum:drafts.draft_status", "abandoned,merged,open")
                .Annotation("Npgsql:Enum:identity.agent_scope", "export,propose,push,read,scan_pull_request")
                .Annotation("Npgsql:Enum:import.coverage_gap", "failed,limit,scope,withheld")
                .Annotation("Npgsql:Enum:import.entity_kind", "binding,channel,element,message,operation,relationship,schema,subscription")
                .Annotation("Npgsql:Enum:import.fact_disposition", "blocked,evidence_only,folded,mapped")
                .Annotation("Npgsql:Enum:import.fact_kind", "channel,data_access,data_store,decision,dependency,deployment,element,interaction,message,operation,pattern,publication,relationship,schema,service,subscription")
                .Annotation("Npgsql:Enum:import.proposal_action", "add,bind,change,conflict,relink,remove")
                .Annotation("Npgsql:Enum:import.proposal_status", "accepted,pending,rejected,superseded")
                .Annotation("Npgsql:Enum:import.scan_file_state", "pending,read,skipped")
                .Annotation("Npgsql:Enum:import.scan_status", "cancelled,completed,failed,queued,running,waiting")
                .Annotation("Npgsql:Enum:import.source_kind", "agent_push,archive,azure_devops,bitbucket,directory,github,gitlab,model_file,newrelic,traces")
                .Annotation("Npgsql:Enum:import.subject", "fact,repository")
                .Annotation("Npgsql:Enum:loadtests.target_environment", "development,production,staging,test")
                .Annotation("Npgsql:Enum:modelling.element_kind", "actor,app,component,store,system")
                .Annotation("Npgsql:Enum:modelling.element_status", "current,deprecated,planned,removed")
                .Annotation("Npgsql:Enum:modelling.fact_status", "declared,inferred,measured,observed_in_source,unknown")
                .Annotation("Npgsql:Enum:modelling.relationship_kind", "behavioural,structural")
                .Annotation("Npgsql:Enum:modelling.variant_status", "open")
                .Annotation("Npgsql:Enum:promises.impact_gate_policy", "fail_on_broken,off")
                .Annotation("Npgsql:Enum:promises.promise_lifecycle", "approved,draft,retired")
                .Annotation("Npgsql:Enum:promises.promise_origin", "agent_suggested,ai_suggested,human")
                .Annotation("Npgsql:Enum:promises.run_result", "failed,not_run,passed,unsupported")
                .Annotation("Npgsql:Enum:review.decision_status", "accepted,proposed,rejected")
                .Annotation("Npgsql:Enum:review.disposition_status", "wont_fix")
                .Annotation("Npgsql:Enum:review.question_origin", "agent_suggested,ai_suggested,human")
                .Annotation("Npgsql:Enum:review.question_status", "answered,dropped,open")
                .Annotation("Npgsql:Enum:workspaces.invitation_status", "accepted,declined,expired,pending,revoked")
                .Annotation("Npgsql:Enum:workspaces.workspace_role", "commenter,editor,owner,viewer")
                .OldAnnotation("Npgsql:Enum:ai.ai_call_outcome", "abandoned,cancelled,failed,invalid_output,provider_error,rate_limited,refused,started,succeeded,timed_out,truncated")
                .OldAnnotation("Npgsql:Enum:ai.ai_provider_kind", "anthropic,gemini,openai,openai_compatible")
                .OldAnnotation("Npgsql:Enum:ai.ai_usage_note", "cost_estimated,reaped,schema_rejected_fallback,usage_out_of_range")
                .OldAnnotation("Npgsql:Enum:ai.ai_usage_task", "critique,explain,infer_architecture,propose_flow,test")
                .OldAnnotation("Npgsql:Enum:ai.design_proposal_kind", "architecture,flow")
                .OldAnnotation("Npgsql:Enum:ai.design_proposal_status", "accepted,decided,draft,rejected")
                .OldAnnotation("Npgsql:Enum:comments.delivery_status", "delivered,failed,pending")
                .OldAnnotation("Npgsql:Enum:comments.inbox_reason", "mention,proposal,reply")
                .OldAnnotation("Npgsql:Enum:comments.target_kind", "channel,decision,document,element,message,operation,project,promise,relationship,step")
                .OldAnnotation("Npgsql:Enum:comments.thread_status", "open,resolved")
                .OldAnnotation("Npgsql:Enum:costs.price_source_kind", "custom,fetched")
                .OldAnnotation("Npgsql:Enum:drafts.draft_status", "abandoned,merged,open")
                .OldAnnotation("Npgsql:Enum:identity.agent_scope", "export,propose,push,read,scan_pull_request")
                .OldAnnotation("Npgsql:Enum:import.coverage_gap", "failed,limit,scope,withheld")
                .OldAnnotation("Npgsql:Enum:import.entity_kind", "binding,channel,element,message,operation,relationship,schema,subscription")
                .OldAnnotation("Npgsql:Enum:import.fact_disposition", "blocked,evidence_only,folded,mapped")
                .OldAnnotation("Npgsql:Enum:import.fact_kind", "channel,data_access,data_store,decision,dependency,deployment,element,interaction,message,operation,pattern,publication,relationship,schema,service,subscription")
                .OldAnnotation("Npgsql:Enum:import.proposal_action", "add,bind,change,conflict,relink,remove")
                .OldAnnotation("Npgsql:Enum:import.proposal_status", "accepted,pending,rejected,superseded")
                .OldAnnotation("Npgsql:Enum:import.scan_file_state", "pending,read,skipped")
                .OldAnnotation("Npgsql:Enum:import.scan_status", "cancelled,completed,failed,queued,running,waiting")
                .OldAnnotation("Npgsql:Enum:import.source_kind", "agent_push,archive,azure_devops,bitbucket,directory,github,gitlab,model_file,newrelic,traces")
                .OldAnnotation("Npgsql:Enum:import.subject", "fact,repository")
                .OldAnnotation("Npgsql:Enum:loadtests.target_environment", "development,production,staging,test")
                .OldAnnotation("Npgsql:Enum:modelling.element_kind", "actor,app,component,store,system")
                .OldAnnotation("Npgsql:Enum:modelling.element_status", "current,deprecated,planned,removed")
                .OldAnnotation("Npgsql:Enum:modelling.fact_status", "declared,inferred,measured,observed_in_source,unknown")
                .OldAnnotation("Npgsql:Enum:modelling.relationship_kind", "behavioural,structural")
                .OldAnnotation("Npgsql:Enum:modelling.variant_status", "open")
                .OldAnnotation("Npgsql:Enum:promises.impact_gate_policy", "fail_on_broken,off")
                .OldAnnotation("Npgsql:Enum:promises.promise_lifecycle", "approved,draft,retired")
                .OldAnnotation("Npgsql:Enum:promises.promise_origin", "agent_suggested,ai_suggested,human")
                .OldAnnotation("Npgsql:Enum:promises.run_result", "failed,not_run,passed,unsupported")
                .OldAnnotation("Npgsql:Enum:review.decision_status", "accepted,proposed,rejected")
                .OldAnnotation("Npgsql:Enum:review.disposition_status", "wont_fix")
                .OldAnnotation("Npgsql:Enum:review.question_origin", "agent_suggested,ai_suggested,human")
                .OldAnnotation("Npgsql:Enum:review.question_status", "answered,dropped,open")
                .OldAnnotation("Npgsql:Enum:workspaces.invitation_status", "accepted,declined,expired,pending,revoked")
                .OldAnnotation("Npgsql:Enum:workspaces.workspace_role", "commenter,editor,owner,viewer");

            migrationBuilder.AlterColumn<Guid>(
                name: "thread_id",
                schema: "comments",
                table: "inbox_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "comment_id",
                schema: "comments",
                table: "inbox_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
