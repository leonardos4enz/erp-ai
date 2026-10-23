using System.Security.Cryptography;
using Dapper;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;

namespace Erp.Api.Infrastructure;

/// <summary>Crea (de forma idempotente) la base, los esquemas del sistema y el usuario administrador inicial.</summary>
public static class Bootstrap
{
    public static async Task RunAsync(IServiceProvider services, IConfiguration config, ILogger log)
    {
        var db = services.GetRequiredService<Db>();
        if (config.GetValue("Database:AutoCreate", false))
            await EnsureDatabaseAsync(db.ConnectionString, log);

        await using var cn = await db.OpenAsync();
        await cn.ExecuteAsync(SchemaSql);
        await SeedAsync(cn, services.GetRequiredService<PasswordService>(), config, log);
    }

    private static async Task EnsureDatabaseAsync(string cs, ILogger log)
    {
        var builder = new SqlConnectionStringBuilder(cs);
        var name = builder.InitialCatalog;
        Identifiers.Quote(name); // el nombre de BD no puede ir como parámetro: se valida igual que cualquier identificador
        builder.InitialCatalog = "master";
        await using var cn = new SqlConnection(builder.ConnectionString);
        await cn.OpenAsync();
        var exists = await cn.ExecuteScalarAsync<int?>("SELECT 1 FROM sys.databases WHERE name = @name", new { name });
        if (exists is null)
        {
            log.LogInformation("Creando base de datos {Db}", name);
            await cn.ExecuteAsync($"CREATE DATABASE {Identifiers.Quote(name)}");
        }
    }

