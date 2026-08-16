/*
    Creates the two databases and the login the application signs in with, on SQL Server.

    Run once, as a principal that can create databases and logins (sysadmin, or dbcreator +
    securityadmin). Creates no tables: the schema is EF Core's, applied by the migrations.

    Windows authentication for the operator running it:

        sqlcmd -S <server> -E -i scripts/mssql-create-databases.sql ^
               -v AppPassword="<password>" ^
               -v AppLogin="ro_app" ^
               -v DbName="Echelon" ^
               -v ArchiveDbName="EchelonArchive"

    SQL authentication for the operator running it: replace -E with -U <admin> -P <admin-password>.

    The three names above are what docker-compose.yml and docs/en/configuration.md already use, so
    passing them verbatim leaves every connection string in this repository working.

    Every variable is required and none has a default. That is on purpose, twice over:

      - The password has no default because a password in a file in git is not a password.
      - The names have none because sqlcmd's :setvar takes precedence over -v, so a default
        written here would silently ignore whatever an operator passed on the command line and
        create databases under a name they did not ask for. Verified, not assumed. Do not add
        :setvar lines back to make this shorter.

    Undefined, sqlcmd stops with "'<name>' scripting variable not defined." and exit code 1 -
    before anything is created, which is what the first batch below is for and nothing else.
    Without it the databases were created and only then did the run stop on the missing password,
    leaving half a deployment behind. Found by running this, not by reading it.

    A password containing a single quote will not survive substitution - sqlcmd replaces the text
    before SQL parses it, so the quote ends the literal. Pick one without.

    Re-running is safe: every step checks first, and nothing here drops anything.
*/

:on error exit

SET NOCOUNT ON;
GO

/* ---------------------------------------------------------------------------
   0. Every variable, named before anything is created.

   sqlcmd substitutes as it reads each batch, so a missing variable stops the run at the batch
   that first mentions it - not at the top. The password is only needed in section 2, which meant
   forgetting it created both databases and then stopped: half a deployment, and a second run
   needed to finish it. Mentioning all four here moves that failure to before the first CREATE.

   The value is discarded. It exists to be substituted, not to be used.
   --------------------------------------------------------------------------- */

DECLARE @everyRequiredVariableIsDefined NVARCHAR(MAX) =
    N'$(AppLogin)/$(DbName)/$(ArchiveDbName)/$(AppPassword)';
GO

/* ---------------------------------------------------------------------------
   1. The databases.

   Two of them, deliberately: the archive holds tasks closed more than 90 days ago, so keeping it
   apart is what stops operational queries from reading two years of history and lets the archive
   be backed up on its own schedule. README §"Why Multiple Databases?".

   No collation is named, so the server's default applies. Worth one thought before accepting it:
   the default is usually case-insensitive, which makes the unique index on
   (ConnectionId, ExternalId) treat "group/Repo" and "group/repo" as one repository. It has never
   mattered here because those paths come from the provider rather than from typing. Name a
   collation if your server's default is not what you want; changing it afterwards is a rebuild.
   --------------------------------------------------------------------------- */

IF DB_ID(N'$(DbName)') IS NULL
BEGIN
    PRINT 'Creating database [$(DbName)]';
    EXEC (N'CREATE DATABASE [$(DbName)]');
END
ELSE
    PRINT 'Database [$(DbName)] already exists; leaving it alone';
GO

IF DB_ID(N'$(ArchiveDbName)') IS NULL
BEGIN
    PRINT 'Creating database [$(ArchiveDbName)]';
    EXEC (N'CREATE DATABASE [$(ArchiveDbName)]');
END
ELSE
    PRINT 'Database [$(ArchiveDbName)] already exists; leaving it alone';
GO

