using System.Text.Json;
using Armory.Agent.Engine.View;
using Armory.Core;

namespace Armory.Agent;

// A computer shared by several students (docs/agent/PROFILES.md; BRIDGE.md, "Several students
// on one computer"). Each record names exactly the fields bridge.js sends for its type.
internal sealed record PickProfileMessage(string? ProfileId, string? RequestId);
internal sealed record EnterPinMessage(string? ProfileId, string? Pin, string? RequestId);
internal sealed record SetPinMessage(string? ProfileId, string? Pin, string? RequestId);
internal sealed record AddProfileMessage(string? RequestId);
internal sealed record ForgotPinMessage(string? ProfileId, string? RequestId);
// choice: "wait" (for the student whose work is in the shared folder) or "own" (a folder of one's own).
internal sealed record ChooseFolderMessage(string? ProfileId, string? Choice, string? RequestId);
internal sealed record RemoveProfileMessage(string? ProfileId, string? RequestId);
// pin: the first student's PIN when turning on while someone is signed in; "" otherwise.
internal sealed record SetSharedComputerMessage(bool? On, string? Pin, string? RequestId);
internal sealed record SetPinsRequiredMessage(bool? On, string? RequestId);

internal sealed partial class Bridge
{
    private static readonly ActionResult NotAStudent = new(false, "That student isn't on this computer.");
    private static readonly ActionResult NotAPin = new(false, "Type 4 digits.");

    // The picker's messages. A profile id is 32 hex digits and a PIN exactly 4 ASCII digits before
    // the host sees either; a PIN never reaches the log (only message types are logged here, and
    // the Redactor masks a "pin" field as a second guard).
    private async Task HandleProfilesAsync(string type, JsonElement message, long asked)
    {
        switch (type)
        {
            case BridgeMessages.ShowPicker:
                host.ShowPicker(PickerTrigger.SwitchStudent);
                break;
            case BridgeMessages.CancelPicker:
                host.CancelPicker();
                break;
            case BridgeMessages.PickProfile:
                var pick = Read<PickProfileMessage>(message);
                await AnswerAsync(type, 1, asked, pick?.RequestId, ProfileStore.IsId(pick?.ProfileId) ? host.PickProfileAsync(pick!.ProfileId!) : Refuse(NotAStudent));
                break;
            case BridgeMessages.EnterPin:
                var enter = Read<EnterPinMessage>(message);
                await AnswerAsync(type, 1, asked, enter?.RequestId,
                    !ProfileStore.IsId(enter?.ProfileId) ? Refuse(NotAStudent) : !SharedComputer.IsPin(enter!.Pin) ? Refuse(NotAPin) : host.EnterPinAsync(enter.ProfileId!, enter.Pin!));
                break;
            case BridgeMessages.SetPin:
                var set = Read<SetPinMessage>(message);
                await AnswerAsync(type, 1, asked, set?.RequestId,
                    !ProfileStore.IsId(set?.ProfileId) ? Refuse(NotAStudent) : !SharedComputer.IsPin(set!.Pin) ? Refuse(NotAPin) : host.SetPinAsync(set.ProfileId!, set.Pin!));
                break;
            case BridgeMessages.AddProfile:
                var add = Read<AddProfileMessage>(message);
                await AnswerAsync(type, 0, asked, add?.RequestId, host.AddProfileAsync());
                break;
            case BridgeMessages.ForgotPin:
                var forgot = Read<ForgotPinMessage>(message);
                await AnswerAsync(type, 1, asked, forgot?.RequestId, ProfileStore.IsId(forgot?.ProfileId) ? host.ForgotPinAsync(forgot!.ProfileId!) : Refuse(NotAStudent));
                break;
            case BridgeMessages.ChooseFolder:
                var choose = Read<ChooseFolderMessage>(message);
                await AnswerAsync(type, 1, asked, choose?.RequestId,
                    !ProfileStore.IsId(choose?.ProfileId) ? Refuse(NotAStudent)
                    : choose!.Choice is not ("wait" or "own") ? Refuse(new ActionResult(false, "Choose Wait or a folder of your own."))
                    : host.ChooseFolderAsync(choose.ProfileId!, choose.Choice));
                break;
            case BridgeMessages.RemoveProfile:
                var remove = Read<RemoveProfileMessage>(message);
                await AnswerAsync(type, 1, asked, remove?.RequestId, ProfileStore.IsId(remove?.ProfileId) ? host.RemoveProfileAsync(remove!.ProfileId!) : Refuse(NotAStudent));
                break;
            case BridgeMessages.SetSharedComputer:
                var shared = Read<SetSharedComputerMessage>(message);
                await AnswerAsync(type, 0, asked, shared?.RequestId,
                    shared?.On is not { } on ? Refuse(new ActionResult(false, "")) : shared.Pin is { Length: > 0 } pin && !SharedComputer.IsPin(pin) ? Refuse(NotAPin)
                    : host.SetSharedComputerAsync(on, shared.Pin is { Length: > 0 } ? shared.Pin : null));
                break;
            case BridgeMessages.SetPinsRequired:
                var pins = Read<SetPinsRequiredMessage>(message);
                await AnswerAsync(type, 0, asked, pins?.RequestId, pins?.On is { } wanted ? host.SetPinsRequiredAsync(wanted) : Refuse(new ActionResult(false, "")));
                break;
        }
    }
}
