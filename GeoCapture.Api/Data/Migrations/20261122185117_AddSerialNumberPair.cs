using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoCapture.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSerialNumberPair : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OldSerialNumber",
                schema: "geo_capture",
                table: "PhotoUploads",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OldSerialNumber",
                schema: "geo_capture",
                table: "PhotoUploads");
        }
    }
}
