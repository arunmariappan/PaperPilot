using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "papers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    arxiv_id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    authors = table.Column<List<string>>(type: "text[]", nullable: false),
                    @abstract = table.Column<string>(name: "abstract", type: "text", nullable: false),
                    categories = table.Column<List<string>>(type: "text[]", nullable: false),
                    published_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    pdf_url = table.Column<string>(type: "text", nullable: false),
                    raw_text = table.Column<string>(type: "text", nullable: true),
                    references = table.Column<List<string>>(type: "text[]", nullable: true),
                    parser_used = table.Column<string>(type: "text", nullable: true),
                    parser_metadata = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    pdf_processed = table.Column<bool>(type: "boolean", nullable: false),
                    pdf_processing_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sections = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_papers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_papers_arxiv_id",
                table: "papers",
                column: "arxiv_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "papers");
        }
    }
}
