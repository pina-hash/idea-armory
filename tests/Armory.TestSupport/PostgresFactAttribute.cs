using Xunit;

namespace Armory.TestSupport;

/// <summary>A fact that runs only when ARMORY_TEST_POSTGRES names a throwaway PostgreSQL cluster.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!ArmoryTestDatabase.IsAvailable)
        {
            Skip = "Set ARMORY_TEST_POSTGRES to a throwaway PostgreSQL cluster connection string.";
        }
    }
}
