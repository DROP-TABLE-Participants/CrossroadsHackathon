using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ErpGenerator.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialControlPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "control_plane");

            migrationBuilder.CreateTable(
                name: "admin_audit_records",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorAdminAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorEmailSnapshot = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SafeDetailsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_audit_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "customer_accounts",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CompanyName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    RepresentativeName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Position = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsGuest = table.Column<bool>(type: "boolean", nullable: false),
                    UserName = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FriendlyName = table.Column<string>(type: "text", nullable: true),
                    Xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_protection_keys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_database_secrets",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SecretReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TenantStableId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Environment = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    DatabaseName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Ciphertext = table.Column<string>(type: "text", nullable: false),
                    Nonce = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AuthenticationTag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_database_secrets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "demo_bookings",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    PhoneNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RepresentativeName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Position = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CompanyName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    StartDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_demo_bookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_demo_bookings_customer_accounts_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "control_plane",
                        principalTable: "customer_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_sessions",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReplacedByTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedByIpHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_refresh_sessions_customer_accounts_CustomerAccountId",
                        column: x => x.CustomerAccountId,
                        principalSchema: "control_plane",
                        principalTable: "customer_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StableId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CompanyName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Plan = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SetupStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tenants_customer_accounts_OwnerId",
                        column: x => x.OwnerId,
                        principalSchema: "control_plane",
                        principalTable: "customer_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_claims",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_claims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_claims_customer_accounts_UserId",
                        column: x => x.UserId,
                        principalSchema: "control_plane",
                        principalTable: "customer_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_logins",
                schema: "control_plane",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_logins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_user_logins_customer_accounts_UserId",
                        column: x => x.UserId,
                        principalSchema: "control_plane",
                        principalTable: "customer_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_tokens",
                schema: "control_plane",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_tokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_user_tokens_customer_accounts_UserId",
                        column: x => x.UserId,
                        principalSchema: "control_plane",
                        principalTable: "customer_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_claims",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_claims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_role_claims_roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "control_plane",
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                schema: "control_plane",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_user_roles_customer_accounts_UserId",
                        column: x => x.UserId,
                        principalSchema: "control_plane",
                        principalTable: "customer_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_roles_roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "control_plane",
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DemoBookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Recipient = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Subject = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notification_deliveries_demo_bookings_DemoBookingId",
                        column: x => x.DemoBookingId,
                        principalSchema: "control_plane",
                        principalTable: "demo_bookings",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "demo_instances",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReplacesId = table.Column<Guid>(type: "uuid", nullable: true),
                    TemplateVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DeploymentReceiptJson = table.Column<string>(type: "text", nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_demo_instances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_demo_instances_customer_accounts_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "control_plane",
                        principalTable: "customer_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_demo_instances_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "control_plane",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "generation_projects",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompanyName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Goal = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    DiscoveryMode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Industry = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CompanyType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    OwnerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    OwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    OwnerRoleTitle = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    GenerateWithAssumptions = table.Column<bool>(type: "boolean", nullable: false),
                    DeployToCustomerStaging = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    GenerationRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_generation_projects_customer_accounts_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "control_plane",
                        principalTable: "customer_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_generation_projects_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "control_plane",
                        principalTable: "tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "tenant_environments",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Environment = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    DatabaseName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    CredentialSecretReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RuntimeUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ActiveBundleId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ActiveBundleVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ActiveBundleHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OwnerActivationUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OwnerActivationExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_environments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tenant_environments_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "control_plane",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "generation_project_input_documents",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_project_input_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_generation_project_input_documents_generation_projects_Gene~",
                        column: x => x.GenerationProjectId,
                        principalSchema: "control_plane",
                        principalTable: "generation_projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "generation_project_input_sources",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderConfigKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ConnectionId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SourceKind = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SafeMetadataJson = table.Column<string>(type: "text", nullable: false),
                    ScopeMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "unconfigured"),
                    SelectedExternalIdsJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    ExcludedExternalIdsJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    IncludeDescendants = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IncludeNewContent = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    AuthorizationAcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AuthorizationNoticeVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSyncRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetainContentUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRecordCount = table.Column<int>(type: "integer", nullable: true),
                    LastTextRecordCount = table.Column<int>(type: "integer", nullable: true),
                    SyncDiscoveredCount = table.Column<int>(type: "integer", nullable: true),
                    SyncProcessedCount = table.Column<int>(type: "integer", nullable: false),
                    SyncFailedCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastWarning = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_project_input_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_generation_project_input_sources_generation_projects_Genera~",
                        column: x => x.GenerationProjectId,
                        principalSchema: "control_plane",
                        principalTable: "generation_projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "generation_project_participants",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    RoleTitle = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Department = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_project_participants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_generation_project_participants_generation_projects_Generat~",
                        column: x => x.GenerationProjectId,
                        principalSchema: "control_plane",
                        principalTable: "generation_projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "generation_runs",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestedMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActualMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastEventSequence = table.Column<int>(type: "integer", nullable: false),
                    ResumeAttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ResumeFailureCount = table.Column<int>(type: "integer", nullable: false),
                    ResumeLastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResumeFailureMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ClaimCount = table.Column<int>(type: "integer", nullable: false),
                    FailureCount = table.Column<int>(type: "integer", nullable: false),
                    FailureDisposition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    WarningsJson = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancellationRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_generation_runs_generation_projects_GenerationProjectId",
                        column: x => x.GenerationProjectId,
                        principalSchema: "control_plane",
                        principalTable: "generation_projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "owner_activations",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantEnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_owner_activations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_owner_activations_tenant_environments_TenantEnvironmentId",
                        column: x => x.TenantEnvironmentId,
                        principalSchema: "control_plane",
                        principalTable: "tenant_environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "runtime_deployment_events",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantEnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Details = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_deployment_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_runtime_deployment_events_tenant_environments_TenantEnviron~",
                        column: x => x.TenantEnvironmentId,
                        principalSchema: "control_plane",
                        principalTable: "tenant_environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "runtime_feedback",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantEnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeUserId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RuntimeUserEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Category = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Message = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    EntityKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    RecordId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    PagePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_feedback", x => x.Id);
                    table.ForeignKey(
                        name: "FK_runtime_feedback_tenant_environments_TenantEnvironmentId",
                        column: x => x.TenantEnvironmentId,
                        principalSchema: "control_plane",
                        principalTable: "tenant_environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "runtime_usage_events",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantEnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeUserId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RuntimeUserEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    RuntimeUserRole = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EventKind = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EntityKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    RecordId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    WorkflowKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ActionKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ReportKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    PagePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: false),
                    DismissedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_usage_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_runtime_usage_events_tenant_environments_TenantEnvironmentId",
                        column: x => x.TenantEnvironmentId,
                        principalSchema: "control_plane",
                        principalTable: "tenant_environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nango_synced_records",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationProjectInputSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    RecordModel = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Provider = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Title = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    ContentState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SafeMetadataJson = table.Column<string>(type: "text", nullable: false),
                    UpstreamModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nango_synced_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_nango_synced_records_generation_project_input_sources_Gener~",
                        column: x => x.GenerationProjectInputSourceId,
                        principalSchema: "control_plane",
                        principalTable: "generation_project_input_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "generation_evidence_snapshots",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    EvidenceText = table.Column<string>(type: "text", nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_evidence_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_generation_evidence_snapshots_generation_runs_GenerationRun~",
                        column: x => x.GenerationRunId,
                        principalSchema: "control_plane",
                        principalTable: "generation_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "generation_run_events",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    EventType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    StageKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: true),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SafeMetadataJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_run_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_generation_run_events_generation_runs_GenerationRunId",
                        column: x => x.GenerationRunId,
                        principalSchema: "control_plane",
                        principalTable: "generation_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "generation_run_stages",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    StageKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_run_stages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_generation_run_stages_generation_runs_GenerationRunId",
                        column: x => x.GenerationRunId,
                        principalSchema: "control_plane",
                        principalTable: "generation_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pending_work_items",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Prompt = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Reason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    TargetRole = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    SourceStage = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Blocking = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResolutionNote = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_work_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pending_work_items_generation_runs_GenerationRunId",
                        column: x => x.GenerationRunId,
                        principalSchema: "control_plane",
                        principalTable: "generation_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "generation_evidence_snapshot_items",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationEvidenceSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExternalId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Title = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_evidence_snapshot_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_generation_evidence_snapshot_items_generation_evidence_snap~",
                        column: x => x.GenerationEvidenceSnapshotId,
                        principalSchema: "control_plane",
                        principalTable: "generation_evidence_snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "generation_agent_work_items",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationRunStageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentWorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkItemKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AgentKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AgentVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AgentRole = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    AgentLayer = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ContractVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DefinitionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SupervisorAgentKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    InputManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    RepairCount = table.Column<int>(type: "integer", nullable: false),
                    RoutingDepth = table.Column<int>(type: "integer", nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NotBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureDisposition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_agent_work_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_generation_agent_work_items_generation_agent_work_items_Par~",
                        column: x => x.ParentWorkItemId,
                        principalSchema: "control_plane",
                        principalTable: "generation_agent_work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_generation_agent_work_items_generation_run_stages_Generatio~",
                        column: x => x.GenerationRunStageId,
                        principalSchema: "control_plane",
                        principalTable: "generation_run_stages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_generation_agent_work_items_generation_runs_GenerationRunId",
                        column: x => x.GenerationRunId,
                        principalSchema: "control_plane",
                        principalTable: "generation_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "interview_requests",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    PendingWorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParticipantId = table.Column<Guid>(type: "uuid", nullable: true),
                    QuestionKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Question = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    RecipientEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    InviteDeliveryStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    InviteDeliveryAttemptCount = table.Column<int>(type: "integer", nullable: false),
                    InviteDeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    InviteDeliveryError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interview_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_interview_requests_generation_project_participants_Particip~",
                        column: x => x.ParticipantId,
                        principalSchema: "control_plane",
                        principalTable: "generation_project_participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_interview_requests_generation_projects_GenerationProjectId",
                        column: x => x.GenerationProjectId,
                        principalSchema: "control_plane",
                        principalTable: "generation_projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_interview_requests_pending_work_items_PendingWorkItemId",
                        column: x => x.PendingWorkItemId,
                        principalSchema: "control_plane",
                        principalTable: "pending_work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "generation_agent_execution_attempts",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationAgentWorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WorkerId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AgentVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DefinitionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InputManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InputEnvelopeJson = table.Column<string>(type: "text", nullable: false),
                    OutputArtifactHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureDisposition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RepairInstructions = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_agent_execution_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_generation_agent_execution_attempts_generation_agent_work_i~",
                        column: x => x.GenerationAgentWorkItemId,
                        principalSchema: "control_plane",
                        principalTable: "generation_agent_work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "generation_agent_work_item_dependencies",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationAgentWorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    DependsOnWorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_agent_work_item_dependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_generation_agent_work_item_dependencies_generation_agent_wo~",
                        column: x => x.DependsOnWorkItemId,
                        principalSchema: "control_plane",
                        principalTable: "generation_agent_work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_generation_agent_work_item_dependencies_generation_agent_w~1",
                        column: x => x.GenerationAgentWorkItemId,
                        principalSchema: "control_plane",
                        principalTable: "generation_agent_work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "interview_answers",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InterviewRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    AnswerText = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    StructuredFactsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interview_answers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_interview_answers_interview_requests_InterviewRequestId",
                        column: x => x.InterviewRequestId,
                        principalSchema: "control_plane",
                        principalTable: "interview_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "interview_sessions",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InterviewRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interview_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_interview_sessions_interview_requests_InterviewRequestId",
                        column: x => x.InterviewRequestId,
                        principalSchema: "control_plane",
                        principalTable: "interview_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "generation_artifact_versions",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationRunStageId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationAgentWorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationAgentExecutionAttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupersedesArtifactVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArtifactKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ArtifactType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SchemaName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InputManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EvidenceSnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProducerAgentKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ProducerAgentVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ProducerDefinitionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CanonicalJson = table.Column<string>(type: "text", nullable: false),
                    ValidationStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ValidationSummary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_artifact_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_generation_artifact_versions_generation_agent_execution_att~",
                        column: x => x.GenerationAgentExecutionAttemptId,
                        principalSchema: "control_plane",
                        principalTable: "generation_agent_execution_attempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_generation_artifact_versions_generation_agent_work_items_Ge~",
                        column: x => x.GenerationAgentWorkItemId,
                        principalSchema: "control_plane",
                        principalTable: "generation_agent_work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_generation_artifact_versions_generation_artifact_versions_S~",
                        column: x => x.SupersedesArtifactVersionId,
                        principalSchema: "control_plane",
                        principalTable: "generation_artifact_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_generation_artifact_versions_generation_run_stages_Generati~",
                        column: x => x.GenerationRunStageId,
                        principalSchema: "control_plane",
                        principalTable: "generation_run_stages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_generation_artifact_versions_generation_runs_GenerationRunId",
                        column: x => x.GenerationRunId,
                        principalSchema: "control_plane",
                        principalTable: "generation_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "model_call_records",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationAgentWorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    GenerationAgentExecutionAttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    ControllerCallId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Phase = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProviderResponseId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AgentKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    AgentVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Model = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    PromptVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ContractVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    DurationMilliseconds = table.Column<long>(type: "bigint", nullable: true),
                    InputTokenCount = table.Column<int>(type: "integer", nullable: true),
                    OutputTokenCount = table.Column<int>(type: "integer", nullable: true),
                    ResponseHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_call_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_model_call_records_generation_agent_execution_attempts_Gene~",
                        column: x => x.GenerationAgentExecutionAttemptId,
                        principalSchema: "control_plane",
                        principalTable: "generation_agent_execution_attempts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_model_call_records_generation_agent_work_items_GenerationAg~",
                        column: x => x.GenerationAgentWorkItemId,
                        principalSchema: "control_plane",
                        principalTable: "generation_agent_work_items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_model_call_records_generation_runs_GenerationRunId",
                        column: x => x.GenerationRunId,
                        principalSchema: "control_plane",
                        principalTable: "generation_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "interview_messages",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InterviewSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MessageKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    QuestionKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    TurnKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Content = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interview_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_interview_messages_interview_sessions_InterviewSessionId",
                        column: x => x.InterviewSessionId,
                        principalSchema: "control_plane",
                        principalTable: "interview_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "erp_bundle_versions",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationArtifactVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BundleId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_erp_bundle_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_erp_bundle_versions_generation_artifact_versions_Generation~",
                        column: x => x.GenerationArtifactVersionId,
                        principalSchema: "control_plane",
                        principalTable: "generation_artifact_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_erp_bundle_versions_generation_runs_GenerationRunId",
                        column: x => x.GenerationRunId,
                        principalSchema: "control_plane",
                        principalTable: "generation_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deployments",
                schema: "control_plane",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ErpBundleVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Environment = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OperationId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RuntimeDeploymentId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    DeployedBundleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeployedBundleVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeployedBundleHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TestSuiteHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TestsStatus = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PromotionReceiptId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PromotionReceiptPayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PromotionReceiptSignature = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PromotionReceiptJson = table.Column<string>(type: "text", nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_deployments_erp_bundle_versions_ErpBundleVersionId",
                        column: x => x.ErpBundleVersionId,
                        principalSchema: "control_plane",
                        principalTable: "erp_bundle_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_deployments_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "control_plane",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_records_ActorAdminAccountId_CreatedAt",
                schema: "control_plane",
                table: "admin_audit_records",
                columns: new[] { "ActorAdminAccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "control_plane",
                table: "customer_accounts",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                schema: "control_plane",
                table: "customer_accounts",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_demo_bookings_CustomerId_Status",
                schema: "control_plane",
                table: "demo_bookings",
                columns: new[] { "CustomerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_demo_bookings_StartDate_EndDate",
                schema: "control_plane",
                table: "demo_bookings",
                columns: new[] { "StartDate", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_demo_instances_CustomerId_Status",
                schema: "control_plane",
                table: "demo_instances",
                columns: new[] { "CustomerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_demo_instances_TenantId",
                schema: "control_plane",
                table: "demo_instances",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_deployments_ErpBundleVersionId",
                schema: "control_plane",
                table: "deployments",
                column: "ErpBundleVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_deployments_OperationId",
                schema: "control_plane",
                table: "deployments",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_deployments_PromotionReceiptId",
                schema: "control_plane",
                table: "deployments",
                column: "PromotionReceiptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_deployments_Status_LeaseExpiresAt",
                schema: "control_plane",
                table: "deployments",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_deployments_TenantId",
                schema: "control_plane",
                table: "deployments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_erp_bundle_versions_BundleId_Version",
                schema: "control_plane",
                table: "erp_bundle_versions",
                columns: new[] { "BundleId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_erp_bundle_versions_ContentHash",
                schema: "control_plane",
                table: "erp_bundle_versions",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_erp_bundle_versions_GenerationArtifactVersionId",
                schema: "control_plane",
                table: "erp_bundle_versions",
                column: "GenerationArtifactVersionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_erp_bundle_versions_GenerationRunId_ContentHash",
                schema: "control_plane",
                table: "erp_bundle_versions",
                columns: new[] { "GenerationRunId", "ContentHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generation_agent_execution_attempts_GenerationAgentWorkItem~",
                schema: "control_plane",
                table: "generation_agent_execution_attempts",
                columns: new[] { "GenerationAgentWorkItemId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generation_agent_execution_attempts_Status_StartedAt",
                schema: "control_plane",
                table: "generation_agent_execution_attempts",
                columns: new[] { "Status", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_generation_agent_work_item_dependencies_DependsOnWorkItemId",
                schema: "control_plane",
                table: "generation_agent_work_item_dependencies",
                column: "DependsOnWorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_generation_agent_work_item_dependencies_GenerationAgentWork~",
                schema: "control_plane",
                table: "generation_agent_work_item_dependencies",
                columns: new[] { "GenerationAgentWorkItemId", "DependsOnWorkItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generation_agent_work_items_GenerationRunId_WorkItemKey",
                schema: "control_plane",
                table: "generation_agent_work_items",
                columns: new[] { "GenerationRunId", "WorkItemKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generation_agent_work_items_GenerationRunStageId",
                schema: "control_plane",
                table: "generation_agent_work_items",
                column: "GenerationRunStageId");

            migrationBuilder.CreateIndex(
                name: "IX_generation_agent_work_items_ParentWorkItemId",
                schema: "control_plane",
                table: "generation_agent_work_items",
                column: "ParentWorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_generation_agent_work_items_Status_LeaseExpiresAt",
                schema: "control_plane",
                table: "generation_agent_work_items",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_generation_agent_work_items_Status_NotBefore_Priority_Creat~",
                schema: "control_plane",
                table: "generation_agent_work_items",
                columns: new[] { "Status", "NotBefore", "Priority", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_generation_artifact_versions_ContentHash",
                schema: "control_plane",
                table: "generation_artifact_versions",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_generation_artifact_versions_GenerationAgentExecutionAttemp~",
                schema: "control_plane",
                table: "generation_artifact_versions",
                column: "GenerationAgentExecutionAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_generation_artifact_versions_GenerationAgentWorkItemId",
                schema: "control_plane",
                table: "generation_artifact_versions",
                column: "GenerationAgentWorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_generation_artifact_versions_GenerationRunId_ArtifactKey_Ve~",
                schema: "control_plane",
                table: "generation_artifact_versions",
                columns: new[] { "GenerationRunId", "ArtifactKey", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generation_artifact_versions_GenerationRunId_ArtifactType_C~",
                schema: "control_plane",
                table: "generation_artifact_versions",
                columns: new[] { "GenerationRunId", "ArtifactType", "ContentHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generation_artifact_versions_GenerationRunStageId",
                schema: "control_plane",
                table: "generation_artifact_versions",
                column: "GenerationRunStageId");

            migrationBuilder.CreateIndex(
                name: "IX_generation_artifact_versions_SupersedesArtifactVersionId",
                schema: "control_plane",
                table: "generation_artifact_versions",
                column: "SupersedesArtifactVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_generation_evidence_snapshot_items_GenerationEvidenceSnapsh~",
                schema: "control_plane",
                table: "generation_evidence_snapshot_items",
                columns: new[] { "GenerationEvidenceSnapshotId", "SourceType", "SourceEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_generation_evidence_snapshots_GenerationRunId_Sequence",
                schema: "control_plane",
                table: "generation_evidence_snapshots",
                columns: new[] { "GenerationRunId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generation_project_input_documents_GenerationProjectId",
                schema: "control_plane",
                table: "generation_project_input_documents",
                column: "GenerationProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_generation_project_input_sources_GenerationProjectId_Provid~",
                schema: "control_plane",
                table: "generation_project_input_sources",
                columns: new[] { "GenerationProjectId", "ProviderConfigKey", "ConnectionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generation_project_participants_GenerationProjectId_Email",
                schema: "control_plane",
                table: "generation_project_participants",
                columns: new[] { "GenerationProjectId", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_generation_projects_CustomerId",
                schema: "control_plane",
                table: "generation_projects",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_generation_projects_GenerationRequestedAt",
                schema: "control_plane",
                table: "generation_projects",
                column: "GenerationRequestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_generation_projects_TenantId",
                schema: "control_plane",
                table: "generation_projects",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_generation_run_events_EventType_CreatedAt",
                schema: "control_plane",
                table: "generation_run_events",
                columns: new[] { "EventType", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_generation_run_events_GenerationRunId_Sequence",
                schema: "control_plane",
                table: "generation_run_events",
                columns: new[] { "GenerationRunId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generation_run_stages_GenerationRunId_Sequence",
                schema: "control_plane",
                table: "generation_run_stages",
                columns: new[] { "GenerationRunId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generation_run_stages_GenerationRunId_StageKey",
                schema: "control_plane",
                table: "generation_run_stages",
                columns: new[] { "GenerationRunId", "StageKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generation_runs_GenerationProjectId",
                schema: "control_plane",
                table: "generation_runs",
                column: "GenerationProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_generation_runs_Status_LeaseExpiresAt",
                schema: "control_plane",
                table: "generation_runs",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_generation_runs_Status_NextAttemptAt_CreatedAt",
                schema: "control_plane",
                table: "generation_runs",
                columns: new[] { "Status", "NextAttemptAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_generation_runs_Status_ResumeFailureCount_UpdatedAt",
                schema: "control_plane",
                table: "generation_runs",
                columns: new[] { "Status", "ResumeFailureCount", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_interview_answers_InterviewRequestId",
                schema: "control_plane",
                table: "interview_answers",
                column: "InterviewRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_interview_messages_InterviewSessionId_TurnKey_Role",
                schema: "control_plane",
                table: "interview_messages",
                columns: new[] { "InterviewSessionId", "TurnKey", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_interview_requests_GenerationProjectId",
                schema: "control_plane",
                table: "interview_requests",
                column: "GenerationProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_interview_requests_ParticipantId",
                schema: "control_plane",
                table: "interview_requests",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_interview_requests_PendingWorkItemId",
                schema: "control_plane",
                table: "interview_requests",
                column: "PendingWorkItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_interview_requests_TokenHash",
                schema: "control_plane",
                table: "interview_requests",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_interview_sessions_InterviewRequestId",
                schema: "control_plane",
                table: "interview_sessions",
                column: "InterviewRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_model_call_records_GenerationAgentExecutionAttemptId",
                schema: "control_plane",
                table: "model_call_records",
                column: "GenerationAgentExecutionAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_model_call_records_GenerationAgentWorkItemId",
                schema: "control_plane",
                table: "model_call_records",
                column: "GenerationAgentWorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_model_call_records_GenerationRunId_ControllerCallId",
                schema: "control_plane",
                table: "model_call_records",
                columns: new[] { "GenerationRunId", "ControllerCallId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nango_synced_records_GenerationProjectInputSourceId_RecordM~",
                schema: "control_plane",
                table: "nango_synced_records",
                columns: new[] { "GenerationProjectInputSourceId", "RecordModel", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_DemoBookingId",
                schema: "control_plane",
                table: "notification_deliveries",
                column: "DemoBookingId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_Status_NextAttemptAt",
                schema: "control_plane",
                table: "notification_deliveries",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_owner_activations_TenantEnvironmentId_Email",
                schema: "control_plane",
                table: "owner_activations",
                columns: new[] { "TenantEnvironmentId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pending_work_items_GenerationRunId_Key",
                schema: "control_plane",
                table: "pending_work_items",
                columns: new[] { "GenerationRunId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_sessions_CustomerAccountId_ExpiresAt",
                schema: "control_plane",
                table: "refresh_sessions",
                columns: new[] { "CustomerAccountId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_refresh_sessions_TokenHash",
                schema: "control_plane",
                table: "refresh_sessions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_role_claims_RoleId",
                schema: "control_plane",
                table: "role_claims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                schema: "control_plane",
                table: "roles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runtime_deployment_events_TenantEnvironmentId_CreatedAt",
                schema: "control_plane",
                table: "runtime_deployment_events",
                columns: new[] { "TenantEnvironmentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_feedback_TenantEnvironmentId_CreatedAt",
                schema: "control_plane",
                table: "runtime_feedback",
                columns: new[] { "TenantEnvironmentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_usage_events_TenantEnvironmentId_OccurredAt",
                schema: "control_plane",
                table: "runtime_usage_events",
                columns: new[] { "TenantEnvironmentId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_database_secrets_SecretReference",
                schema: "control_plane",
                table: "tenant_database_secrets",
                column: "SecretReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_database_secrets_TenantStableId_Environment_Database~",
                schema: "control_plane",
                table: "tenant_database_secrets",
                columns: new[] { "TenantStableId", "Environment", "DatabaseName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_environments_Host",
                schema: "control_plane",
                table: "tenant_environments",
                column: "Host",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_environments_TenantId_Environment",
                schema: "control_plane",
                table: "tenant_environments",
                columns: new[] { "TenantId", "Environment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenants_OwnerId",
                schema: "control_plane",
                table: "tenants",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_tenants_Slug",
                schema: "control_plane",
                table: "tenants",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenants_StableId",
                schema: "control_plane",
                table: "tenants",
                column: "StableId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_claims_UserId",
                schema: "control_plane",
                table: "user_claims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_user_logins_UserId",
                schema: "control_plane",
                table: "user_logins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_RoleId",
                schema: "control_plane",
                table: "user_roles",
                column: "RoleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_audit_records",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "data_protection_keys",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "demo_instances",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "deployments",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "generation_agent_work_item_dependencies",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "generation_evidence_snapshot_items",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "generation_project_input_documents",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "generation_run_events",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "interview_answers",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "interview_messages",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "model_call_records",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "nango_synced_records",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "notification_deliveries",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "owner_activations",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "refresh_sessions",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "role_claims",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "runtime_deployment_events",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "runtime_feedback",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "runtime_usage_events",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "tenant_database_secrets",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "user_claims",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "user_logins",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "user_roles",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "user_tokens",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "erp_bundle_versions",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "generation_evidence_snapshots",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "interview_sessions",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "generation_project_input_sources",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "demo_bookings",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "tenant_environments",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "generation_artifact_versions",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "interview_requests",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "generation_agent_execution_attempts",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "generation_project_participants",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "pending_work_items",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "generation_agent_work_items",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "generation_run_stages",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "generation_runs",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "generation_projects",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "tenants",
                schema: "control_plane");

            migrationBuilder.DropTable(
                name: "customer_accounts",
                schema: "control_plane");
        }
    }
}
