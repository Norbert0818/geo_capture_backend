using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoCapture.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PhotoUploads",
                schema: "geo_capture",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientUploadId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadedByUsername = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SerialNumber = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    PayloadSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UploadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoUploads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhotoUploads_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalSchema: "geo_capture",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CapturedPhotos",
                schema: "geo_capture",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientPhotoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    AccuracyMeters = table.Column<double>(type: "double precision", nullable: false),
                    CoordinatesText = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LocationMeasuredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OriginalMimeType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OriginalWidth = table.Column<int>(type: "integer", nullable: false),
                    OriginalHeight = table.Column<int>(type: "integer", nullable: false),
                    StampedWidth = table.Column<int>(type: "integer", nullable: false),
                    StampedHeight = table.Column<int>(type: "integer", nullable: false),
                    OriginalByteLength = table.Column<long>(type: "bigint", nullable: false),
                    StampedByteLength = table.Column<long>(type: "bigint", nullable: false),
                    OriginalSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StampedSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapturedPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CapturedPhotos_PhotoUploads_UploadId",
                        column: x => x.UploadId,
                        principalSchema: "geo_capture",
                        principalTable: "PhotoUploads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PhotoContents",
                schema: "geo_capture",
                columns: table => new
                {
                    PhotoId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalBytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    StampedBytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoContents", x => x.PhotoId);
                    table.ForeignKey(
                        name: "FK_PhotoContents_CapturedPhotos_PhotoId",
                        column: x => x.PhotoId,
                        principalSchema: "geo_capture",
                        principalTable: "CapturedPhotos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CapturedPhotos_UploadId_ClientPhotoId",
                schema: "geo_capture",
                table: "CapturedPhotos",
                columns: new[] { "UploadId", "ClientPhotoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CapturedPhotos_UploadId_Position",
                schema: "geo_capture",
                table: "CapturedPhotos",
                columns: new[] { "UploadId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoUploads_OwnerId_ClientUploadId",
                schema: "geo_capture",
                table: "PhotoUploads",
                columns: new[] { "OwnerId", "ClientUploadId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoUploads_OwnerId_UploadedAtUtc_Id",
                schema: "geo_capture",
                table: "PhotoUploads",
                columns: new[] { "OwnerId", "UploadedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhotoContents",
                schema: "geo_capture");

            migrationBuilder.DropTable(
                name: "CapturedPhotos",
                schema: "geo_capture");

            migrationBuilder.DropTable(
                name: "PhotoUploads",
                schema: "geo_capture");
        }
    }
}
