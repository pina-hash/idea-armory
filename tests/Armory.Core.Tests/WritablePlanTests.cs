using Armory.Core;

namespace Armory.Core.Tests;

// "Check out and reopen" with the SolidWorks link: the order of steps that makes a document
// SolidWorks has open read-only editable, and the promise that no step ever closes a document
// or discards a change.
public sealed class WritablePlanTests
{
    private static WritableState Open(bool drawing = false, bool? changed = false) => new(true, true, drawing, changed, false, false);

    [Fact]
    public void In_place_first_and_it_keeps_unsaved_changes()
    {
        Assert.Equal(WritableStep.SetReadOnlyStateFalse, WritablePlan.Next(Open(changed: true)));
        Assert.Equal(WritableStep.SetReadOnlyStateFalse, WritablePlan.Next(Open(changed: null)));
        // In place worked: SolidWorks reports it writable.
        Assert.Equal(WritableStep.Done, WritablePlan.Next(Open(changed: true) with { TriedInPlace = true, ReadOnly = false }));
    }

    [Fact]
    public void A_document_not_open_or_already_writable_needs_nothing()
    {
        Assert.Equal(WritableStep.NotOpen, WritablePlan.Next(Open() with { Open = false }));
        Assert.Equal(WritableStep.Done, WritablePlan.Next(Open() with { ReadOnly = false }));
    }

    [Fact]
    public void A_changed_document_is_never_reloaded()
    {
        Assert.Equal(WritableStep.StopUnsaved, WritablePlan.Next(Open(changed: true) with { TriedInPlace = true }));
        Assert.Equal(WritableStep.StopUnsaved, WritablePlan.Next(Open(drawing: true, changed: true) with { TriedInPlace = true }));
        // Unknown counts as changed.
        Assert.Equal(WritableStep.StopUnsaved, WritablePlan.Next(Open(changed: null) with { TriedInPlace = true }));
    }

    [Fact]
    public void An_unchanged_part_reloads_and_an_unchanged_drawing_closes_and_reopens_without_discarding()
    {
        Assert.Equal(WritableStep.Reload, WritablePlan.Next(Open() with { TriedInPlace = true }));
        Assert.Equal(WritableStep.CloseAndReopenDrawing, WritablePlan.Next(Open(drawing: true) with { TriedInPlace = true }));
        Assert.Equal(WritableStep.Done, WritablePlan.Next(Open() with { TriedInPlace = true, TriedReopen = true, ReadOnly = false }));
        Assert.Equal(WritableStep.Refused, WritablePlan.Next(Open() with { TriedInPlace = true, TriedReopen = true }));
        Assert.Equal(WritableStep.Refused, WritablePlan.Next(Open(drawing: true) with { TriedInPlace = true, TriedReopen = true }));
    }

    // The invariant: over every state there is, no step closes or discards, a changed (or
    // possibly changed) document is never reloaded, and every reopen's arguments keep changes.
    [Fact]
    public void No_state_leads_to_a_step_that_closes_or_discards()
    {
        Assert.DoesNotContain(Enum.GetNames<WritableStep>(), n => n.Contains("Discard", StringComparison.OrdinalIgnoreCase) || n == "CloseDoc" || n == "Close");
        Assert.False(WritablePlan.ReloadDiscardChanges);
        Assert.False(WritablePlan.ReloadReadOnly);
        Assert.Equal(0, WritablePlan.CloseAndReopenOptions & WritablePlan.CloseReopenDiscardChanges);
        Assert.Equal(WritablePlan.CloseReopenMatchSheet, WritablePlan.CloseAndReopenOptions);
        var bools = new[] { false, true };
        var seen = new HashSet<WritableStep>();
        foreach (var open in bools)
        foreach (var readOnly in bools)
        foreach (var drawing in bools)
        foreach (var changed in new bool?[] { false, true, null })
        foreach (var inPlace in bools)
        foreach (var reopen in bools)
        {
            var step = WritablePlan.Next(new WritableState(open, readOnly, drawing, changed, inPlace, reopen));
            seen.Add(step);
            if (step is WritableStep.Reload or WritableStep.CloseAndReopenDrawing)
            {
                Assert.Equal(false, changed);
                Assert.True(inPlace, "a reopen only after in place was tried");
                Assert.Equal(drawing, step == WritableStep.CloseAndReopenDrawing);
            }
            if (!open) Assert.Equal(WritableStep.NotOpen, step);
        }
        Assert.Equal(Enum.GetValues<WritableStep>().ToHashSet(), seen);
    }

    // Carried out step by step as the link does, a plan always ends, in at most four steps.
    [Fact]
    public void Every_plan_ends_within_four_steps()
    {
        foreach (var drawing in new[] { false, true })
        foreach (var changed in new bool?[] { false, true, null })
        foreach (var inPlaceWorks in new[] { false, true })
        foreach (var reopenWorks in new[] { false, true })
        {
            var state = Open(drawing, changed);
            var steps = 0;
            WritableStep step;
            while ((step = WritablePlan.Next(state)) is not (WritableStep.Done or WritableStep.StopUnsaved or WritableStep.Refused or WritableStep.NotOpen))
            {
                Assert.True(++steps <= 4);
                state = step switch
                {
                    WritableStep.SetReadOnlyStateFalse => state with { TriedInPlace = true, ReadOnly = !inPlaceWorks },
                    _ => state with { TriedReopen = true, ReadOnly = !reopenWorks },
                };
            }
            if (inPlaceWorks) Assert.Equal(WritableStep.Done, step);
            else if (changed != false) Assert.Equal(WritableStep.StopUnsaved, step);
            else Assert.Equal(reopenWorks ? WritableStep.Done : WritableStep.Refused, step);
        }
    }
}
