namespace Darkspyre.LocalServices;

/// <summary>The configured store has an exclusive owner; selecting another store is not a recovery action.</summary>
public sealed class StoreInUseException : IOException
{
    /// <summary>Creates the error for a store held by another owner.</summary>
    public StoreInUseException() : base("The configured local service store is already owned.") { }
}
