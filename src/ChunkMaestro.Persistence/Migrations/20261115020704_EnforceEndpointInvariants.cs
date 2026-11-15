using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChunkMaestro.Persistence.Migrations;
/// <inheritdoc />
public partial class EnforceEndpointInvariants : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // EF's default SQLite rebuild commits the temporary table before the destructive
        // phase. Endpoints is a leaf table: keep foreign keys enabled and the complete
        // rebuild in EF's migration transaction so interruption leaves the old schema.
        migrationBuilder.Sql("""
            CREATE TABLE "ef_temp_Endpoints" (
                "InstanceId" TEXT NOT NULL,
                "Edition" INTEGER NOT NULL,
                "Address" TEXT NOT NULL,
                "Port" INTEGER NOT NULL,
                "HostId" TEXT NOT NULL,
                CONSTRAINT "PK_Endpoints" PRIMARY KEY ("InstanceId", "Edition", "Address", "Port"),
                CONSTRAINT "CK_Endpoints_Edition" CHECK ("Edition" IN (0, 1)),
                CONSTRAINT "CK_Endpoints_Port" CHECK ("Port" BETWEEN 1 AND 65535),
                CONSTRAINT "FK_Endpoints_Hosts_HostId" FOREIGN KEY ("HostId") REFERENCES "Hosts" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_Endpoints_Instances_InstanceId" FOREIGN KEY ("InstanceId") REFERENCES "Instances" ("Id") ON DELETE RESTRICT
            );
            INSERT INTO "ef_temp_Endpoints" ("InstanceId", "Edition", "Address", "Port", "HostId")
            SELECT "InstanceId", "Edition", "Address", "Port", "HostId" FROM "Endpoints";
            """);
        migrationBuilder.Sql("DROP TABLE \"Endpoints\";");
        migrationBuilder.Sql("""
            ALTER TABLE "ef_temp_Endpoints" RENAME TO "Endpoints";
            CREATE UNIQUE INDEX "IX_Endpoints_HostId_Edition_Port" ON "Endpoints" ("HostId", "Edition", "Port");
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new System.NotSupportedException("Blind downgrade is disabled. Recover a compatible pre-migration backup through reviewed recovery.");
    }
}
