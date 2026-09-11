using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

[DbContext(typeof(MyloMailDbContext))]
[Migration("20260911230000_AddResolvedMutationTarget")]
public partial class AddResolvedMutationTarget : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<Guid>(
			name: "ResolvedTargetMailboxId",
			table: "MutationExecutionAttemptItems",
			type: "TEXT",
			nullable: true
		);
	}

	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql(
			"""ALTER TABLE "MutationExecutionAttemptItems" DROP COLUMN "ResolvedTargetMailboxId";"""
		);
	}
}
