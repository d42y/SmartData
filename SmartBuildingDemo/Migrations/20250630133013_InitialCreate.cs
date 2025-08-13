using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBuildingDemo.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "__sysAnalytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Interval = table.Column<int>(type: "int", nullable: false),
                    LastRun = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Embeddable = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK___sysAnalytics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "__sysChangeLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PropertyName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OriginalValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ChangeType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK___sysChangeLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "__sysEmbeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Embedding = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK___sysEmbeddings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "__sysIntegrityLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PropertyName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PreviousHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK___sysIntegrityLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "__sysTimeseriesBaseValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PropertyName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK___sysTimeseriesBaseValues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Buildings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Address = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Buildings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "__sysAnalyticsSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnalyticsId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Expression = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ResultVariable = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MaxLoop = table.Column<int>(type: "int", nullable: false, defaultValue: 10)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK___sysAnalyticsSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK___sysAnalyticsSteps___sysAnalytics_AnalyticsId",
                        column: x => x.AnalyticsId,
                        principalTable: "__sysAnalytics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "__sysTimeseriesDeltas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaseValueId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Deltas = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    LastTimestamp = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK___sysTimeseriesDeltas", x => x.Id);
                    table.ForeignKey(
                        name: "FK___sysTimeseriesDeltas___sysTimeseriesBaseValues_BaseValueId",
                        column: x => x.BaseValueId,
                        principalTable: "__sysTimeseriesBaseValues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sensors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BuildingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Temperature = table.Column<double>(type: "float", nullable: false),
                    Humidity = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sensors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sensors_Buildings_BuildingId",
                        column: x => x.BuildingId,
                        principalTable: "Buildings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX___sysAnalytics_Name",
                table: "__sysAnalytics",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX___sysAnalyticsSteps_AnalyticsId_Order",
                table: "__sysAnalyticsSteps",
                columns: new[] { "AnalyticsId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX___sysChangeLog_TableName_EntityId_ChangedAt",
                table: "__sysChangeLog",
                columns: new[] { "TableName", "EntityId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX___sysEmbeddings_TableName_EntityId",
                table: "__sysEmbeddings",
                columns: new[] { "TableName", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX___sysTimeseriesBaseValues_TableName_EntityId_PropertyName_Timestamp",
                table: "__sysTimeseriesBaseValues",
                columns: new[] { "TableName", "EntityId", "PropertyName", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX___sysTimeseriesDeltas_BaseValueId",
                table: "__sysTimeseriesDeltas",
                column: "BaseValueId");

            migrationBuilder.CreateIndex(
                name: "IX_Sensors_BuildingId",
                table: "Sensors",
                column: "BuildingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "__sysAnalyticsSteps");

            migrationBuilder.DropTable(
                name: "__sysChangeLog");

            migrationBuilder.DropTable(
                name: "__sysEmbeddings");

            migrationBuilder.DropTable(
                name: "__sysIntegrityLog");

            migrationBuilder.DropTable(
                name: "__sysTimeseriesDeltas");

            migrationBuilder.DropTable(
                name: "Sensors");

            migrationBuilder.DropTable(
                name: "__sysAnalytics");

            migrationBuilder.DropTable(
                name: "__sysTimeseriesBaseValues");

            migrationBuilder.DropTable(
                name: "Buildings");
        }
    }
}
