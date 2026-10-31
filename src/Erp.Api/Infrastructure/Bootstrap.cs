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
        await EnsureScheduleAccountAsync(cn, services.GetRequiredService<PasswordService>());
    }

    /// <summary>
    /// Spec 016: cuenta de sistema a cuyo nombre quedan los registros que crean o modifican las tareas programadas. Es de
    /// servicio (login la rechaza), no tiene roles y su contraseña aleatoria nunca se conoce. Se crea después del
    /// administrador inicial, que solo se siembra en una base sin usuarios.
    /// </summary>
    private static async Task EnsureScheduleAccountAsync(SqlConnection cn, PasswordService passwords)
    {
        if (await cn.ExecuteScalarAsync<int?>("SELECT 1 FROM meta.[User] WHERE Username = @u", new { u = ScheduleAccount.Username }) is not null) return;
        await cn.ExecuteAsync("""
            INSERT meta.[User] (Username, DisplayName, PasswordHash, IsService) VALUES (@u, @name, @hash, 1);
            INSERT meta.UserCompany (UserId, CompanyId) SELECT u.Id, c.Id FROM meta.[User] u CROSS JOIN meta.Company c WHERE u.Username = @u AND c.IsDefault = 1;
            """, new { u = ScheduleAccount.Username, name = ScheduleAccount.DisplayName, hash = passwords.Hash(GeneratePassword() + GeneratePassword()) });
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

        // Spec 010: toda instalación tiene una empresa predeterminada, y todo usuario sin empresas queda en ella. Así una
        // base existente sigue igual: sus registros pasan a esa empresa cuando un catálogo se vuelve «por empresa».
        await cn.ExecuteAsync("""
            IF NOT EXISTS (SELECT 1 FROM meta.Company)
                INSERT meta.Company (Code, Name, BaseCurrency, IsDefault) VALUES (N'PRINCIPAL', N'Empresa principal', @currency, 1);
            INSERT meta.UserCompany (UserId, CompanyId)
                SELECT u.Id, c.Id FROM meta.[User] u CROSS JOIN meta.Company c
                 WHERE c.IsDefault = 1 AND NOT EXISTS (SELECT 1 FROM meta.UserCompany uc WHERE uc.UserId = u.Id);
            """, new { currency = DefaultCurrency(config) });

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
        await cn.ExecuteAsync("INSERT meta.UserCompany (UserId, CompanyId) SELECT @id, Id FROM meta.Company WHERE IsDefault = 1", new { id });

        if (string.IsNullOrEmpty(configured))
            // Se muestra una única vez en la consola local. Cámbiala al entrar.
            log.LogWarning("Usuario inicial creado: admin  /  contraseña: {Password}  (solo se muestra esta vez)", password);
        else
            log.LogInformation("Usuario inicial 'admin' creado con la contraseña configurada.");
    }

    /// <summary>Moneda base de la empresa predeterminada al crearla (código ISO de 3 letras).</summary>
    private static string DefaultCurrency(IConfiguration config)
    {
        var c = (config["Companies:DefaultCurrency"] ?? "MXN").Trim().ToUpperInvariant();
        return Companies.IsCurrency(c) ? c : throw new InvalidOperationException("Companies:DefaultCurrency debe ser un código ISO de 3 letras.");
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

        IF OBJECT_ID('meta.App') IS NULL
        CREATE TABLE meta.App (
            Name nvarchar(60) NOT NULL PRIMARY KEY,
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

        -- Spec 009: paquetes de metadatos recibidos (con su revisión) y exportados.
        IF OBJECT_ID('meta.PackageImport') IS NULL
        CREATE TABLE meta.PackageImport (
            Id int IDENTITY PRIMARY KEY,
            PackageId nvarchar(50) NOT NULL,
            Name nvarchar(100) NOT NULL,
            Version nvarchar(20) NOT NULL,
            Description nvarchar(500) NULL,
            SourceEnvironment nvarchar(100) NULL,
            ExportedAt datetime2(3) NULL,
            ContentJson nvarchar(max) NOT NULL,
            ContentHash char(64) NOT NULL,
            Signed bit NOT NULL,
            Status nvarchar(12) NOT NULL,
            PreviousVersion nvarchar(20) NULL,
            UploadedBy int NOT NULL REFERENCES meta.[User](Id),
            UploadedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            ReviewedBy int NULL REFERENCES meta.[User](Id),
            ReviewedAt datetime2(3) NULL,
            ReviewComment nvarchar(500) NULL,
            ApprovedDiffHash char(64) NULL,
            AppliedBy int NULL REFERENCES meta.[User](Id),
            AppliedAt datetime2(3) NULL,
            ChangeSetId int NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PackageImport_Hash')
            CREATE INDEX IX_PackageImport_Hash ON meta.PackageImport (ContentHash, Status);

        IF OBJECT_ID('meta.PackageExport') IS NULL
        CREATE TABLE meta.PackageExport (
            Id int IDENTITY PRIMARY KEY,
            PackageId nvarchar(50) NOT NULL,
            Name nvarchar(100) NOT NULL,
            Version nvarchar(20) NOT NULL,
            ContentHash char(64) NOT NULL,
            Signed bit NOT NULL,
            EntitiesJson nvarchar(max) NOT NULL,
            ExportedBy int NOT NULL REFERENCES meta.[User](Id),
            ExportedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
        );

        -- Spec 010: empresas, empresa actual de cada usuario, tipos de cambio y catálogos por empresa.
        IF COL_LENGTH('meta.Entity', 'PerCompany') IS NULL ALTER TABLE meta.Entity ADD PerCompany bit NOT NULL CONSTRAINT DF_Entity_PerCompany DEFAULT 0;

        IF OBJECT_ID('meta.Company') IS NULL
        CREATE TABLE meta.Company (
            Id int IDENTITY PRIMARY KEY,
            Code nvarchar(20) NOT NULL CONSTRAINT UX_Company_Code UNIQUE,
            Name nvarchar(150) NOT NULL,
            BaseCurrency char(3) NOT NULL,
            IsDefault bit NOT NULL DEFAULT 0,
            IsActive bit NOT NULL DEFAULT 1,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedAt datetime2(3) NULL
        );

        IF OBJECT_ID('meta.UserCompany') IS NULL
        CREATE TABLE meta.UserCompany (
            UserId int NOT NULL REFERENCES meta.[User](Id),
            CompanyId int NOT NULL REFERENCES meta.Company(Id),
            PRIMARY KEY (UserId, CompanyId)
        );

        IF COL_LENGTH('meta.[User]', 'CompanyId') IS NULL
            ALTER TABLE meta.[User] ADD CompanyId int NULL CONSTRAINT FK_User_Company REFERENCES meta.Company(Id);

        IF OBJECT_ID('meta.ExchangeRate') IS NULL
        CREATE TABLE meta.ExchangeRate (
            Id int IDENTITY PRIMARY KEY,
            FromCurrency char(3) NOT NULL,
            ToCurrency char(3) NOT NULL,
            RateDate date NOT NULL,
            Rate decimal(19,8) NOT NULL,
            UpdatedBy int NOT NULL REFERENCES meta.[User](Id),
            UpdatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            CONSTRAINT UX_ExchangeRate_Pair UNIQUE (FromCurrency, ToCurrency, RateDate)
        );

        -- Spec 011: solicitudes al asistente IA con su propuesta (el texto pedido, el plan y la huella del contexto enviado).
        IF OBJECT_ID('meta.AssistantRequest') IS NULL
        CREATE TABLE meta.AssistantRequest (
            Id int IDENTITY PRIMARY KEY,
            ConversationId int NOT NULL,
            UserId int NOT NULL REFERENCES meta.[User](Id),
            Prompt nvarchar(max) NOT NULL,
            Status nvarchar(12) NOT NULL,
            Explanation nvarchar(max) NULL,
            PlanJson nvarchar(max) NULL,
            ErrorsJson nvarchar(max) NULL,
            ResponseJson nvarchar(max) NULL,
            ErrorCode nvarchar(30) NULL,
            Model nvarchar(100) NOT NULL,
            Attempts int NOT NULL,
            InputTokens int NOT NULL,
            OutputTokens int NOT NULL,
            ContextHash char(64) NOT NULL,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            AppliedAt datetime2(3) NULL,
            ChangeSetId int NULL
        );
        -- Spec 011: clave de Anthropic guardada desde la app, cifrada con Data Protection (una sola fila).
        IF OBJECT_ID('meta.AssistantKey') IS NULL
        CREATE TABLE meta.AssistantKey (
            Id int NOT NULL PRIMARY KEY CONSTRAINT CK_AssistantKey_One CHECK (Id = 1),
            KeyProtected nvarchar(max) NOT NULL,
            UpdatedBy int NOT NULL REFERENCES meta.[User](Id),
            UpdatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
        );
        IF COL_LENGTH('meta.AssistantKey', 'WorkspaceId') IS NULL ALTER TABLE meta.AssistantKey ADD WorkspaceId nvarchar(100) NULL;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AssistantRequest_User')
            CREATE INDEX IX_AssistantRequest_User ON meta.AssistantRequest (UserId, CreatedAt);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AssistantRequest_Conversation')
            CREATE INDEX IX_AssistantRequest_Conversation ON meta.AssistantRequest (ConversationId);

        -- Spec 015: unidades en árbol (ruta materializada con ids: '/1/4/'), equipos, herencia de roles y solicitudes de
        -- cambios de seguridad. La unidad del usuario sigue siendo el nombre (CURRENTUNIT() y reglas de la 006 sin cambios).
        IF OBJECT_ID('meta.BusinessUnit') IS NULL
        CREATE TABLE meta.BusinessUnit (
            Id int IDENTITY PRIMARY KEY,
            Name nvarchar(100) NOT NULL CONSTRAINT UX_BusinessUnit_Name UNIQUE,
            ParentId int NULL CONSTRAINT FK_BusinessUnit_Parent REFERENCES meta.BusinessUnit(Id),
            Path nvarchar(900) NOT NULL,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BusinessUnit_Path')
            CREATE INDEX IX_BusinessUnit_Path ON meta.BusinessUnit (Path);
        -- Las etiquetas que ya tenían los usuarios (spec 006) pasan a ser unidades raíz.
        INSERT meta.BusinessUnit (Name, Path)
            SELECT DISTINCT u.Unit, N'' FROM meta.[User] u
             WHERE u.Unit IS NOT NULL AND NOT EXISTS (SELECT 1 FROM meta.BusinessUnit b WHERE b.Name = u.Unit);
        UPDATE meta.BusinessUnit SET Path = N'/' + CAST(Id AS nvarchar(12)) + N'/' WHERE Path = N'';

        IF COL_LENGTH('meta.[User]', 'IsTeam') IS NULL ALTER TABLE meta.[User] ADD IsTeam bit NOT NULL CONSTRAINT DF_User_IsTeam DEFAULT 0;

        IF OBJECT_ID('meta.TeamMember') IS NULL
        CREATE TABLE meta.TeamMember (
            TeamId int NOT NULL REFERENCES meta.[User](Id),
            UserId int NOT NULL REFERENCES meta.[User](Id),
            PRIMARY KEY (TeamId, UserId)
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_TeamMember_User')
            CREATE INDEX IX_TeamMember_User ON meta.TeamMember (UserId, TeamId);

        IF OBJECT_ID('meta.RoleInherit') IS NULL
        CREATE TABLE meta.RoleInherit (
            RoleId int NOT NULL REFERENCES meta.Role(Id),
            ParentRoleId int NOT NULL REFERENCES meta.Role(Id),
            PRIMARY KEY (RoleId, ParentRoleId)
        );

        IF OBJECT_ID('meta.SecurityChange') IS NULL
        CREATE TABLE meta.SecurityChange (
            Id int IDENTITY PRIMARY KEY,
            Kind nvarchar(20) NOT NULL,
            TargetId int NULL,
            Summary nvarchar(400) NOT NULL,
            PayloadJson nvarchar(max) NOT NULL,
            BeforeJson nvarchar(max) NULL,
            Hash char(64) NOT NULL,
            Status nvarchar(10) NOT NULL,
            RequestedBy int NOT NULL REFERENCES meta.[User](Id),
            RequestedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            ReviewedBy int NULL REFERENCES meta.[User](Id),
            ReviewedAt datetime2(3) NULL,
            ReviewComment nvarchar(500) NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SecurityChange_Status')
            CREATE INDEX IX_SecurityChange_Status ON meta.SecurityChange (Status, Id);

        -- Spec 016: tareas programadas (qué hacen es metadato; si corren, de cada entorno) y bandeja de automatizaciones.
        IF COL_LENGTH('meta.Entity', 'SchedulesJson') IS NULL ALTER TABLE meta.Entity ADD SchedulesJson nvarchar(max) NULL;

        -- Spec 020: libro de partida doble (metadato) y capas de costo (estado interno del motor, sin API).
        IF COL_LENGTH('meta.Entity', 'LedgerJson') IS NULL ALTER TABLE meta.Entity ADD LedgerJson nvarchar(max) NULL;
        IF OBJECT_ID('meta.CostLayer') IS NULL
        CREATE TABLE meta.CostLayer (
            Id bigint IDENTITY PRIMARY KEY,
            Book nvarchar(63) NOT NULL,
            KeyHash char(64) NOT NULL,
            CompanyId int NULL,
            SourceEntity nvarchar(63) NOT NULL,
            SourceId bigint NOT NULL,
            Quantity decimal(28,8) NOT NULL,
            Value decimal(28,8) NOT NULL,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CostLayer_Open')
            CREATE INDEX IX_CostLayer_Open ON meta.CostLayer (Book, KeyHash, Id) INCLUDE (Quantity, Value) WHERE Quantity > 0;

        IF OBJECT_ID('meta.ScheduleState') IS NULL
        CREATE TABLE meta.ScheduleState (
            EntityName nvarchar(63) NOT NULL,
            ScheduleName nvarchar(63) NOT NULL,
            IsActive bit NOT NULL DEFAULT 0,
            CalendarJson nvarchar(400) NULL,
            NextRunAt datetime2(3) NULL,
            LastRunId bigint NULL,
            UpdatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy int NULL REFERENCES meta.[User](Id),
            PRIMARY KEY (EntityName, ScheduleName)
        );

        IF OBJECT_ID('meta.AutomationRun') IS NULL
        CREATE TABLE meta.AutomationRun (
            Id bigint IDENTITY PRIMARY KEY,
            Kind nvarchar(10) NOT NULL,
            EntityName nvarchar(63) NOT NULL,
            Name nvarchar(63) NOT NULL,
            RecordId bigint NULL,
            Event nvarchar(20) NULL,
            EventKey nvarchar(63) NULL,
            TriggeredBy int NULL REFERENCES meta.[User](Id),
            RunAsRole nvarchar(100) NULL,
            RunAsName nvarchar(150) NOT NULL,
            CompanyId int NULL,
            Generation int NOT NULL DEFAULT 0,
            Manual bit NOT NULL DEFAULT 0,
            Status nvarchar(12) NOT NULL,
            Attempts int NOT NULL DEFAULT 0,
            NextAttemptAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            LeaseUntil datetime2(3) NULL,
            StartedAt datetime2(3) NULL,
            FinishedAt datetime2(3) NULL,
            Processed int NOT NULL DEFAULT 0,
            Succeeded int NOT NULL DEFAULT 0,
            Failed int NOT NULL DEFAULT 0,
            Skipped int NOT NULL DEFAULT 0,
            More bit NOT NULL DEFAULT 0,
            LastError nvarchar(500) NULL,
            ErrorsJson nvarchar(max) NULL,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AutomationRun_Pending')
            CREATE INDEX IX_AutomationRun_Pending ON meta.AutomationRun (Status, NextAttemptAt);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AutomationRun_Name')
            CREATE INDEX IX_AutomationRun_Name ON meta.AutomationRun (Kind, EntityName, Name, Id);

        -- Spec 017: instantáneas de lo que un paquete puede tocar (antes y después de aplicarlo) y solicitudes de reversión.
        IF COL_LENGTH('meta.PackageImport', 'Kind') IS NULL
            ALTER TABLE meta.PackageImport ADD Kind nvarchar(10) NOT NULL CONSTRAINT DF_PackageImport_Kind DEFAULT 'package';
        IF COL_LENGTH('meta.PackageImport', 'RevertOf') IS NULL
            ALTER TABLE meta.PackageImport ADD RevertOf int NULL CONSTRAINT FK_PackageImport_RevertOf REFERENCES meta.PackageImport(Id);
        IF COL_LENGTH('meta.PackageImport', 'TargetVersion') IS NULL ALTER TABLE meta.PackageImport ADD TargetVersion nvarchar(20) NULL;
        IF COL_LENGTH('meta.PackageImport', 'RevertedBy') IS NULL ALTER TABLE meta.PackageImport ADD RevertedBy int NULL;

        IF OBJECT_ID('meta.ModelSnapshot') IS NULL
        CREATE TABLE meta.ModelSnapshot (
            Id int IDENTITY PRIMARY KEY,
            ImportId int NOT NULL REFERENCES meta.PackageImport(Id),
            Moment nvarchar(6) NOT NULL,
            ContentJson nvarchar(max) NOT NULL,
            ContentHash char(64) NOT NULL,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            CONSTRAINT UX_ModelSnapshot_Import UNIQUE (ImportId, Moment)
        );

        -- Spec 018: clientes OAuth (client credentials) y sus tokens opacos (solo el SHA-256 de secreto y token).
        IF OBJECT_ID('meta.OAuthClient') IS NULL
        CREATE TABLE meta.OAuthClient (
            Id int IDENTITY PRIMARY KEY,
            Name nvarchar(100) NOT NULL,
            ClientId char(20) NOT NULL CONSTRAINT UX_OAuthClient_ClientId UNIQUE,
            SecretHash varbinary(32) NOT NULL,
            PreviousSecretHash varbinary(32) NULL,
            PreviousExpiresAt datetime2(3) NULL,
            Scopes nvarchar(200) NOT NULL,
            TokenMinutes int NOT NULL,
            UserId int NOT NULL REFERENCES meta.[User](Id),
            CreatedBy int NOT NULL REFERENCES meta.[User](Id),
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            RotatedAt datetime2(3) NULL,
            RevokedAt datetime2(3) NULL,
            LastTokenAt datetime2(3) NULL
        );
        IF OBJECT_ID('meta.OAuthToken') IS NULL
        CREATE TABLE meta.OAuthToken (
            Id bigint IDENTITY PRIMARY KEY,
            ClientId int NOT NULL REFERENCES meta.OAuthClient(Id),
            TokenHash varbinary(32) NOT NULL CONSTRAINT UX_OAuthToken_Hash UNIQUE,
            Scopes nvarchar(200) NOT NULL,
            IssuedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            ExpiresAt datetime2(3) NOT NULL,
            RevokedAt datetime2(3) NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OAuthToken_Client' AND object_id = OBJECT_ID('meta.OAuthToken'))
            CREATE INDEX IX_OAuthToken_Client ON meta.OAuthToken (ClientId, ExpiresAt);

        -- Spec 021: cuándo se reclamó una solicitud de seguridad (una atorada en 'applying' se libera), dueño del lease de
        -- un trabajo en segundo plano y huella del contenido de una tarea programada al activarla.
        IF COL_LENGTH('meta.SecurityChange', 'ClaimedAt') IS NULL ALTER TABLE meta.SecurityChange ADD ClaimedAt datetime2(3) NULL;
        IF COL_LENGTH('meta.AutomationRun', 'LeaseToken') IS NULL ALTER TABLE meta.AutomationRun ADD LeaseToken uniqueidentifier NULL;
        IF COL_LENGTH('meta.ScheduleState', 'ContentHash') IS NULL ALTER TABLE meta.ScheduleState ADD ContentHash char(64) NULL;

        -- Spec 027: plantillas de impresión (metadato) y archivos de imagen del entorno (logos e imágenes de plantillas).
        -- Los archivos son datos del entorno: no viajan en paquetes. Reemplazar uno archiva el anterior (no se borra).
        IF COL_LENGTH('meta.Entity', 'PrintTemplatesJson') IS NULL ALTER TABLE meta.Entity ADD PrintTemplatesJson nvarchar(max) NULL;
        IF OBJECT_ID('meta.[File]') IS NULL
        CREATE TABLE meta.[File] (
            Id int IDENTITY PRIMARY KEY,
            Purpose nvarchar(10) NOT NULL,
            FileKey nvarchar(63) NULL,
            ContentType nvarchar(20) NOT NULL,
            Size int NOT NULL,
            Width int NOT NULL,
            Height int NOT NULL,
            Sha256 char(64) NOT NULL,
            Content varbinary(max) NOT NULL,
            IsArchived bit NOT NULL DEFAULT 0,
            CreatedBy int NOT NULL REFERENCES meta.[User](Id),
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            ArchivedAt datetime2(3) NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_File_Key')
            CREATE UNIQUE INDEX UX_File_Key ON meta.[File] (FileKey) WHERE FileKey IS NOT NULL AND IsArchived = 0;
        IF COL_LENGTH('meta.Company', 'LogoFileId') IS NULL
            ALTER TABLE meta.Company ADD LogoFileId int NULL CONSTRAINT FK_Company_Logo REFERENCES meta.[File](Id);
        IF COL_LENGTH('meta.Company', 'TaxId') IS NULL ALTER TABLE meta.Company ADD TaxId nvarchar(20) NULL;
        IF COL_LENGTH('meta.Company', 'Address') IS NULL ALTER TABLE meta.Company ADD Address nvarchar(500) NULL;

        -- Spec 033: sesiones de ERP AI Studio (un trabajador con lease por sesión) y sus pasos (avance visible).
        IF OBJECT_ID('meta.StudioSession') IS NULL
        CREATE TABLE meta.StudioSession (
            Id int IDENTITY PRIMARY KEY,
            UserId int NOT NULL REFERENCES meta.[User](Id),
            CompanyId int NULL,
            SecurityStamp uniqueidentifier NOT NULL,
            Prompt nvarchar(4000) NOT NULL,
            Scopes nvarchar(100) NOT NULL,
            Status nvarchar(12) NOT NULL,
            CancelRequested bit NOT NULL DEFAULT 0,
            LeaseToken uniqueidentifier NULL,
            LeaseUntil datetime2(3) NULL,
            Steps int NOT NULL DEFAULT 0,
            ToolCalls int NOT NULL DEFAULT 0,
            InputTokens bigint NOT NULL DEFAULT 0,
            OutputTokens bigint NOT NULL DEFAULT 0,
            CostMicros bigint NOT NULL DEFAULT 0,
            ReservedMicros bigint NOT NULL DEFAULT 0,
            Model nvarchar(100) NOT NULL,
            ErrorCode nvarchar(30) NULL,
            Summary nvarchar(max) NULL,
            PlanJson nvarchar(max) NULL,
            ReportJson nvarchar(max) NULL,
            SandboxDb nvarchar(128) NULL,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            StartedAt datetime2(3) NULL,
            FinishedAt datetime2(3) NULL,
            AppliedAt datetime2(3) NULL,
            ChangeSetId int NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StudioSession_User')
            CREATE INDEX IX_StudioSession_User ON meta.StudioSession (UserId, CreatedAt);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StudioSession_Status')
            CREATE INDEX IX_StudioSession_Status ON meta.StudioSession (Status);
        IF OBJECT_ID('meta.StudioStep') IS NULL
        CREATE TABLE meta.StudioStep (
            Id bigint IDENTITY PRIMARY KEY,
            SessionId int NOT NULL REFERENCES meta.StudioSession(Id),
            Seq int NOT NULL,
            Kind nvarchar(12) NOT NULL,
            Tool nvarchar(60) NULL,
            InputJson nvarchar(max) NULL,
            OutputJson nvarchar(max) NULL,
            Status nvarchar(10) NOT NULL,
            Summary nvarchar(400) NOT NULL,
            DurationMs int NOT NULL DEFAULT 0,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            CONSTRAINT UX_StudioStep_Seq UNIQUE (SessionId, Seq)
        );
        -- Spec 035 (R-033-4; revisión adversarial F-01): lo que leyeron las herramientas de «datos» en sesiones ya cerradas
        -- antes de la 035 también se borra (idempotente; las nuevas se purgan al cerrar). La lista es StudioTools.DataTools.
        UPDATE meta.StudioStep SET OutputJson = NULL
         WHERE Tool IN ('leer_registros', 'leer_registro') AND OutputJson IS NOT NULL
           AND SessionId IN (SELECT Id FROM meta.StudioSession WHERE Status NOT IN ('queued', 'running'));

        -- Spec 036: Studio como chat. Varios turnos por sesión (conversación), la propuesta separada del estado del turno,
        -- el estado en vivo que lee el flujo SSE, la marca de la copia aislada que se conserva entre turnos y el historial
        -- exacto que se manda al modelo.
        IF COL_LENGTH('meta.StudioSession', 'Turns') IS NULL ALTER TABLE meta.StudioSession ADD Turns int NOT NULL DEFAULT 1;
        IF COL_LENGTH('meta.StudioSession', 'PlanStatus') IS NULL
        BEGIN
            ALTER TABLE meta.StudioSession ADD PlanStatus nvarchar(10) NULL;
            EXEC('UPDATE meta.StudioSession SET PlanStatus = Status WHERE PlanJson IS NOT NULL AND Status IN (''proposed'', ''applied'', ''discarded'')');
        END
        IF COL_LENGTH('meta.StudioSession', 'LiveJson') IS NULL ALTER TABLE meta.StudioSession ADD LiveJson nvarchar(max) NULL;
        IF COL_LENGTH('meta.StudioSession', 'SandboxToken') IS NULL ALTER TABLE meta.StudioSession ADD SandboxToken uniqueidentifier NULL;
        IF COL_LENGTH('meta.StudioSession', 'TurnStartedAt') IS NULL ALTER TABLE meta.StudioSession ADD TurnStartedAt datetime2(3) NULL;
        IF COL_LENGTH('meta.StudioSession', 'LastActivityAt') IS NULL
            ALTER TABLE meta.StudioSession ADD LastActivityAt datetime2(3) NOT NULL CONSTRAINT DF_StudioSession_Activity DEFAULT SYSUTCDATETIME();
        IF COL_LENGTH('meta.StudioStep', 'Turn') IS NULL ALTER TABLE meta.StudioStep ADD Turn int NOT NULL DEFAULT 1;
        IF OBJECT_ID('meta.StudioMessage') IS NULL
        CREATE TABLE meta.StudioMessage (
            Id bigint IDENTITY PRIMARY KEY,
            SessionId int NOT NULL REFERENCES meta.StudioSession(Id),
            Seq int NOT NULL,
            FromUser bit NOT NULL,
            ContentJson nvarchar(max) NOT NULL,
            HasData bit NOT NULL DEFAULT 0,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            CONSTRAINT UX_StudioMessage_Seq UNIQUE (SessionId, Seq)
        );

        -- Spec 038: servidor MCP por persona. Clientes públicos registrados (RFC 7591), códigos de autorización (un uso, PKCE),
        -- conexiones (el consentimiento de una persona a un cliente, con su sello) y sus tokens (solo el SHA-256).
        IF OBJECT_ID('meta.McpClient') IS NULL
        CREATE TABLE meta.McpClient (
            Id int IDENTITY PRIMARY KEY,
            ClientId varchar(40) NOT NULL CONSTRAINT UX_McpClient_ClientId UNIQUE,
            Name nvarchar(100) NOT NULL,
            RedirectUris nvarchar(max) NOT NULL,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            CreatedIp varchar(45) NULL,
            LastAuthorizedAt datetime2(3) NULL
        );
        IF OBJECT_ID('meta.McpGrant') IS NULL
        CREATE TABLE meta.McpGrant (
            Id int IDENTITY PRIMARY KEY,
            ClientId int NOT NULL REFERENCES meta.McpClient(Id),
            UserId int NOT NULL REFERENCES meta.[User](Id),
            SecurityStamp uniqueidentifier NOT NULL,
            Scopes nvarchar(60) NOT NULL,
            Resource nvarchar(500) NOT NULL,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            LastUsedAt datetime2(3) NULL,
            RevokedAt datetime2(3) NULL,
            RevokedReason nvarchar(20) NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_McpGrant_User')
            CREATE INDEX IX_McpGrant_User ON meta.McpGrant (UserId, RevokedAt);
        -- Spec 040 (revisión adversarial F-02): el barrido y los topes del registro buscan las conexiones por cliente.
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_McpGrant_Client')
            CREATE INDEX IX_McpGrant_Client ON meta.McpGrant (ClientId) INCLUDE (RevokedAt);
        IF OBJECT_ID('meta.McpCode') IS NULL
        CREATE TABLE meta.McpCode (
            Id bigint IDENTITY PRIMARY KEY,
            CodeHash varbinary(32) NOT NULL CONSTRAINT UX_McpCode_Hash UNIQUE,
            ClientId int NOT NULL REFERENCES meta.McpClient(Id),
            UserId int NOT NULL REFERENCES meta.[User](Id),
            SecurityStamp uniqueidentifier NOT NULL,
            Scopes nvarchar(60) NOT NULL,
            RedirectUri nvarchar(500) NOT NULL,
            Challenge varchar(128) NOT NULL,
            Resource nvarchar(500) NOT NULL,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
            ExpiresAt datetime2(3) NOT NULL,
            UsedAt datetime2(3) NULL,
            GrantId int NULL REFERENCES meta.McpGrant(Id)
        );
        IF OBJECT_ID('meta.McpToken') IS NULL
        CREATE TABLE meta.McpToken (
            Id bigint IDENTITY PRIMARY KEY,
            GrantId int NOT NULL REFERENCES meta.McpGrant(Id),
            Kind char(1) NOT NULL,
            TokenHash varbinary(32) NOT NULL CONSTRAINT UX_McpToken_Hash UNIQUE,
            ExpiresAt datetime2(3) NOT NULL,
            UsedAt datetime2(3) NULL,
            CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_McpToken_Grant')
            CREATE INDEX IX_McpToken_Grant ON meta.McpToken (GrantId, ExpiresAt);

        -- Constitución II.4: la auditoría es de solo inserción, incluso para la propia aplicación.
        IF OBJECT_ID('meta.TR_AuditLog_Immutable') IS NULL
            EXEC('CREATE TRIGGER meta.TR_AuditLog_Immutable ON meta.AuditLog INSTEAD OF UPDATE, DELETE AS
                  BEGIN THROW 51000, ''La bitácora de auditoría es inmutable.'', 1; END');
        """;
}
