using System.Text.Json.Nodes;
using Npgsql;

namespace Armory.TestSupport;

/// <summary>
/// Opt-in test stand-ins for the parts of idea-app migration 0233 the Windows app calls
/// (<c>sql-v3/*.sql</c>), written from ARMORY.md's v0.3 contract section. Not the deployed SQL:
/// they answer the way that section says (shapes, SQLSTATEs, DETAIL reasons) so the agent can be
/// tested against them. <see cref="ApplyCoreAsync"/> adds can_take_back, armory_project_purged,
/// armory_heartbeat and the batch RPCs; <see cref="ApplyReportsAsync"/> the feedback and incident
/// RPCs with their limits. The purge helpers leave what a successful purge leaves. The later
/// migrations' functions are copied from their SQL as they are (0234:
/// <see cref="ApplyBreakLocksAsync"/>; 0235: <see cref="ApplyFeedbackV2Async"/>).
/// </summary>
public static class ArmoryV3StandIn
{
    public static Task ApplyCoreAsync(ArmoryTestDatabase database) => ApplyAsync(database, "armory_v3_core.sql");

    public static Task ApplyReportsAsync(ArmoryTestDatabase database) => ApplyAsync(database, "armory_v3_reports.sql");

    /// <summary>
    /// idea-app 0234 (<c>armory_v3_break_locks.sql</c>): armory_break_locks, 0234's body as it is,
    /// over this database's armory_break_lock.
    /// </summary>
    public static Task ApplyBreakLocksAsync(ArmoryTestDatabase database) => ApplyAsync(database, "armory_v3_break_locks.sql");

    /// <summary>
    /// idea-app 0235 (<c>armory_v3_feedback_v2.sql</c>), after the reports stand-in (applied here
    /// first): the eight-argument armory_submit_app_feedback, the five-argument wrapper and
    /// armory_my_app_feedback with 0235's bodies as they are, plus auth.uid(), the storage tables
    /// and the armory-feedback-shots bucket with 0235's two policies, for FakeSupabase's Storage.
    /// </summary>
    public static async Task ApplyFeedbackV2Async(ArmoryTestDatabase database)
    {
        await ApplyReportsAsync(database);
        await ApplyAsync(database, "armory_v3_feedback_v2.sql");
    }

    /// <summary>Sets a note's status the way the site admin's console does (new, seen, resolved or spam).</summary>
    public static async Task SetFeedbackStatusAsync(ArmoryTestDatabase database, Guid note, string status)
    {
        await using var connection = await database.OpenAsync();
        await using var command = new NpgsqlCommand("update public.armory_app_feedback set status = $2, reviewed_at = now(), reviewed_by = 'admin' where id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = note });
        command.Parameters.Add(new NpgsqlParameter { Value = status });
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>What armory_purge_folder leaves: the removed files at or under the folder gone with their history, and one folder_purged change.</summary>
    public static async Task<JsonObject> PurgeFolderAsync(ArmoryTestDatabase database, Guid project, string folder, string by)
    {
        await using var connection = await database.OpenAsync();
        await using var command = new NpgsqlCommand("select public.armory_test_purge_folder($1, $2, $3)::text", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = project });
        command.Parameters.Add(new NpgsqlParameter { Value = folder });
        command.Parameters.Add(new NpgsqlParameter { Value = by });
        return (JsonObject)JsonNode.Parse((string)(await command.ExecuteScalarAsync())!)!;
    }

    /// <summary>What armory_purge_project leaves: nothing of the project but its receipt. Returns when.</summary>
    public static async Task<DateTimeOffset> PurgeProjectAsync(ArmoryTestDatabase database, Guid project, string by)
    {
        await using var connection = await database.OpenAsync();
        await using var command = new NpgsqlCommand("select public.armory_test_purge_project($1, $2)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = project });
        command.Parameters.Add(new NpgsqlParameter { Value = by });
        return new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync())!, TimeSpan.Zero);
    }

    private static async Task ApplyAsync(ArmoryTestDatabase database, string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sql-v3", file);
        await using var connection = await database.OpenAsync();
        await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(path), connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }
}
