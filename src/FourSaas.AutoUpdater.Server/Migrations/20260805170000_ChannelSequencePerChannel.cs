using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FourSaas.AutoUpdater.Server.Migrations;

/// <inheritdoc />
[Migration("20260805170000_ChannelSequencePerChannel")]
[DbContext(typeof(ManagementDbContext))]
public partial class ChannelSequencePerChannel : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Channels_RepositoryId_ProductId_ChannelSequence",
            table: "Channels");

        migrationBuilder.CreateIndex(
            name: "IX_Channels_RepositoryId_ProductId_Channel_ChannelSequence",
            table: "Channels",
            columns: new[] { "RepositoryId", "ProductId", "Channel", "ChannelSequence" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Channels_RepositoryId_ProductId_Channel_ChannelSequence",
            table: "Channels");

        migrationBuilder.CreateIndex(
            name: "IX_Channels_RepositoryId_ProductId_ChannelSequence",
            table: "Channels",
            columns: new[] { "RepositoryId", "ProductId", "ChannelSequence" },
            unique: true);
    }
}