    private static async Task SeedAsync(SqlConnection cn, PasswordService passwords, IConfiguration config, ILogger log)
    {
        await cn.ExecuteAsync("""
            IF NOT EXISTS (SELECT 1 FROM meta.Role WHERE Name = N'Administrador')
                INSERT meta.Role (Name, Description, IsSystem, IsSuperuser, CanManageModel, CanManageUsers, CanViewAudit)
                VALUES (N'Administrador', N'Acceso total al sistema.', 1, 1, 1, 1, 1);
            IF NOT EXISTS (SELECT 1 FROM meta.Role WHERE Name = N'Usuario')
                INSERT meta.Role (Name, Description, IsSystem, IsSuperuser, CanManageModel, CanManageUsers, CanViewAudit)
                VALUES (N'Usuario', N'Captura de información según permisos por catálogo.', 1, 0, 0, 0, 0);
            """);

        if (await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM meta.[User]") > 0) return;

        var configured = config["Bootstrap:AdminPassword"];
        var password = string.IsNullOrEmpty(configured) ? GeneratePassword() : configured;
        if (!PasswordService.MeetsPolicy(password, out var why))
            throw new InvalidOperationException($"Bootstrap:AdminPassword no cumple la política: {why}");

        var id = await cn.ExecuteScalarAsync<int>("""
            INSERT meta.[User] (Username, DisplayName, PasswordHash)
            OUTPUT inserted.Id VALUES (N'admin', N'Administrador', @hash);
            """, new { hash = passwords.Hash(password) });
        await cn.ExecuteAsync("INSERT meta.UserRole (UserId, RoleId) SELECT @id, Id FROM meta.Role WHERE Name = N'Administrador'", new { id });

        if (string.IsNullOrEmpty(configured))
            // Se muestra una única vez en la consola local. Cámbiala al entrar.
            log.LogWarning("Usuario inicial creado: admin  /  contraseña: {Password}  (solo se muestra esta vez)", password);
        else
            log.LogInformation("Usuario inicial 'admin' creado con la contraseña configurada.");
    }

    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return string.Create(20, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }

    private const string SchemaSql = """
        IF SCHEMA_ID('meta') IS NULL EXEC('CREATE SCHEMA meta');
        IF SCHEMA_ID('app') IS NULL EXEC('CREATE SCHEMA app');

        IF OBJECT_ID('meta.Entity') IS NULL
        CREATE TABLE meta.Entity (
            Id int IDENTITY PRIMARY KEY,
            Name nvarchar(50) NOT NULL CONSTRAINT UX_Entity_Name UNIQUE,
            Label nvarchar(100) NOT NULL,
            LabelPlural nvarchar(100) NOT NULL,
            Icon nvarchar(40) NOT NULL,
            Description nvarchar(500) NULL,
            DisplayField nvarchar(50) NULL,
            IsArchived bit NOT NULL DEFAULT 0,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedAt datetime2(3) NULL
        );

        IF OBJECT_ID('meta.Field') IS NULL
        CREATE TABLE meta.Field (
            Id int IDENTITY PRIMARY KEY,
            EntityId int NOT NULL REFERENCES meta.Entity(Id),
            Name nvarchar(50) NOT NULL,
            Label nvarchar(100) NOT NULL,
            Type nvarchar(20) NOT NULL,
            IsRequired bit NOT NULL,
            IsUnique bit NOT NULL,
            MaxLength int NULL,
            [Precision] int NULL,
            Scale int NULL,
            OptionsJson nvarchar(max) NULL,
            ReferenceEntity nvarchar(50) NULL,
            DisplayOrder int NOT NULL,
            ShowInList bit NOT NULL,
            RequiredForRolesJson nvarchar(max) NULL,
            IsArchived bit NOT NULL DEFAULT 0,
            CONSTRAINT UX_Field_EntityName UNIQUE (EntityId, Name)
        );

        IF OBJECT_ID('meta.[User]') IS NULL
        CREATE TABLE meta.[User] (
            Id int IDENTITY PRIMARY KEY,
            Username nvarchar(100) NOT NULL CONSTRAINT UX_User_Username UNIQUE,
            DisplayName nvarchar(150) NOT NULL,
            PasswordHash nvarchar(400) NOT NULL,
            SecurityStamp uniqueidentifier NOT NULL DEFAULT NEWID(),
            IsActive bit NOT NULL DEFAULT 1,
            FailedAttempts int NOT NULL DEFAULT 0,
            LockoutEnd datetime2(0) NULL,
            LastLoginAt datetime2(0) NULL,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
        );

        IF OBJECT_ID('meta.Role') IS NULL
        CREATE TABLE meta.Role (
            Id int IDENTITY PRIMARY KEY,
            Name nvarchar(100) NOT NULL CONSTRAINT UX_Role_Name UNIQUE,
            Description nvarchar(300) NULL,
            IsSystem bit NOT NULL DEFAULT 0,
            IsSuperuser bit NOT NULL DEFAULT 0,
            CanManageModel bit NOT NULL DEFAULT 0,
            CanManageUsers bit NOT NULL DEFAULT 0,
            CanViewAudit bit NOT NULL DEFAULT 0
        );

        IF OBJECT_ID('meta.UserRole') IS NULL
        CREATE TABLE meta.UserRole (
            UserId int NOT NULL REFERENCES meta.[User](Id),
            RoleId int NOT NULL REFERENCES meta.Role(Id),
            PRIMARY KEY (UserId, RoleId)
        );

        IF OBJECT_ID('meta.EntityPermission') IS NULL
        CREATE TABLE meta.EntityPermission (
            RoleId int NOT NULL REFERENCES meta.Role(Id),
            EntityName nvarchar(50) NOT NULL,
            CanRead bit NOT NULL, CanCreate bit NOT NULL, CanUpdate bit NOT NULL, CanDelete bit NOT NULL,
            PRIMARY KEY (RoleId, EntityName)
        );

        IF OBJECT_ID('meta.ChangeSet') IS NULL
        CREATE TABLE meta.ChangeSet (
            Id int IDENTITY PRIMARY KEY,
            Description nvarchar(500) NOT NULL,
            PlanJson nvarchar(max) NOT NULL,
            SqlText nvarchar(max) NULL,
            OperationCount int NOT NULL,
            AppliedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            AppliedBy int NOT NULL REFERENCES meta.[User](Id)
        );

        IF OBJECT_ID('meta.AuditLog') IS NULL
        CREATE TABLE meta.AuditLog (
            Id bigint IDENTITY PRIMARY KEY,
            At datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            UserId int NULL,
            Username nvarchar(100) NULL,
            Action nvarchar(40) NOT NULL,
            EntityName nvarchar(100) NULL,
            RecordId nvarchar(50) NULL,
            BeforeJson nvarchar(max) NULL,
            AfterJson nvarchar(max) NULL,
            IpAddress nvarchar(45) NULL
        );

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuditLog_Entity')
            CREATE INDEX IX_AuditLog_Entity ON meta.AuditLog (EntityName, At DESC);

        -- ---- Spec 002: migración idempotente sobre bases creadas con la spec 001 ----
        IF COL_LENGTH('meta.Entity', 'WorkflowJson') IS NULL ALTER TABLE meta.Entity ADD WorkflowJson nvarchar(max) NULL;
        IF COL_LENGTH('meta.Entity', 'RulesJson') IS NULL ALTER TABLE meta.Entity ADD RulesJson nvarchar(max) NULL;
        IF COL_LENGTH('meta.Entity', 'GroupName') IS NULL ALTER TABLE meta.Entity ADD GroupName nvarchar(60) NULL;
        IF COL_LENGTH('meta.Field', 'DefaultValue') IS NULL ALTER TABLE meta.Field ADD DefaultValue nvarchar(1000) NULL;
        IF COL_LENGTH('meta.Field', 'Formula') IS NULL ALTER TABLE meta.Field ADD Formula nvarchar(1000) NULL;
        IF COL_LENGTH('meta.Field', 'ResultType') IS NULL ALTER TABLE meta.Field ADD ResultType nvarchar(20) NULL;
        IF COL_LENGTH('meta.Field', 'Prefix') IS NULL ALTER TABLE meta.Field ADD Prefix nvarchar(10) NULL;
        IF COL_LENGTH('meta.Field', 'Padding') IS NULL ALTER TABLE meta.Field ADD Padding int NULL;
        IF COL_LENGTH('meta.Field', 'ChildEntity') IS NULL ALTER TABLE meta.Field ADD ChildEntity nvarchar(50) NULL;
        IF COL_LENGTH('meta.Field', 'IsEditable') IS NULL ALTER TABLE meta.Field ADD IsEditable bit NOT NULL CONSTRAINT DF_Field_IsEditable DEFAULT 1;
        -- Spec 005
        IF COL_LENGTH('meta.Entity', 'AutomationsJson') IS NULL ALTER TABLE meta.Entity ADD AutomationsJson nvarchar(max) NULL;
        IF COL_LENGTH('meta.Field', 'LookupFilterJson') IS NULL ALTER TABLE meta.Field ADD LookupFilterJson nvarchar(400) NULL;
        -- Spec 006
        IF COL_LENGTH('meta.Entity', 'SecurityJson') IS NULL ALTER TABLE meta.Entity ADD SecurityJson nvarchar(max) NULL;
        IF COL_LENGTH('meta.[User]', 'Unit') IS NULL ALTER TABLE meta.[User] ADD Unit nvarchar(100) NULL;
        -- Spec 007
        IF COL_LENGTH('meta.Entity', 'LayoutJson') IS NULL ALTER TABLE meta.Entity ADD LayoutJson nvarchar(max) NULL;
        IF COL_LENGTH('meta.Entity', 'ViewsJson') IS NULL ALTER TABLE meta.Entity ADD ViewsJson nvarchar(max) NULL;
        IF COL_LENGTH('meta.Entity', 'ReportsJson') IS NULL ALTER TABLE meta.Entity ADD ReportsJson nvarchar(max) NULL;

        -- Spec 008
        IF COL_LENGTH('meta.Entity', 'WebhooksJson') IS NULL ALTER TABLE meta.Entity ADD WebhooksJson nvarchar(max) NULL;
        IF COL_LENGTH('meta.[User]', 'IsService') IS NULL ALTER TABLE meta.[User] ADD IsService bit NOT NULL CONSTRAINT DF_User_IsService DEFAULT 0;

        IF OBJECT_ID('meta.ApiKey') IS NULL
        CREATE TABLE meta.ApiKey (
            Id int IDENTITY PRIMARY KEY,
            Name nvarchar(100) NOT NULL,
            PublicId char(12) NOT NULL CONSTRAINT UX_ApiKey_PublicId UNIQUE,
            SecretHash varbinary(32) NOT NULL,
            PreviousSecretHash varbinary(32) NULL,
            PreviousExpiresAt datetime2(3) NULL,
            UserId int NOT NULL REFERENCES meta.[User](Id),
            CreatedBy int NOT NULL REFERENCES meta.[User](Id),
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            ExpiresAt datetime2(3) NULL,
            RotatedAt datetime2(3) NULL,
            RevokedAt datetime2(3) NULL,
            LastUsedAt datetime2(3) NULL
        );

        IF OBJECT_ID('meta.WebhookEndpoint') IS NULL
        CREATE TABLE meta.WebhookEndpoint (
            EntityName nvarchar(50) NOT NULL,
            WebhookName nvarchar(50) NOT NULL,
            UrlProtected nvarchar(max) NOT NULL,
            Destination nvarchar(300) NOT NULL,
            SecretProtected nvarchar(max) NOT NULL,
            IsActive bit NOT NULL,
            SecretCreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy int NOT NULL REFERENCES meta.[User](Id),
            PRIMARY KEY (EntityName, WebhookName)
        );

        IF OBJECT_ID('meta.WebhookDelivery') IS NULL
        CREATE TABLE meta.WebhookDelivery (
            Id bigint IDENTITY PRIMARY KEY,
            DeliveryId uniqueidentifier NOT NULL CONSTRAINT UX_WebhookDelivery_DeliveryId UNIQUE,
            EntityName nvarchar(50) NOT NULL,
            WebhookName nvarchar(50) NOT NULL,
            RecordId bigint NULL,
            Event nvarchar(20) NOT NULL,
            EventKey nvarchar(50) NULL,
            PayloadJson nvarchar(max) NOT NULL,
            Status nvarchar(12) NOT NULL,
            Attempts int NOT NULL DEFAULT 0,
            NextAttemptAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            LeaseUntil datetime2(3) NULL,
            LastStatusCode int NULL,
            LastError nvarchar(300) NULL,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            DeliveredAt datetime2(3) NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WebhookDelivery_Pending')
            CREATE INDEX IX_WebhookDelivery_Pending ON meta.WebhookDelivery (Status, NextAttemptAt);

        IF OBJECT_ID('meta.Dashboard') IS NULL
        CREATE TABLE meta.Dashboard (
            Name nvarchar(50) NOT NULL PRIMARY KEY,
            Label nvarchar(100) NOT NULL,
            DefinitionJson nvarchar(max) NOT NULL,
            IsArchived bit NOT NULL DEFAULT 0,
            UpdatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
        );

        IF OBJECT_ID('meta.SavedView') IS NULL
        CREATE TABLE meta.SavedView (
            Id int IDENTITY PRIMARY KEY,
            UserId int NOT NULL REFERENCES meta.[User](Id),
            EntityName nvarchar(50) NOT NULL,
            Label nvarchar(100) NOT NULL,
            DefinitionJson nvarchar(max) NOT NULL,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedAt datetime2(3) NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SavedView_User')
            CREATE INDEX IX_SavedView_User ON meta.SavedView (UserId, EntityName);

        IF OBJECT_ID('meta.Sequence') IS NULL
        CREATE TABLE meta.Sequence (
            EntityName nvarchar(50) NOT NULL,
            FieldName nvarchar(50) NOT NULL,
            NextValue bigint NOT NULL,
            PRIMARY KEY (EntityName, FieldName)
        );

        IF OBJECT_ID('meta.WorkflowHistory') IS NULL
        CREATE TABLE meta.WorkflowHistory (
            Id bigint IDENTITY PRIMARY KEY,
            EntityName nvarchar(50) NOT NULL,
            RecordId bigint NOT NULL,
            FromState nvarchar(50) NULL,
            ToState nvarchar(50) NOT NULL,
            Transition nvarchar(50) NULL,
            Comment nvarchar(1000) NULL,
            UserId int NOT NULL REFERENCES meta.[User](Id),
            At datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WorkflowHistory_Record')
            CREATE INDEX IX_WorkflowHistory_Record ON meta.WorkflowHistory (EntityName, RecordId, Id);

        IF OBJECT_ID('meta.Module') IS NULL
        CREATE TABLE meta.Module (
            Id nvarchar(50) NOT NULL PRIMARY KEY,
            Version nvarchar(20) NOT NULL,
            InstalledAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            InstalledBy int NOT NULL REFERENCES meta.[User](Id)
        );

        -- Constitución II.4: la auditoría es de solo inserción, incluso para la propia aplicación.
        IF OBJECT_ID('meta.TR_AuditLog_Immutable') IS NULL
            EXEC('CREATE TRIGGER meta.TR_AuditLog_Immutable ON meta.AuditLog INSTEAD OF UPDATE, DELETE AS
                  BEGIN THROW 51000, ''La bitácora de auditoría es inmutable.'', 1; END');
        """;
}
