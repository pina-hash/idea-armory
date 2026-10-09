using System.Security.Cryptography;
using System.Text;

namespace Armory.Core;

public enum PinCheck { Right, Wrong, Waiting }

// A student's 4-digit PIN on a shared computer (docs/agent/PROFILES.md, "The PIN"): never the
// digits, only a PBKDF2-HMAC-SHA256 hash with a random salt, and the wrong tries and the wait
// they earned. The host keeps the record in the profile's own protected secrets, so the counter
// cannot be reset by editing a file. Iterations are stored per record, so a later version can
// raise them.
public sealed record PinRecord(int Version, byte[] Salt, byte[] Hash, int Iterations, int WrongTries, DateTimeOffset? WaitUntil);

public static class PinHash
{
    // OWASP's figure for PBKDF2-HMAC-SHA256 (Password Storage Cheat Sheet): about 280 ms here.
    public const int Iterations = 600_000, SaltBytes = 16, HashBytes = 32;
    public const int CurrentVersion = 1;

    public static PinRecord Create(string pin, int iterations = Iterations)
    {
        if (!SharedComputer.IsPin(pin)) throw new ArgumentException("A PIN is exactly 4 digits.", nameof(pin));
        if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return new PinRecord(CurrentVersion, salt, Derive(pin, salt, iterations), iterations, 0, null);
    }

    // During a wait even the right PIN is refused (Waiting, and nothing changes). A right PIN
    // clears the wrong tries; a wrong one counts, and from the fifth on earns a wait
    // (SharedComputer.PinWait). next is the record to keep.
    public static PinCheck Check(PinRecord record, string pin, DateTimeOffset now, out PinRecord next)
    {
        next = record;
        if (record.WaitUntil is { } until && now < until) return PinCheck.Waiting;
        var right = SharedComputer.IsPin(pin) && record.Iterations > 0 && record.Salt.Length > 0 && record.Hash.Length == HashBytes &&
            CryptographicOperations.FixedTimeEquals(Derive(pin, record.Salt, record.Iterations), record.Hash);
        if (right)
        {
            next = record with { WrongTries = 0, WaitUntil = null };
            return PinCheck.Right;
        }
        var tries = record.WrongTries == int.MaxValue ? int.MaxValue : record.WrongTries + 1;
        next = record with { WrongTries = tries, WaitUntil = SharedComputer.PinWait(tries) is { } wait ? now + wait : null };
        return PinCheck.Wrong;
    }

    private static byte[] Derive(string pin, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}
