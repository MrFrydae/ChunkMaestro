using Microsoft.EntityFrameworkCore.Migrations;
using System;

#nullable disable

namespace ChunkMaestro.Persistence.Migrations;

/// <inheritdoc />
public partial class ControllerIdentityMetadata : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ControllerIdentities",
            columns: table => new
            {
                FleetId = table.Column<Guid>(type: "TEXT", nullable: false),
                ControllerId = table.Column<Guid>(type: "TEXT", nullable: false),
                AttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                CredentialId = table.Column<Guid>(type: "TEXT", nullable: false),
                ProfileVersion = table.Column<int>(type: "INTEGER", nullable: false),
                Purpose = table.Column<string>(type: "TEXT", nullable: false),
                State = table.Column<int>(type: "INTEGER", nullable: false),
                CertificateDer = table.Column<byte[]>(type: "BLOB", nullable: false),
                CertificateHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                SpkiHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                PublishedAt = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ControllerIdentities", x => x.FleetId);
                table.CheckConstraint("CK_ControllerIdentity_AttemptId", "typeof(AttemptId) = 'text' AND length(AttemptId) = 36 AND AttemptId <> '00000000-0000-0000-0000-000000000000' AND substr(AttemptId,9,1) = '-' AND substr(AttemptId,14,1) = '-' AND substr(AttemptId,19,1) = '-' AND substr(AttemptId,24,1) = '-' AND length(replace(AttemptId,'-','')) = 32 AND replace(AttemptId,'-','') NOT GLOB '*[^0-9A-F]*'");
                table.CheckConstraint("CK_ControllerIdentity_ControllerId", "typeof(ControllerId) = 'text' AND length(ControllerId) = 36 AND ControllerId <> '00000000-0000-0000-0000-000000000000' AND substr(ControllerId,9,1) = '-' AND substr(ControllerId,14,1) = '-' AND substr(ControllerId,19,1) = '-' AND substr(ControllerId,24,1) = '-' AND length(replace(ControllerId,'-','')) = 32 AND replace(ControllerId,'-','') NOT GLOB '*[^0-9A-F]*'");
                table.CheckConstraint("CK_ControllerIdentity_CredentialId", "typeof(CredentialId) = 'text' AND length(CredentialId) = 36 AND CredentialId <> '00000000-0000-0000-0000-000000000000' AND substr(CredentialId,9,1) = '-' AND substr(CredentialId,14,1) = '-' AND substr(CredentialId,19,1) = '-' AND substr(CredentialId,24,1) = '-' AND length(replace(CredentialId,'-','')) = 32 AND replace(CredentialId,'-','') NOT GLOB '*[^0-9A-F]*'");
                table.CheckConstraint("CK_ControllerIdentity_FleetId", "typeof(FleetId) = 'text' AND length(FleetId) = 36 AND FleetId <> '00000000-0000-0000-0000-000000000000' AND substr(FleetId,9,1) = '-' AND substr(FleetId,14,1) = '-' AND substr(FleetId,19,1) = '-' AND substr(FleetId,24,1) = '-' AND length(replace(FleetId,'-','')) = 32 AND replace(FleetId,'-','') NOT GLOB '*[^0-9A-F]*'");
                table.CheckConstraint("CK_ControllerIdentity_Material", "typeof(CertificateDer) = 'blob' AND length(CertificateDer) BETWEEN 1 AND 4096 AND typeof(CertificateHash) = 'blob' AND length(CertificateHash) = 32 AND typeof(SpkiHash) = 'blob' AND length(SpkiHash) = 32");
                table.CheckConstraint("CK_ControllerIdentity_Profile", "typeof(ProfileVersion) = 'integer' AND ProfileVersion = 1 AND Purpose = 'controller-root-v1'");
                table.CheckConstraint("CK_ControllerIdentity_StateTime", "typeof(State) = 'integer' AND typeof(CreatedAt) = 'integer' AND CreatedAt BETWEEN -62135596800000 AND 253402300799999 AND ((State = 0 AND PublishedAt IS NULL) OR (State = 1 AND typeof(PublishedAt) = 'integer' AND PublishedAt BETWEEN CreatedAt AND 253402300799999))");
                table.ForeignKey(
                    name: "FK_ControllerIdentities_Fleets_FleetId",
                    column: x => x.FleetId,
                    principalTable: "Fleets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ControllerIdentities_AttemptId",
            table: "ControllerIdentities",
            column: "AttemptId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ControllerIdentities_ControllerId",
            table: "ControllerIdentities",
            column: "ControllerId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ControllerIdentities_CredentialId",
            table: "ControllerIdentities",
            column: "CredentialId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException("Blind downgrade is disabled. Recover a compatible pre-migration backup through reviewed recovery.");
    }
}
