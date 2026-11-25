using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoCapture.Api.FieldWork.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldWork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "geo_capture");

            migrationBuilder.CreateTable(
                name: "FieldWorks",
                schema: "geo_capture",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldWorks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FieldWorkSettings",
                schema: "geo_capture",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    AllowQrGeneration = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldWorkSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FieldWorkPhotos",
                schema: "geo_capture",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                    DescriptorJson = table.Column<string>(type: "text", nullable: false),
                    OriginalMimeType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OriginalBytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    StampedBytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    OriginalHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StampedHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldWorkPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FieldWorkPhotos_FieldWorks_WorkId",
                        column: x => x.WorkId,
                        principalSchema: "geo_capture",
                        principalTable: "FieldWorks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FieldWorkRevisions",
                schema: "geo_capture",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    ChangeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DocumentJson = table.Column<string>(type: "text", nullable: false),
                    ChangesJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldWorkRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FieldWorkRevisions_FieldWorks_WorkId",
                        column: x => x.WorkId,
                        principalSchema: "geo_capture",
                        principalTable: "FieldWorks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FieldWorkPhotos_WorkId",
                schema: "geo_capture",
                table: "FieldWorkPhotos",
                column: "WorkId");

            migrationBuilder.CreateIndex(
                name: "IX_FieldWorkRevisions_WorkId_ChangeId",
                schema: "geo_capture",
                table: "FieldWorkRevisions",
                columns: new[] { "WorkId", "ChangeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FieldWorkRevisions_WorkId_Number",
                schema: "geo_capture",
                table: "FieldWorkRevisions",
                columns: new[] { "WorkId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FieldWorks_OwnerId_UpdatedAtUtc",
                schema: "geo_capture",
                table: "FieldWorks",
                columns: new[] { "OwnerId", "UpdatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FieldWorkPhotos",
                schema: "geo_capture");

            migrationBuilder.DropTable(
                name: "FieldWorkRevisions",
                schema: "geo_capture");

            migrationBuilder.DropTable(
                name: "FieldWorkSettings",
                schema: "geo_capture");

            migrationBuilder.DropTable(
                name: "FieldWorks",
                schema: "geo_capture");
        }
    }
}