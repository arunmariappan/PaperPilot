using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestionRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ingestion_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_from = table.Column<DateOnly>(type: "date", nullable: false),
                    target_to = table.Column<DateOnly>(type: "date", nullable: false),
                    trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    papers_fetched = table.Column<int>(type: "integer", nullable: false),
                    pdfs_downloaded = table.Column<int>(type: "integer", nullable: false),
                    pdfs_parsed = table.Column<int>(type: "integer", nullable: false),
                    pdfs_skipped = table.Column<int>(type: "integer", nullable: false),
                    papers_stored = table.Column<int>(type: "integer", nullable: false),
                    chunks_created = table.Column<int>(type: "integer", nullable: false),
                    chunks_indexed = table.Column<int>(type: "integer", nullable: false),
                    embeddings_generated = table.Column<int>(type: "integer", nullable: false),
                    errors = table.Column<List<string>>(type: "text[]", nullable: false),
                    index_doc_count_after = table.Column<long>(type: "bigint", nullable: true),
                    hangfire_job_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ingestion_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ingestion_runs_started_at",
                table: "ingestion_runs",
                column: "started_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ingestion_runs");
        }
    }
}
