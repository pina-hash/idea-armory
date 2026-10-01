using Npgsql;

namespace Armory.TestSupport.Tests;

public sealed class ArmoryTestDatabaseTests(TestDatabaseFixture fixture) : IClassFixture<TestDatabaseFixture>
{
    [Fact]
    public void IsAvailableFollowsTheEnvironment() =>
        Assert.Equal(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARMORY_TEST_POSTGRES")), ArmoryTestDatabase.IsAvailable);

    [PostgresFact]
    public async Task OpenAsSetsTheTestIdentityAndAdmins()
    {
        await using (var student = await fixture.Database.OpenAs(" Student@Example.com "))
        {
            Assert.Equal("student@example.com", await Harness.Command(student, "select public.current_user_email()").ExecuteScalarAsync());
            Assert.False((bool)(await Harness.Command(student, "select public.is_admin()").ExecuteScalarAsync())!);
        }
        await using var admin = await fixture.Database.OpenAs("Admin@Example.com", isAdmin: true);
        Assert.True((bool)(await Harness.Command(admin, "select public.is_admin()").ExecuteScalarAsync())!);
        var project = await Harness.Command(admin, "select armory_create_project($1,2027::smallint,$2)", "Robot " + Guid.NewGuid().ToString("N")[..8], Guid.NewGuid()).ExecuteScalarAsync();
        Assert.IsType<Guid>(project);
    }

    [PostgresFact]
    public async Task RunAsCallerUsesTheAuthenticatedOrAnonRoleInOneTransaction()
    {
        var (role, email) = await fixture.Database.RunAsCallerAsync("someone@example.com", ["boss@example.com"], async (connection, token) =>
        {
            await using var reader = await Harness.Command(connection, "select current_user::text, public.current_user_email()").ExecuteReaderAsync(token);
            await reader.ReadAsync(token);
            return (reader.GetString(0), reader.GetString(1));
        });
        Assert.Equal(("authenticated", "someone@example.com"), (role, email));

        var anon = await fixture.Database.RunAsCallerAsync<string>(null, null, async (connection, token) =>
            (string)(await Harness.Command(connection, "select current_user::text").ExecuteScalarAsync(token))!);
        Assert.Equal("anon", anon);

        var error = await Assert.ThrowsAsync<PostgresException>(() => fixture.Database.RunAsCallerAsync<object?>("someone@example.com", null,
            (connection, token) => Harness.Command(connection, "insert into armory_projects(name,season) values('Direct',2027)").ExecuteScalarAsync(token)));
        Assert.Equal("42501", error.SqlState); // no direct table writes for authenticated
    }

    [PostgresFact]
    public async Task IdentityFromOpenAsDoesNotSurviveThePool()
    {
        // OpenAs sets the identity for the whole session; the pool must reset it (and any
        // role a test switched to) before the connection serves anyone else, including the
        // superuser lookups the fakes make.
        for (var i = 0; i < 3; i++)
        {
            await using (var leaky = await fixture.Database.OpenAs("leak@example.com", isAdmin: true))
                await Harness.Command(leaky, "set role authenticated").ExecuteNonQueryAsync();
            await using var next = await fixture.Database.OpenAsync();
            Assert.Equal("", await Harness.Command(next, "select coalesce(current_setting('armory.test_email', true), '')").ExecuteScalarAsync());
            Assert.Equal("", await Harness.Command(next, "select coalesce(current_setting('armory.test_admins', true), '')").ExecuteScalarAsync());
            Assert.Null(await Harness.Command(next, "select public.current_user_email()").ExecuteScalarAsync() as string);
            Assert.True((bool)(await Harness.Command(next, "select rolsuper from pg_roles where rolname = current_user").ExecuteScalarAsync())!);
        }
    }

    [PostgresFact]
    public async Task CreateAppliesTheSqlAndDisposeDropsOnlyThatDatabase()
    {
        var database = await ArmoryTestDatabase.CreateAsync();
        Assert.StartsWith("armory_ts_", database.DatabaseName);
        Assert.Contains("Include Error Detail=True", database.ConnectionString, StringComparison.OrdinalIgnoreCase);
        await using (var connection = await database.OpenAsync())
        {
            Assert.Equal(1L, await Harness.Command(connection, "select count(*) from pg_proc where proname='armory_create_file'").ExecuteScalarAsync());
            Assert.Equal(2L, await Harness.Command(connection, "select count(*) from pg_roles where rolname in ('anon','authenticated')").ExecuteScalarAsync());
        }
        await database.DisposeAsync();
        await database.DisposeAsync(); // idempotent

        var admin = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ARMORY_TEST_POSTGRES")) { Database = "postgres" }.ConnectionString;
        await using var server = new NpgsqlConnection(admin);
        await server.OpenAsync();
        Assert.Equal(0L, await Harness.Command(server, "select count(*) from pg_database where datname=$1", database.DatabaseName).ExecuteScalarAsync());
        Assert.Equal(1L, await Harness.Command(server, "select count(*) from pg_database where datname=$1", fixture.Database.DatabaseName).ExecuteScalarAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => database.OpenAsync());
    }
}
