using Microsoft.EntityFrameworkCore.Migrations;
using System;

#nullable disable

namespace ChunkMaestro.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialFleet : Migration
{
    private static readonly string[] EndpointIndexColumns = ["HostId", "Edition", "Port"];
    private static readonly string[] HostIndexColumns = ["FleetId", "Address"];
    private static readonly string[] InstanceIndexColumns = ["HostId", "ServiceName"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Fleets",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Fleets", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Hosts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                FleetId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Address = table.Column<string>(type: "TEXT", nullable: false),
                RegisteredAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Hosts", x => x.Id);
                table.ForeignKey(
                    name: "FK_Hosts_Fleets_FleetId",
                    column: x => x.FleetId,
                    principalTable: "Fleets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Instances",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                ServiceName = table.Column<string>(type: "TEXT", nullable: false),
                RegisteredAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Instances", x => x.Id);
                table.ForeignKey(
                    name: "FK_Instances_Hosts_HostId",
                    column: x => x.HostId,
                    principalTable: "Hosts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Endpoints",
            columns: table => new
            {
                InstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                Edition = table.Column<int>(type: "INTEGER", nullable: false),
                Address = table.Column<string>(type: "TEXT", nullable: false),
                Port = table.Column<int>(type: "INTEGER", nullable: false),
                HostId = table.Column<Guid>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Endpoints", x => new
                {
                    x.InstanceId,
                    x.Edition,
                    x.Address,
                    x.Port
                });
                table.ForeignKey(
                    name: "FK_Endpoints_Hosts_HostId",
                    column: x => x.HostId,
                    principalTable: "Hosts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Endpoints_Instances_InstanceId",
                    column: x => x.InstanceId,
                    principalTable: "Instances",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Endpoints_HostId_Edition_Port",
            table: "Endpoints",
            columns: EndpointIndexColumns
,
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Hosts_FleetId_Address",
            table: "Hosts",
            columns: HostIndexColumns
,
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Instances_HostId_ServiceName",
            table: "Instances",
            columns: InstanceIndexColumns
,
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException("Blind downgrade is disabled. Recover a compatible pre-migration backup through reviewed recovery.");
    }
}