/* ---------------------------------------------------------------------------
   2. The login.

   Server-level, so it is created once and mapped into both databases below.

   CHECK_POLICY stays on: it hands password strength to the Windows policy the server already
   enforces, rather than to whoever ran this script. CHECK_EXPIRATION stays off - an expiring
   password on a service account means the service stops at an hour nobody chose.
   --------------------------------------------------------------------------- */

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(AppLogin)')
BEGIN
    PRINT 'Creating login [$(AppLogin)]';
    EXEC (N'CREATE LOGIN [$(AppLogin)]
            WITH PASSWORD = ''$(AppPassword)'',
                 CHECK_POLICY = ON,
                 CHECK_EXPIRATION = OFF,
                 DEFAULT_DATABASE = [$(DbName)]');
END
ELSE
    PRINT 'Login [$(AppLogin)] already exists; its password is left as it is';
GO

/* ---------------------------------------------------------------------------
   3. The user, in each database, and what it may do.

   These three roles, and not db_owner:

     db_ddladmin    CREATE/ALTER/DROP. Needed because the application applies its own migrations
                    at startup (Database__MigrateOnStartup=true).
     db_datareader  SELECT.
     db_datawriter  INSERT/UPDATE/DELETE - including the DataProtection key ring, which lives in
                    this database and is written on first run.

   db_owner would also work and is what most scripts reach for. It additionally allows dropping
   the database, changing its permissions and granting rights to others: none of which the
   application does, and all of which it could do for the life of the deployment.

   The honest part: db_ddladmin is still standing DDL rights on production. The application can
   drop every table it can read. That is what MigrateOnStartup costs, and it is why the code has
   it off by default - see the comment in Program.cs. To pay less:

     - leave Database__MigrateOnStartup=false,
     - apply the migrations from CI or an init container, with a separate privileged login,
     - and drop db_ddladmin from the two ALTER ROLE statements below. The application then holds
       read/write only, which is all it needs once the schema exists.

   With more than one replica that is not merely tidier but required: concurrent Migrate() calls
   race each other.
   --------------------------------------------------------------------------- */

USE [$(DbName)];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(AppLogin)')
BEGIN
    PRINT 'Creating user [$(AppLogin)] in [$(DbName)]';
    EXEC (N'CREATE USER [$(AppLogin)] FOR LOGIN [$(AppLogin)]');
END
ELSE
    PRINT 'User [$(AppLogin)] already exists in [$(DbName)]';
GO

ALTER ROLE db_ddladmin ADD MEMBER [$(AppLogin)];
ALTER ROLE db_datareader ADD MEMBER [$(AppLogin)];
ALTER ROLE db_datawriter ADD MEMBER [$(AppLogin)];
GO

USE [$(ArchiveDbName)];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(AppLogin)')
BEGIN
    PRINT 'Creating user [$(AppLogin)] in [$(ArchiveDbName)]';
    EXEC (N'CREATE USER [$(AppLogin)] FOR LOGIN [$(AppLogin)]');
END
ELSE
    PRINT 'User [$(AppLogin)] already exists in [$(ArchiveDbName)]';
GO

ALTER ROLE db_ddladmin ADD MEMBER [$(AppLogin)];
ALTER ROLE db_datareader ADD MEMBER [$(AppLogin)];
ALTER ROLE db_datawriter ADD MEMBER [$(AppLogin)];
GO

/* ---------------------------------------------------------------------------
   4. What to do next.

   ASCII only in these, deliberately: sqlcmd renders PRINT through the console code page, and an
   em-dash comes out as mojibake on a Russian-locale console.
   --------------------------------------------------------------------------- */

USE [master];
GO

PRINT '';
PRINT 'Done. Both databases exist and [$(AppLogin)] can reach them. There are no tables yet -';
PRINT 'the migrations create them.';
PRINT '';
PRINT 'Connection strings (replace <server> and the password):';
PRINT '';
PRINT '  ConnectionStrings__Default = Server=<server>;Database=$(DbName);User Id=$(AppLogin);Password=<password>;Encrypt=True;TrustServerCertificate=True';
PRINT '  ConnectionStrings__Archive = Server=<server>;Database=$(ArchiveDbName);User Id=$(AppLogin);Password=<password>;Encrypt=True;TrustServerCertificate=True';
PRINT '';
PRINT 'TrustServerCertificate=True accepts a self-signed certificate. Correct for a container on';
PRINT 'a private network, wrong against a server holding real tokens: install a certificate the';
PRINT 'client trusts and drop it.';
PRINT '';
PRINT 'Then set Database__Provider=sqlserver and Database__MigrateOnStartup=true, and start the';
PRINT 'application. It applies the migrations for both databases itself.';
GO
