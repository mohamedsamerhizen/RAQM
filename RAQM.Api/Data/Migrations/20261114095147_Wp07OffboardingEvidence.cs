using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RAQM.Api.Data.Migrations;

[DbContext(typeof(RaqmDbContext))]
[Migration("20261114095147_Wp07OffboardingEvidence")]
public sealed class Wp07OffboardingEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "RetainedExportId", table: "Accounts", schema: "saas", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "ExportId", table: "Cases", schema: "saas", nullable: true);
        migrationBuilder.CreateIndex(name: "IX_Accounts_TenantId_RetainedExportId", table: "Accounts", columns: ["TenantId", "RetainedExportId"], schema: "saas");
        migrationBuilder.CreateIndex(name: "IX_Cases_TenantId_ExportId", table: "Cases", columns: ["TenantId", "ExportId"], schema: "saas");
        migrationBuilder.AddForeignKey(name: "FK_Accounts_Exports_TenantId_RetainedExportId", table: "Accounts",
            columns: ["TenantId", "RetainedExportId"], principalTable: "Exports", principalColumns: ["TenantId", "Id"],
            schema: "saas", principalSchema: "saas", onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey(name: "FK_Cases_Exports_TenantId_ExportId", table: "Cases",
            columns: ["TenantId", "ExportId"], principalTable: "Exports", principalColumns: ["TenantId", "Id"],
            schema: "saas", principalSchema: "saas", onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(name: "FK_Accounts_Exports_TenantId_RetainedExportId", table: "Accounts", schema: "saas");
        migrationBuilder.DropForeignKey(name: "FK_Cases_Exports_TenantId_ExportId", table: "Cases", schema: "saas");
        migrationBuilder.DropIndex(name: "IX_Accounts_TenantId_RetainedExportId", table: "Accounts", schema: "saas");
        migrationBuilder.DropIndex(name: "IX_Cases_TenantId_ExportId", table: "Cases", schema: "saas");
        migrationBuilder.DropColumn(name: "RetainedExportId", table: "Accounts", schema: "saas");
        migrationBuilder.DropColumn(name: "ExportId", table: "Cases", schema: "saas");
    }
}
