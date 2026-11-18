using Armory.Client;

namespace Armory.Agent.Engine.Tests;

// 0.6.1: a website refusal shown in the window is a sentence written for the student, never a
// function name, a status code or the database's own words (student note: badly formatted text).
public sealed class RefusalWordsTests
{
    private const string Fallback = "Armory couldn't move this file to Trash. Try again in a moment.";

    private static string Words(string? sqlState, string message, int status = 400)
        => SyncEngine.RefusalWords(new ArmoryRpcException(status, sqlState, message, null, null), Fallback);

    [Fact]
    public void A_sentence_written_for_the_student_is_said_as_it_is()
    {
        Assert.Equal("This file is already in Trash.", Words("P0001", "This file is already in Trash."));
        Assert.Equal("Ana has this file checked out. Ask her to check it in first!", Words("P0001", "  Ana has this file checked out. Ask her to check it in first!  "));
        Assert.Equal("This is Ana's folder. Save your work in your own folder.", Words("PT403", "This is Ana's folder. Save your work in your own folder."));
        Assert.Equal("This folder is someone else's. Save your work in your own folder.", Words("PT403", ""));
    }

    [Theory]
    [InlineData(null, "armory_web_trash failed with 500.")]
    [InlineData("P0001", "file_locked")]
    [InlineData("XX000", "Armory_web_trash failed with 502.")]
    [InlineData("42883", "function public.armory_web_trash(uuid, boolean, uuid) does not exist")]
    [InlineData("23514", "new row for relation \"armory_files\" violates check constraint")]
    [InlineData("P0001", "")]
    [InlineData("P0001", "Permission denied for schema armory.")]
    public void Anything_else_gets_the_actions_own_words(string? sqlState, string message)
        => Assert.Equal(Fallback, Words(sqlState, message));

    [Fact]
    public void Membership_and_permission_refusals_say_what_they_mean()
    {
        Assert.Equal("You're no longer in this project.", Words("P0001", ArmoryRpcException.NotMemberMessage));
        Assert.Equal("You don't have permission to do that in this project.", Words("42501", "permission denied for function armory_web_trash", 403));
    }
}
