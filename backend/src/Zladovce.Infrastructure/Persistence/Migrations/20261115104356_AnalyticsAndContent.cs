using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zladovce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AnalyticsAndContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_email_outbox_template",
                table: "email_outbox");

            migrationBuilder.AddColumn<int>(
                name: "logo_height",
                table: "store_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "logo_width",
                table: "store_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "blog_categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blog_categories", x => x.id);
                    table.CheckConstraint("ck_blog_categories_sort_order", "sort_order >= 0");
                });

            migrationBuilder.CreateTable(
                name: "cms_pages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    content = table.Column<string>(type: "jsonb", nullable: true),
                    seo_title = table.Column<string>(type: "text", nullable: true),
                    seo_description = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cms_pages", x => x.id);
                    table.CheckConstraint("ck_cms_pages_key", "key IN ('home', 'about', 'privacy', 'terms', 'delivery')");
                    table.ForeignKey(
                        name: "fk_cms_pages_admin_users_updated_by_admin_id",
                        column: x => x.updated_by_admin_id,
                        principalTable: "admin_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "contact_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "citext", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    read_by_admin_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contact_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_contact_messages_admin_users_read_by_admin_id",
                        column: x => x.read_by_admin_id,
                        principalTable: "admin_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "expenses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    expense_date = table.Column<DateOnly>(type: "date", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    created_by_admin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expenses", x => x.id);
                    table.CheckConstraint("ck_expenses_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_expenses_admin_users_created_by_admin_id",
                        column: x => x.created_by_admin_id,
                        principalTable: "admin_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "faq_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    question = table.Column<string>(type: "text", nullable: false),
                    answer = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_published = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_faq_items", x => x.id);
                    table.CheckConstraint("ck_faq_items_sort_order", "sort_order >= 0");
                });

            migrationBuilder.CreateTable(
                name: "newsletter_subscribers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "citext", nullable: false),
                    consented_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmation_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    unsubscribed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_newsletter_subscribers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "site_rebuild_requests",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    pending = table.Column<bool>(type: "boolean", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_triggered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scheduled_checked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_result = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_rebuild_requests", x => x.id);
                    table.CheckConstraint("ck_site_rebuild_requests_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "testimonials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_name = table.Column<string>(type: "text", nullable: false),
                    place = table.Column<string>(type: "text", nullable: true),
                    text = table.Column<string>(type: "text", nullable: false),
                    is_published = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_testimonials", x => x.id);
                    table.CheckConstraint("ck_testimonials_sort_order", "sort_order >= 0");
                });

            migrationBuilder.CreateTable(
                name: "blog_posts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_admin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    excerpt = table.Column<string>(type: "text", nullable: false),
                    cover_image_key = table.Column<string>(type: "text", nullable: true),
                    cover_image_width = table.Column<int>(type: "integer", nullable: true),
                    cover_image_height = table.Column<int>(type: "integer", nullable: true),
                    content_html = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    seo_title = table.Column<string>(type: "text", nullable: true),
                    seo_description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blog_posts", x => x.id);
                    table.CheckConstraint("ck_blog_posts_archived", "deleted_at IS NULL OR status = 'Draft'");
                    table.CheckConstraint("ck_blog_posts_cover", "(cover_image_key IS NULL AND cover_image_width IS NULL AND cover_image_height IS NULL) OR (cover_image_key IS NOT NULL AND cover_image_width > 0 AND cover_image_height > 0)");
                    table.CheckConstraint("ck_blog_posts_published", "status <> 'Published' OR published_at IS NOT NULL");
                    table.CheckConstraint("ck_blog_posts_status", "status IN ('Draft', 'Published')");
                    table.ForeignKey(
                        name: "fk_blog_posts_admin_users_author_admin_id",
                        column: x => x.author_admin_id,
                        principalTable: "admin_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_blog_posts_blog_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "blog_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_email_outbox_template",
                table: "email_outbox",
                sql: "template IN ('OrderReceived', 'OrderAccepted', 'OrderPreparing', 'OrderShipped', 'OrderDelivered', 'OrderCancelled', 'NewsletterConfirm', 'ContactMessage')");

            migrationBuilder.CreateIndex(
                name: "ix_blog_categories_slug",
                table: "blog_categories",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_blog_posts_author_admin_id",
                table: "blog_posts",
                column: "author_admin_id");

            migrationBuilder.CreateIndex(
                name: "ix_blog_posts_category_id",
                table: "blog_posts",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_blog_posts_slug",
                table: "blog_posts",
                column: "slug",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_blog_posts_status_published_at",
                table: "blog_posts",
                columns: new[] { "status", "published_at" },
                descending: new[] { false, true },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_cms_pages_key",
                table: "cms_pages",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cms_pages_updated_by_admin_id",
                table: "cms_pages",
                column: "updated_by_admin_id");

            migrationBuilder.CreateIndex(
                name: "ix_contact_messages_created_at",
                table: "contact_messages",
                column: "created_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_contact_messages_read_by_admin_id",
                table: "contact_messages",
                column: "read_by_admin_id");

            migrationBuilder.CreateIndex(
                name: "ix_expenses_created_by_admin_id",
                table: "expenses",
                column: "created_by_admin_id");

            migrationBuilder.CreateIndex(
                name: "ix_expenses_expense_date",
                table: "expenses",
                column: "expense_date");

            migrationBuilder.CreateIndex(
                name: "ix_faq_items_sort_order",
                table: "faq_items",
                column: "sort_order");

            migrationBuilder.CreateIndex(
                name: "ix_newsletter_subscribers_email",
                table: "newsletter_subscribers",
                column: "email",
                unique: true);

            // Seed (DATABASE.md D6): the page keys with EMPTY content (no invented texts) and the single rebuild row.
            migrationBuilder.Sql(
                """
                INSERT INTO cms_pages (id, key, title, content, seo_title, seo_description, updated_at, updated_by_admin_id) VALUES
                    ('01937a00-0000-7000-8000-000000000001', 'home', NULL, NULL, NULL, NULL, now(), NULL),
                    ('01937a00-0000-7000-8000-000000000002', 'about', NULL, NULL, NULL, NULL, now(), NULL),
                    ('01937a00-0000-7000-8000-000000000003', 'privacy', NULL, NULL, NULL, NULL, now(), NULL),
                    ('01937a00-0000-7000-8000-000000000004', 'terms', NULL, NULL, NULL, NULL, now(), NULL),
                    ('01937a00-0000-7000-8000-000000000005', 'delivery', NULL, NULL, NULL, NULL, now(), NULL)
                ON CONFLICT (key) DO NOTHING;
                INSERT INTO site_rebuild_requests (id, pending, requested_at, last_triggered_at, scheduled_checked_until, last_result)
                VALUES (1, false, NULL, NULL, NULL, NULL)
                ON CONFLICT (id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blog_posts");

            migrationBuilder.DropTable(
                name: "cms_pages");

            migrationBuilder.DropTable(
                name: "contact_messages");

            migrationBuilder.DropTable(
                name: "expenses");

            migrationBuilder.DropTable(
                name: "faq_items");

            migrationBuilder.DropTable(
                name: "newsletter_subscribers");

            migrationBuilder.DropTable(
                name: "site_rebuild_requests");

            migrationBuilder.DropTable(
                name: "testimonials");

            migrationBuilder.DropTable(
                name: "blog_categories");

            migrationBuilder.DropCheckConstraint(
                name: "ck_email_outbox_template",
                table: "email_outbox");

            migrationBuilder.DropColumn(
                name: "logo_height",
                table: "store_settings");

            migrationBuilder.DropColumn(
                name: "logo_width",
                table: "store_settings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_email_outbox_template",
                table: "email_outbox",
                sql: "template IN ('OrderReceived', 'OrderAccepted', 'OrderPreparing', 'OrderShipped', 'OrderDelivered', 'OrderCancelled')");
        }
    }
}
