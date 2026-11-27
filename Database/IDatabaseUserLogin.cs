namespace MultiplayerARPG.MMO
{
    /// <summary>A database server calls these from worker threads, several at once.</summary>
    public interface IDatabaseUserLogin
    {
        string GetHashedPassword(string password);
        bool VerifyPassword(string password, string hashedPassword);
        /// <summary>True if the stored hash uses an outdated scheme and should be replaced after a successful login.</summary>
        bool NeedsRehash(string hashedPassword) => false;
        string GenerateNewId();
    }
}
