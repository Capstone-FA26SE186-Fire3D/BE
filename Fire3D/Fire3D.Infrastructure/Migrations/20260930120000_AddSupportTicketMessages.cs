using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))][Migration("20260930120000_AddSupportTicketMessages")]
public sealed class AddSupportTicketMessages : Migration { protected override void Up(MigrationBuilder b) => b.Sql("CREATE TABLE IF NOT EXISTS public.support_ticket_messages(id uuid PRIMARY KEY,ticket_id uuid NOT NULL REFERENCES public.support_tickets(id) ON DELETE RESTRICT,author_id uuid NOT NULL REFERENCES public.users(id) ON DELETE RESTRICT,message text NOT NULL CHECK(length(message) BETWEEN 1 AND 10000),created_at timestamptz NOT NULL); CREATE INDEX IF NOT EXISTS ix_support_ticket_messages_ticket_created ON public.support_ticket_messages(ticket_id,created_at);"); protected override void Down(MigrationBuilder b) => b.Sql("DROP TABLE IF EXISTS public.support_ticket_messages;"); }
